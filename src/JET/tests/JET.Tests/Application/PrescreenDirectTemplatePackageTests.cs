using System.IO.Compression;
using System.Text.Json;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class PrescreenDirectTemplatePackageTests
{
    [Theory]
    [InlineData(9_999, true)]
    [InlineData(10_000, false)]
    public async Task RowBoundary_ControlsDetailReadAndPreservesTemplateSummary(
        long plannedRowCount,
        bool expectedDetailSheet)
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportArtifactExportFixture.SetupProjectAsync(host);
        var database = new SqliteProjectDatabase(
            new JetProjectFolder(host.ProjectsRoot));
        var pages = new TrackingPrescreenPageRepository(
            new LocalPrescreenPageRepository(database),
            throwOnPageRead: !expectedDetailSheet);
        var writer = CreateWriter(database, pages);
        var projection = Projection(plannedRowCount);
        var plan = Plan(projectId, projection, plannedRowCount);
        await using var output = new MemoryStream();

        await ((IPlannedPrescreenReportWriter)writer).WritePlannedAsync(
            output,
            Context(projectId),
            projection,
            plan,
            "tester",
            CancellationToken.None);

        var bytes = output.ToArray();
        WriteReceiptArtifactIfRequested(
            $"prescreen-{plannedRowCount}.xlsx",
            bytes);
        var templateBytes = await TemplateBytesAsync(
            ReportTemplateCatalog.Prescreen);
        AssertDirectPackageAllowedDiff(
            templateBytes,
            bytes,
            "xl/worksheets/sheet1.xml");
        using (var workbook = new XLWorkbook(
                   new MemoryStream(bytes, writable: false)))
        {
            Assert.Equal(
                expectedDetailSheet,
                workbook.Worksheets.Any(sheet => sheet.Name == "R1"));
            Assert.Equal(
                expectedDetailSheet ? 1 : 0,
                pages.PageCalls);
            var summary = workbook.Worksheet("Pre-screening_Report");
            Assert.Equal(
                "# 1. 於期末財務報表準備期間核准並入到查核年度總帳之分錄",
                summary.Cell("B8").GetString());
            Assert.DoesNotContain(
                "xxxx/xx/xx",
                summary.Cell("C8").GetString(),
                StringComparison.Ordinal);
            Assert.Equal(
                new[] { "A2", "A3", "A4" },
                summary.Range("B14:B16").Cells()
                    .Select(cell => cell.GetString())
                    .ToArray());
            Assert.Equal(
                "# 分錄無摘要描述(即空白摘要)",
                summary.Cell("B17").GetString());
            Assert.Equal(1, summary.Cell("E8").GetValue<long>());
            Assert.Equal(plannedRowCount, summary.Cell("F8").GetValue<long>());
            Assert.Equal(
                expectedDetailSheet
                    ? $"符合 {plannedRowCount:N0} 筆"
                    : "明細筆數超過10,000筆，明細資料不匯出",
                summary.Cell("D8").GetString());
            Assert.All(
                summary.Range("E12:F16").Cells(),
                cell => Assert.Equal("N/A", cell.GetString()));
            Assert.True(summary.Cell("C8").Style.Alignment.WrapText);
            if (expectedDetailSheet)
            {
                var detail = workbook.Worksheet("R1");
                AssertUnwrapped(detail.Cell("A1"));
                AssertUnwrapped(detail.Cell("A2"));
                Assert.NotEqual(18D, detail.Column(1).Width);
            }
        }

        using var template = SpreadsheetDocument.Open(
            new MemoryStream(templateBytes, writable: false),
            false);
        using var actual = SpreadsheetDocument.Open(
            new MemoryStream(bytes, writable: false),
            false);
        var templateSummary = WorksheetPartFor(
            template,
            "Pre-screening_Report");
        var actualSummary = WorksheetPartFor(
            actual,
            "Pre-screening_Report");
        Assert.Equal(
            Assert.Single(templateSummary.Worksheet.Elements<Drawing>())
                .OuterXml,
            Assert.Single(actualSummary.Worksheet.Elements<Drawing>())
                .OuterXml);
        var appearanceMismatches = new List<string>();
        if (!ColumnAndCellsAreWrapped(
                actual,
                actualSummary.Worksheet,
                columnIndex: 3))
        {
            appearanceMismatches.Add(
                "prescreen-summary-description-wrap: Pre-screening_Report!column C alignment.wrapText");
        }
        if (expectedDetailSheet
            && !ColumnsAreAutoFit(
                WorksheetPartFor(actual, "R1").Worksheet,
                first: 1,
                last: 20))
        {
            appearanceMismatches.Add(
                "prescreen-detail-autofit: R1!columns 1-20 column.autoFit");
        }
        Assert.True(
            appearanceMismatches.Count == 0,
            "Stage 8 Track O Pre-screening registry diff:\n"
            + string.Join("\n", appearanceMismatches));
        Assert.Empty(new OpenXmlValidator().Validate(actual));
    }

    [Fact]
    public async Task AggregateSheets_AutoFitAllRowsReuseWidthsAcrossContinuationsAndPreserveNumericStyles()
    {
        const int rowCount = 17;
        var longCreator = new string('編', 40);
        var longAccountName = new string('科', 40);
        var creators = Enumerable.Range(0, rowCount)
            .Select(index => new CreatorSummaryExportRow(
                index == rowCount - 1 ? longCreator : $"creator-{index + 1}",
                index == rowCount - 1 ? long.MaxValue : index + 1,
                index == rowCount - 1 ? long.MaxValue : 10_000,
                index == rowCount - 1 ? long.MinValue : -10_000))
            .ToArray();
        var accounts = Enumerable.Range(0, rowCount)
            .Select(index => new AccountUsageExportRow(
                $"ACCOUNT-{index + 1}",
                index == rowCount - 1 ? longAccountName : $"account-{index + 1}",
                index == rowCount - 1 ? long.MaxValue : index + 1,
                index == rowCount - 1 ? long.MaxValue : 10_000,
                index == rowCount - 1 ? long.MinValue : -10_000))
            .ToArray();
        var database = new SqliteProjectDatabase(
            new JetProjectFolder(Path.GetTempPath()));
        // R6 標題沿用 committed GL mapping 的來源欄名（欄位來源忠實性），
        // writer 層 fixture 因此需要既有 mapping state。
        await new LocalMappingStateStore(database).SaveAsync(
            "aggregate-width-project",
            new CommittedMapping(
                DatasetKind.Gl,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [GlMappingKeys.AccNum] = "科目代號",
                    [GlMappingKeys.AccName] = "科目名稱"
                },
                "flag",
                "aggregate-width-batch",
                DateTimeOffset.UtcNow),
            CancellationToken.None);
        var writer = CreateWriter(
            database,
            new LocalPrescreenPageRepository(database),
            new LegacyReportWriterOptions(17),
            new FixedCreatorSummaryRepository(creators),
            new FixedAccountUsageRepository(accounts));
        var projection = Projection(0);
        var plan = Plan("aggregate-width-project", projection, 0);
        await using var output = new MemoryStream();

        await ((IPlannedPrescreenReportWriter)writer).WritePlannedAsync(
            output,
            Context("aggregate-width-project"),
            projection,
            plan,
            "tester",
            CancellationToken.None);

        using var workbook = new XLWorkbook(
            new MemoryStream(output.ToArray(), writable: false));
        var r5 = workbook.Worksheet("R5");
        var r5Continuation = workbook.Worksheet("R5 (2)");
        var r6 = workbook.Worksheet("R6");
        var r6Continuation = workbook.Worksheet("R6 (2)");

        for (var column = 1; column <= 4; column++)
        {
            Assert.Equal(
                r5.Column(column).Width,
                r5Continuation.Column(column).Width,
                6);
        }
        for (var column = 1; column <= 5; column++)
        {
            Assert.Equal(
                r6.Column(column).Width,
                r6Continuation.Column(column).Width,
                6);
        }

        Assert.InRange(r5.Column(1).Width, 92.5, 255);
        Assert.InRange(r5.Column(2).Width, 29.5, 255);
        Assert.InRange(r5.Column(3).Width, 28.5, 255);
        Assert.InRange(r5.Column(4).Width, 29.5, 255);
        Assert.InRange(r6.Column(2).Width, 92.5, 255);
        Assert.InRange(r6.Column(3).Width, 29.5, 255);
        Assert.InRange(r6.Column(4).Width, 28.5, 255);
        Assert.InRange(r6.Column(5).Width, 29.5, 255);

        AssertUnwrapped(r5.Cell("A1"));
        AssertUnwrapped(r5.Cell("A2"));
        AssertUnwrapped(r5Continuation.Cell("A1"));
        AssertUnwrapped(r5Continuation.Cell("A2"));
        AssertUnwrapped(r6.Cell("A1"));
        AssertUnwrapped(r6.Cell("B2"));
        AssertUnwrapped(r6Continuation.Cell("A1"));
        AssertUnwrapped(r6Continuation.Cell("B2"));
        Assert.Equal(
            "#,##0.0000;[Red]-#,##0.0000",
            r5.Cell("C2").Style.NumberFormat.Format);
        Assert.Equal(
            "#,##0.0000;[Red]-#,##0.0000",
            r6.Cell("D2").Style.NumberFormat.Format);
    }

    [Fact]
    public async Task DirectTemplateWriters_HonorPreCanceledToken()
    {
        using var host = new HandlerTestHost();
        const string projectId = "cancelled-direct-template-project";
        var database = new SqliteProjectDatabase(
            new JetProjectFolder(host.ProjectsRoot));
        var writer = CreateWriter(
            database,
            new LocalPrescreenPageRepository(database));
        var projection = Projection(1);
        var plan = Plan(projectId, projection, 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await using (var prescreenOutput = new MemoryStream())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ((IPlannedPrescreenReportWriter)writer).WritePlannedAsync(
                    prescreenOutput,
                    Context(projectId),
                    projection,
                    plan,
                    "tester",
                    cancellation.Token));
        }

        await using var criteriaOutput = new MemoryStream();
        var criteriaContext = new CriteriaSelectionReportContext(
            new ReportDocumentContext(
                projectId,
                "取消驗收公司",
                "2025-01-01",
                "2025-12-31",
                null,
                10_000),
            "revision",
            new DateTimeOffset(2025, 12, 31, 10, 30, 0, TimeSpan.Zero),
            [],
            new Dictionary<int, string>());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ((ITypedCriteriaSelectionReportWriter)writer).WriteTypedAsync(
                criteriaOutput,
                criteriaContext,
                "tester",
                cancellation.Token));
    }

    [Fact]
    public async Task Criteria_MultipleScenariosExpandWholeVouchersAndRepeatHeadersOnContinuation()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder =>
            {
                builder.WithColumns(
                    "傳票號碼",
                    "傳票日期",
                    "核准日期",
                    "科目代號",
                    "科目名稱",
                    "摘要",
                    "金額",
                    "借方旗標");
                for (var index = 1; index <= 9; index++)
                {
                    builder.AddRow(
                        $"JV-{index:000}",
                        "2025-03-05",
                        "2025-03-06",
                        "1101",
                        "現金",
                        index == 9
                            ? $"調整{new string('寬', 40)}"
                            : $"調整列 {index}",
                        "100.00",
                        1);
                    builder.AddRow(
                        $"JV-{index:000}",
                        "2025-03-05",
                        "2025-03-06",
                        "4101",
                        "收入",
                        $"正常列 {index}",
                        "100.00",
                        0);
                }
            },
            validateForDownstream: true);
        var committed = await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                scenarios = new object[]
                {
                    KeywordScenario("調整情境", "調整"),
                    KeywordScenario("正常情境", "正常")
                }
            }));
        var revision = committed.GetProperty("resultRef")
            .GetProperty("revision")
            .GetString()!;
        var database = new SqliteProjectDatabase(
            new JetProjectFolder(host.ProjectsRoot));
        var scenarios = await new LocalFilterScenarioStore(database)
            .ListAsync(projectId, CancellationToken.None);
        var writer = CreateWriter(
            database,
            new LocalPrescreenPageRepository(database),
            new LegacyReportWriterOptions(17));
        var context = new CriteriaSelectionReportContext(
            new ReportDocumentContext(
                projectId,
                "Criteria 續頁公司",
                "2025-01-01",
                "2025-12-31",
                null,
                10_000),
            revision,
            scenarios[0].SavedUtc,
            scenarios,
            new Dictionary<int, string>
            {
                [1] = "摘要包含「調整」",
                [2] = "摘要包含「正常」"
            });
        await using var output = new MemoryStream();

        await ((ITypedCriteriaSelectionReportWriter)writer).WriteTypedAsync(
            output,
            context,
            "tester",
            CancellationToken.None);

        var bytes = output.ToArray();
        WriteReceiptArtifactIfRequested(
            "criteria-multiple-scenarios-continuation.xlsx",
            bytes);
        var templateBytes = await TemplateBytesAsync(
            ReportTemplateCatalog.CriteriaSelection);
        AssertDirectPackageAllowedDiff(
            templateBytes,
            bytes,
            "xl/worksheets/sheet1.xml");
        using (var workbook = new XLWorkbook(
                   new MemoryStream(bytes, writable: false)))
        {
            Assert.Equal(
                new[]
                {
                    "Summary Inforamtion",
                    "#Criteria Select 1",
                    "#Criteria Select 1 (2)",
                    "#Criteria Select 2",
                    "#Criteria Select 2 (2)"
                },
                workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
            var summary = workbook.Worksheet("Summary Inforamtion");
            Assert.All(
                summary.Range("A3:D3").Cells(),
                cell => Assert.True(cell.IsEmpty()));
            Assert.True(summary.Cell("A4").IsEmpty());
            Assert.Equal("條件的內容", summary.Cell("B4").GetString());
            Assert.True(summary.Cell("C4").IsEmpty());
            Assert.True(summary.Cell("D4").IsEmpty());
            Assert.Equal(9, summary.Cell("C5").GetValue<long>());
            Assert.Equal(9, summary.Cell("D5").GetValue<long>());
            Assert.Equal(9, summary.Cell("C6").GetValue<long>());
            Assert.Equal(9, summary.Cell("D6").GetValue<long>());

            foreach (var position in new[] { 1, 2 })
            {
                var first = workbook.Worksheet(
                    $"#Criteria Select {position}");
                var second = workbook.Worksheet(
                    $"#Criteria Select {position} (2)");
                Assert.Equal(
                    OriginalHeaders,
                    ReadRow(first, 1));
                Assert.Equal(
                    OriginalHeaders,
                    ReadRow(second, 1));
                Assert.Equal(17, first.LastRowUsed()!.RowNumber());
                Assert.Equal(3, second.LastRowUsed()!.RowNumber());
                var documents = first.Range("A2:A17").Cells()
                    .Concat(second.Range("A2:A3").Cells())
                    .Select(cell => cell.GetString())
                    .ToArray();
                Assert.Equal(
                    Enumerable.Range(1, 9)
                        .SelectMany(index => new[]
                        {
                            $"JV-{index:000}",
                            $"JV-{index:000}"
                        })
                        .ToArray(),
                    documents);
                Assert.Equal(first.Column(6).Width, second.Column(6).Width, 6);
                Assert.InRange(first.Column(6).Width, 94, 255);
                AssertUnwrapped(first.Cell("A1"));
                AssertUnwrapped(first.Cell("A2"));
                AssertUnwrapped(second.Cell("F2"));
            }
        }

        using var document = SpreadsheetDocument.Open(
            new MemoryStream(bytes, writable: false),
            false);
        Assert.Empty(new OpenXmlValidator().Validate(document));
        Assert.Single(
            WorksheetPartFor(document, "Summary Inforamtion")
                .Worksheet.Elements<Drawing>());
        var appearanceMismatches = new List<string>();
        var summaryWorksheet = WorksheetPartFor(
            document,
            "Summary Inforamtion").Worksheet;
        if (!RowsAreAutoFit(summaryWorksheet))
        {
            appearanceMismatches.Add(
                "criteria-summary-row-autofit: Summary Inforamtion!all rows row.autoFit");
        }
        foreach (var sheetName in document.WorkbookPart!.Workbook.Sheets!
                     .Elements<Sheet>()
                     .Select(sheet => sheet.Name?.Value!)
                     .Where(name => name.StartsWith(
                         "#Criteria Select",
                         StringComparison.Ordinal)))
        {
            Assert.Empty(
                WorksheetPartFor(document, sheetName)
                    .Worksheet.Elements<Drawing>());
            if (!ColumnsAreAutoFit(
                    WorksheetPartFor(document, sheetName).Worksheet,
                    first: 1,
                    last: 20))
            {
                appearanceMismatches.Add(
                    $"criteria-detail-autofit: {sheetName}!columns 1-20 column.autoFit");
            }
        }
        Assert.True(
            appearanceMismatches.Count == 0,
            "Stage 8 Track O Criteria registry diff:\n"
            + string.Join("\n", appearanceMismatches));

        // AuditCore 只會把舊 IDEA 的單一「未預期借貸組合」位置放入此集合。
        // Writer 必須只改該位置的 D 欄，不能把所有情境都改成整張傳票列數。
        await using var wholeVoucherSummaryOutput = new MemoryStream();
        await ((ITypedCriteriaSelectionReportWriter)writer).WriteTypedAsync(
            wholeVoucherSummaryOutput,
            context with
            {
                WholeVoucherSummaryPositions = new HashSet<int> { 1 }
            },
            "tester",
            CancellationToken.None);
        using var wholeVoucherSummaryWorkbook = new XLWorkbook(
            new MemoryStream(wholeVoucherSummaryOutput.ToArray(), writable: false));
        var scopedSummary = wholeVoucherSummaryWorkbook.Worksheet("Summary Inforamtion");
        Assert.Equal(18, scopedSummary.Cell("D5").GetValue<long>());
        Assert.Equal(9, scopedSummary.Cell("D6").GetValue<long>());
    }

    [Fact]
    public async Task Stage9_CriteriaSevenThroughTen_ProjectTheClosedExportDatabaseAppearanceFamily()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder =>
            {
                builder.WithColumns(
                    "傳票號碼",
                    "傳票日期",
                    "核准日期",
                    "科目代號",
                    "科目名稱",
                    "摘要",
                    "金額",
                    "借方旗標");
                builder.AddRow(
                    "SYN-001",
                    "2025-03-05",
                    "2025-03-06",
                    "SYN-A",
                    "Synthetic account",
                    "Stage9 family marker",
                    "100.00",
                    1);
                builder.AddRow(
                    "SYN-001",
                    "2025-03-05",
                    "2025-03-06",
                    "SYN-B",
                    "Synthetic account",
                    "Stage9 family marker",
                    "100.00",
                    0);
            },
            validateForDownstream: true);
        var committed = await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                scenarios = Enumerable.Range(1, 10)
                    .Select(position => KeywordScenario(
                        $"Stage9 family {position}",
                        "Stage9 family marker"))
                    .ToArray()
            }));
        var revision = committed.GetProperty("resultRef")
            .GetProperty("revision")
            .GetString()!;
        var database = new SqliteProjectDatabase(
            new JetProjectFolder(host.ProjectsRoot));
        var scenarios = await new LocalFilterScenarioStore(database)
            .ListAsync(projectId, CancellationToken.None);
        var writer = CreateWriter(
            database,
            new LocalPrescreenPageRepository(database),
            new LegacyReportWriterOptions(17));
        var context = new CriteriaSelectionReportContext(
            new ReportDocumentContext(
                projectId,
                "Stage9 synthetic",
                "2025-01-01",
                "2025-12-31",
                null,
                10_000),
            revision,
            scenarios[0].SavedUtc,
            scenarios,
            Enumerable.Range(1, 10).ToDictionary(
                position => position,
                position => $"Stage9 family {position}"));
        await using var output = new MemoryStream();

        await ((ITypedCriteriaSelectionReportWriter)writer).WriteTypedAsync(
            output,
            context,
            "tester",
            CancellationToken.None);

        using var workbook = new XLWorkbook(
            new MemoryStream(output.ToArray(), writable: false));
        Assert.Equal(
            new[] { "Summary Inforamtion" }
                .Concat(Enumerable.Range(1, 10).Select(position =>
                    $"#Criteria Select {position}")),
            workbook.Worksheets.Select(sheet => sheet.Name));
        foreach (var position in Enumerable.Range(7, 4))
        {
            var sheet = workbook.Worksheet($"#Criteria Select {position}");
            Assert.All(new[] { sheet.Cell("A1"), sheet.Cell("A2") }, cell =>
            {
                Assert.Equal("微軟正黑體", cell.Style.Font.FontName);
                Assert.Equal(10D, cell.Style.Font.FontSize);
                Assert.False(cell.Style.Font.Bold);
                Assert.Equal(XLFillPatternValues.None, cell.Style.Fill.PatternType);
                Assert.Equal(XLAlignmentVerticalValues.Bottom, cell.Style.Alignment.Vertical);
                Assert.Equal(XLBorderStyleValues.None, cell.Style.Border.LeftBorder);
                Assert.Equal(XLBorderStyleValues.None, cell.Style.Border.RightBorder);
                Assert.Equal(XLBorderStyleValues.None, cell.Style.Border.TopBorder);
                Assert.Equal(XLBorderStyleValues.None, cell.Style.Border.BottomBorder);
            });
            Assert.Equal(
                sheet.Cell("A1").Style.NumberFormat.Format,
                sheet.Cell("A2").Style.NumberFormat.Format);
        }
    }

    [SqlServerFact]
    public async Task Criteria_OutputIdentityCountsAndOrder_AreEquivalentAcrossThreeProviders()
    {
        var connectionString =
            await TempSqlServerProject.ProbeConnectionStringAsync();
        Assert.NotNull(connectionString);

        var sqlite = await ExportCriteriaOracleAsync(
            "sqlite",
            connectionString: null);
        var duckDb = await ExportCriteriaOracleAsync(
            "duckdb",
            connectionString: null);
        var sqlServer = await ExportCriteriaOracleAsync(
            "sqlServer",
            connectionString);

        Assert.Equal(2, sqlite.VoucherHitCount);
        Assert.Equal(2, sqlite.RowHitCount);
        Assert.Equal(4, sqlite.DetailRows.Count);
        Assert.Equal(
            new[]
            {
                "JV-001|風險摘要 1",
                "JV-001|一般摘要 1",
                "JV-002|風險摘要 2",
                "JV-002|一般摘要 2"
            },
            sqlite.DetailRows);
        AssertProviderOracleEqual(sqlite, duckDb);
        AssertProviderOracleEqual(sqlite, sqlServer);
    }

    private static LegacyReportWriter CreateWriter(
        SqliteProjectDatabase database,
        IPrescreenPageRepository pages) =>
        new(
            new LocalCompletenessAccountPageRepository(database),
            new LocalCompletenessDiffPageRepository(database),
            new LocalUnbalancedGlEntryPageRepository(database),
            new LocalNullRecordsPageRepository(database),
            new LocalInfSamplePageRepository(database),
            pages,
            new LocalRawGlExportRepository(database),
            new LocalImportRepository(database),
            new LocalMappingStateStore(database),
            new LocalCreatorSummaryExportRepository(database),
            new LocalAccountUsageExportRepository(database),
            new LocalTagMatrixScenariosRepository(database),
            new LocalTagMatrixRowPageRepository(database));

    private static LegacyReportWriter CreateWriter(
        SqliteProjectDatabase database,
        IPrescreenPageRepository pages,
        LegacyReportWriterOptions options) =>
        CreateWriter(
            database,
            pages,
            options,
            new LocalCreatorSummaryExportRepository(database),
            new LocalAccountUsageExportRepository(database));

    private static LegacyReportWriter CreateWriter(
        SqliteProjectDatabase database,
        IPrescreenPageRepository pages,
        LegacyReportWriterOptions options,
        ICreatorSummaryExportRepository creatorSummaries,
        IAccountUsageExportRepository accountUsage) =>
        new(
            new LocalCompletenessAccountPageRepository(database),
            new LocalCompletenessDiffPageRepository(database),
            new LocalUnbalancedGlEntryPageRepository(database),
            new LocalNullRecordsPageRepository(database),
            new LocalInfSamplePageRepository(database),
            pages,
            new LocalRawGlExportRepository(database),
            new LocalImportRepository(database),
            new LocalMappingStateStore(database),
            creatorSummaries,
            accountUsage,
            new LocalTagMatrixScenariosRepository(database),
            new LocalTagMatrixRowPageRepository(database),
            options);

    private static object KeywordScenario(string name, string keyword) => new
    {
        name,
        rationale = $"測試 {name}",
        groups = new[]
        {
            new
            {
                join = "AND",
                rules = new[]
                {
                    new
                    {
                        join = "AND",
                        type = "customKeywords",
                        keywords = keyword
                    }
                }
            }
        }
    };

    private static readonly string[] OriginalHeaders =
    [
        "傳票號碼",
        "傳票日期",
        "核准日期",
        "科目代號",
        "科目名稱",
        "摘要",
        "金額",
        "借方旗標"
    ];

    private static string[] ReadRow(IXLWorksheet sheet, int row) =>
        Enumerable.Range(1, OriginalHeaders.Length)
            .Select(column => sheet.Cell(row, column).GetString())
            .ToArray();

    private static void AssertUnwrapped(IXLCell cell)
    {
        Assert.False(cell.Style.Alignment.WrapText);
        Assert.Equal(0, cell.Style.Alignment.Indent);
        Assert.False(cell.Style.Alignment.ShrinkToFit);
    }

    private static async Task<CriteriaProviderOracle> ExportCriteriaOracleAsync(
        string provider,
        string? connectionString)
    {
        using var host = new HandlerTestHost(
            sqlServerConnectionString: connectionString);
        string? projectId = null;
        try
        {
            projectId = await InlineWorkbookProject.SetupAsync(
                host,
                builder => builder
                    .WithColumns(OriginalHeaders)
                    .AddRow(
                        "JV-001",
                        "2025-03-05",
                        "2025-03-06",
                        "1101",
                        "現金",
                        "風險摘要 1",
                        "100.00",
                        1)
                    .AddRow(
                        "JV-001",
                        "2025-03-05",
                        "2025-03-06",
                        "4101",
                        "收入",
                        "一般摘要 1",
                        "100.00",
                        0)
                    .AddRow(
                        "JV-002",
                        "2025-03-06",
                        "2025-03-07",
                        "1101",
                        "現金",
                        "風險摘要 2",
                        "200.00",
                        1)
                    .AddRow(
                        "JV-002",
                        "2025-03-06",
                        "2025-03-07",
                        "4101",
                        "收入",
                        "一般摘要 2",
                        "200.00",
                        0),
                databaseProvider: provider,
                configureTb: tb => tb
                    .AddRow("1101", "現金", 300)
                    .AddRow("4101", "收入", -300));
            var validation = await host.DispatchAsync("validate.run");
            var prescreen = await host.DispatchAsync("prescreen.run");
            var committed = await host.DispatchAsync(
                "filter.commit",
                JsonSerializer.Serialize(new
                {
                    scenarios = new[]
                    {
                        KeywordScenario("命中摘要", "風險")
                    }
                }));
            var response = await host.DispatchAsync(
                "export.criteriaSelectionReport",
                JsonSerializer.Serialize(new
                {
                    validationRunId = validation.GetProperty("resultRef")
                        .GetProperty("runId")
                        .GetString(),
                    prescreenRunId = prescreen.GetProperty("resultRef")
                        .GetProperty("runId")
                        .GetString(),
                    revision = committed.GetProperty("resultRef")
                        .GetProperty("revision")
                        .GetString()
                }));
            var fileName = response.GetProperty("artifact")
                .GetProperty("fileName")
                .GetString()!;
            using var workbook = new XLWorkbook(Path.Combine(
                host.ProjectsRoot,
                projectId,
                fileName));
            var summary = workbook.Worksheet("Summary Inforamtion");
            var detail = workbook.Worksheet("#Criteria Select 1");
            var detailRows = Enumerable.Range(
                    2,
                    detail.LastRowUsed()!.RowNumber() - 1)
                .Select(row =>
                    $"{detail.Cell(row, 1).GetString()}|"
                    + detail.Cell(row, 6).GetString())
                .ToArray();
            return new CriteriaProviderOracle(
                summary.Cell("C5").GetValue<long>(),
                summary.Cell("D5").GetValue<long>(),
                detailRows);
        }
        finally
        {
            if (projectId is not null)
            {
                await host.DispatchAsync(
                    "project.delete",
                    JsonSerializer.Serialize(new { projectId }));
            }
        }
    }

    private sealed record CriteriaProviderOracle(
        long VoucherHitCount,
        long RowHitCount,
        IReadOnlyList<string> DetailRows);

    private static void AssertProviderOracleEqual(
        CriteriaProviderOracle expected,
        CriteriaProviderOracle actual)
    {
        Assert.Equal(expected.VoucherHitCount, actual.VoucherHitCount);
        Assert.Equal(expected.RowHitCount, actual.RowHitCount);
        Assert.Equal(expected.DetailRows, actual.DetailRows);
    }

    private static PrescreenReportProjection Projection(long rowCount)
    {
        var notApplicable = new PrescreenReportRuleProjection(
            "na",
            "測試未執行");
        return new PrescreenReportProjection(
            PostPeriodApproval: new("V", null, rowCount),
            SuspiciousKeywords: notApplicable,
            UnexpectedAccountPair: notApplicable,
            TrailingZeros: notApplicable,
            CreatorSummary: new("V", null, 1),
            RareAccounts: new("V", null, 2),
            BlankDescription: notApplicable);
    }

    private static PrescreenReportPlan Plan(
        string projectId,
        PrescreenReportProjection projection,
        long rowCount)
    {
        var unfinalized = JetAuditProgram.Plan(new PrescreenReportRequest(
            projectId,
            new FilterRuleContext(
                10_000,
                "2025-12-31",
                "2025-01-01",
                "2025-12-31"),
            projection.PostPeriodApproval.NaReason,
            projection.SuspiciousKeywords.NaReason,
            projection.UnexpectedAccountPair.NaReason,
            projection.TrailingZeros.NaReason,
            projection.BlankDescription.NaReason));
        return JetAuditProgram.Finalize(
            unfinalized,
            new PrescreenReportPlanningFacts(
                new Dictionary<PrescreenReportDetailKind, PrescreenHitCounts>
                {
                    [PrescreenReportDetailKind.PostPeriodApproval] =
                        new(VoucherHitCount: 1, RowHitCount: rowCount)
                }));
    }

    private static PrescreenReportContext Context(string projectId) => new(
        new ReportDocumentContext(
            projectId,
            "範本驗收公司",
            "2025-01-01",
            "2025-12-31",
            "2025-12-31",
            10_000),
        "prescreen-run-must-not-be-visible",
        new DateTimeOffset(2025, 12, 31, 10, 30, 0, TimeSpan.Zero),
        "{}");

    private static async Task<byte[]> TemplateBytesAsync(string templateName)
    {
        await using var stream = new MemoryStream();
        await ReportTemplateCatalog.Default.CopyToAsync(
            templateName,
            stream,
            CancellationToken.None);
        return stream.ToArray();
    }

    private static void AssertDirectPackageAllowedDiff(
        byte[] templateBytes,
        byte[] outputBytes,
        string summaryPart)
    {
        using var templateStream = new MemoryStream(
            templateBytes,
            writable: false);
        using var outputStream = new MemoryStream(
            outputBytes,
            writable: false);
        using var template = new ZipArchive(
            templateStream,
            ZipArchiveMode.Read);
        using var output = new ZipArchive(
            outputStream,
            ZipArchiveMode.Read);
        var templateParts = template.Entries
            .ToDictionary(
                entry => entry.FullName,
                ReadEntryBytes,
                StringComparer.Ordinal);
        var outputParts = output.Entries
            .ToDictionary(
                entry => entry.FullName,
                ReadEntryBytes,
                StringComparer.Ordinal);
        Assert.Empty(templateParts.Keys.Except(
            outputParts.Keys,
            StringComparer.Ordinal));

        var added = outputParts.Keys
            .Except(templateParts.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(added);
        Assert.All(
            added,
            part => Assert.Matches(
                "^xl/worksheets/sheet[0-9]+[.]xml$",
                part));

        var changed = templateParts.Keys
            .Intersect(outputParts.Keys, StringComparer.Ordinal)
            .Where(part => !templateParts[part]
                .SequenceEqual(outputParts[part]))
            .ToHashSet(StringComparer.Ordinal);
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "[Content_Types].xml",
            "xl/workbook.xml",
            "xl/_rels/workbook.xml.rels",
            "xl/sharedStrings.xml",
            "xl/styles.xml",
            summaryPart
        };
        Assert.True(
            changed.IsSubsetOf(allowed),
            $"Unexpected changed package parts: {string.Join(", ", changed.Except(allowed))}");

        foreach (var protectedPart in templateParts.Keys.Where(part =>
                     part.StartsWith("xl/drawings/", StringComparison.Ordinal)
                     || part.StartsWith("xl/media/", StringComparison.Ordinal)
                     || part.StartsWith(
                         "xl/printerSettings/",
                         StringComparison.Ordinal)
                     || string.Equals(
                         part,
                         "xl/worksheets/_rels/sheet1.xml.rels",
                         StringComparison.Ordinal)))
        {
            Assert.True(
                outputParts.TryGetValue(protectedPart, out var actual)
                && templateParts[protectedPart].SequenceEqual(actual),
                $"Template-owned relationship closure changed: {protectedPart}");
        }
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static void WriteReceiptArtifactIfRequested(
        string fileName,
        byte[] bytes)
    {
        var receiptDirectory = Environment.GetEnvironmentVariable(
            "JET_PRESCREEN_CRITERIA_RECEIPT_DIR");
        if (string.IsNullOrWhiteSpace(receiptDirectory))
        {
            return;
        }

        Directory.CreateDirectory(receiptDirectory);
        File.WriteAllBytes(
            Path.Combine(receiptDirectory, fileName),
            bytes);
    }

    private static WorksheetPart WorksheetPartFor(
        SpreadsheetDocument document,
        string sheetName)
    {
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var sheet = workbookPart.Workbook.Sheets!.Elements<Sheet>()
            .Single(item => string.Equals(
                item.Name?.Value,
                sheetName,
                StringComparison.Ordinal));
        return Assert.IsType<WorksheetPart>(
            workbookPart.GetPartById(sheet.Id!.Value!));
    }

    private static bool ColumnAndCellsAreWrapped(
        SpreadsheetDocument document,
        Worksheet worksheet,
        uint columnIndex)
    {
        var column = worksheet.GetFirstChild<Columns>()?
            .Elements<Column>()
            .SingleOrDefault(item =>
                item.Min?.Value <= columnIndex
                && item.Max?.Value >= columnIndex);
        if (column?.Style is null
            || !StyleHasWrapText(document, column.Style.Value))
        {
            return false;
        }

        return worksheet.Descendants<Cell>()
            .Where(cell => CellColumnIndex(cell.CellReference?.Value) == columnIndex)
            .All(cell => StyleHasWrapText(
                document,
                cell.StyleIndex?.Value ?? column.Style.Value));
    }

    private static bool ColumnsAreAutoFit(
        Worksheet worksheet,
        uint first,
        uint last)
    {
        var columns = worksheet.GetFirstChild<Columns>();
        if (columns is null)
        {
            return false;
        }

        for (var index = first; index <= last; index++)
        {
            var column = columns.Elements<Column>().SingleOrDefault(item =>
                item.Min?.Value <= index && item.Max?.Value >= index);
            if (column is null
                || column.Width is null
                || column.BestFit?.Value != true
                || column.CustomWidth?.Value != true)
            {
                return false;
            }
        }

        return true;
    }

    private static bool RowsAreAutoFit(Worksheet worksheet)
    {
        var rows = worksheet.GetFirstChild<SheetData>()?.Elements<Row>().ToArray()
            ?? [];
        return rows.Length > 0
            && rows.All(row =>
                row.Height is null
                && row.CustomHeight?.Value != true);
    }

    private static bool StyleHasWrapText(
        SpreadsheetDocument document,
        uint styleIndex) =>
        document.WorkbookPart!.WorkbookStylesPart!.Stylesheet.CellFormats!
            .Elements<CellFormat>()
            .ElementAt(checked((int)styleIndex))
            .Alignment?
            .WrapText?
            .Value == true;

    private static uint CellColumnIndex(string? reference)
    {
        uint result = 0;
        foreach (var character in reference ?? string.Empty)
        {
            if (!char.IsAsciiLetter(character))
            {
                break;
            }

            result = checked(
                result * 26
                + (uint)(char.ToUpperInvariant(character) - 'A' + 1));
        }

        return result;
    }

    private sealed class TrackingPrescreenPageRepository(
        IPrescreenPageRepository inner,
        bool throwOnPageRead) : IPrescreenPageRepository
    {
        internal int PageCalls { get; private set; }

        public Task<PageResult<PrescreenHitRow>> GetPageAsync(
            string projectId,
            string ruleKey,
            FilterRuleContext context,
            PageRequest request,
            CancellationToken cancellationToken)
        {
            PageCalls++;
            if (throwOnPageRead)
            {
                throw new InvalidOperationException(
                    "Summary-only Pre-screening detail must not be read.");
            }

            return inner.GetPageAsync(
                projectId,
                ruleKey,
                context,
                request,
                cancellationToken);
        }

        public Task<PrescreenHitCounts> GetCountsAsync(
            string projectId,
            string ruleKey,
            FilterRuleContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Infrastructure writer must not acquire Pre-screening counts.");
    }

    private sealed class FixedCreatorSummaryRepository(
        IReadOnlyList<CreatorSummaryExportRow> rows)
        : ICreatorSummaryExportRepository
    {
        public Task<IReadOnlyList<CreatorSummaryExportRow>> FetchAllAsync(
            string projectId,
            string periodStart,
            string periodEnd,
            CancellationToken cancellationToken) =>
            Task.FromResult(rows);
    }

    private sealed class FixedAccountUsageRepository(
        IReadOnlyList<AccountUsageExportRow> rows)
        : IAccountUsageExportRepository
    {
        public Task<IReadOnlyList<AccountUsageExportRow>> FetchAllAsync(
            string projectId,
            string periodStart,
            string periodEnd,
            CancellationToken cancellationToken) =>
            Task.FromResult(rows);
    }
}
