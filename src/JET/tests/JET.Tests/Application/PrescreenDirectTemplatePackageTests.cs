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
        var projectId = await ReportExportFixture.SetupProjectAsync(host);
        var database = new SqliteProjectDatabase(
            new JetProjectFolder(host.ProjectsRoot));
        var pages = new TrackingPrescreenHitEntryStream(
            new LocalPrescreenPageRepository(database),
            throwOnDetailRead: !expectedDetailSheet);
        var writer = CreateWriter(database, pages);
        var projection = Projection(plannedRowCount);
        var plan = Plan(projectId, projection, plannedRowCount);
        await using var output = new MemoryStream();

        await ((IPrescreenReportWriter)writer).WriteAsync(
            output,
            Context(projectId),
            projection,
            plan,
            ReportWriterTestSupport.SyntheticMetadata(),
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
                pages.DetailReads);
            var summary = workbook.Worksheet("Pre-screening_Report");
            Assert.Equal(
                "# 1. 於期末財務報表準備期間核准並入到查核年度總帳之分錄",
                summary.Cell("B8").GetString());
            Assert.DoesNotContain(
                "xxxx/xx/xx",
                summary.Cell("C8").GetString(),
                StringComparison.Ordinal);
            Assert.Equal(
                "# 分錄無摘要描述(即空白摘要)",
                summary.Cell("B14").GetString());
            Assert.Equal(14, summary.LastRowUsed()!.RowNumber());
            Assert.Equal(1, summary.Cell("E8").GetValue<long>());
            Assert.Equal(plannedRowCount, summary.Cell("F8").GetValue<long>());
            // D 欄照舊版原句，筆數看 E、F 欄。
            Assert.Equal(
                expectedDetailSheet
                    ? "符合預篩選條件之明細項請詳 [R1] 工作表"
                    : "明細筆數超過10,000筆，明細資料不匯出",
                summary.Cell("D8").GetString());
            Assert.All(
                summary.Range("E12:F14").Cells(),
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
            Assert.Single(templateSummary.RequiredWorksheet().Elements<Drawing>())
                .OuterXml,
            Assert.Single(actualSummary.RequiredWorksheet().Elements<Drawing>())
                .OuterXml);
        var appearanceMismatches = new List<string>();
        if (!ColumnAndCellsAreWrapped(
                actual,
                actualSummary.RequiredWorksheet(),
                columnIndex: 3))
        {
            appearanceMismatches.Add(
                "prescreen-summary-description-wrap: Pre-screening_Report!column C alignment.wrapText");
        }
        if (expectedDetailSheet
            && !ColumnsAreAutoFit(
                WorksheetPartFor(actual, "R1").RequiredWorksheet(),
                first: 1,
                last: 20))
        {
            appearanceMismatches.Add(
                "prescreen-detail-autofit: R1!columns 1-20 column.autoFit");
        }
        Assert.True(
            appearanceMismatches.Count == 0,
            "Pre-screening registry diff:\n"
            + string.Join("\n", appearanceMismatches));
        Assert.Empty(new OpenXmlValidator().Validate(actual));
    }

    [Fact]
    public async Task AggregateSheets_AutoFitAllRowsReuseWidthsAcrossContinuationsAndPreserveNumericStyles()
    {
        const int rowCount = 17;
        var longPreparer = new string('編', 40);
        var longAccountName = new string('科', 40);
        var preparers = Enumerable.Range(0, rowCount)
            .Select(index => new PreparerSummaryExportRow(
                index == rowCount - 1 ? longPreparer : $"creator-{index + 1}",
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
        // 每次用新的暫存資料夾：固定放在系統暫存資料夾時，前一次執行留下的資料庫會被下一次沿用，
        // 案件資料庫的版本往上改以後，留下的資料庫就被當成較早版本的案件拒絕。
        using var root = new TempProjectRoot();
        var database = new SqliteProjectDatabase(
            new JetProjectFolder(root.Path));
        // 只有建案會建立案件資料庫，讀寫前的檢查不順便建表，所以照建案的做法先建表。
        await database.EnsureCreatedAsync("aggregate-width-project", CancellationToken.None);
        // R6 標題沿用 committed GL mapping 的來源欄名（欄位來源忠實性），
        // writer 層 fixture 因此需要既有 mapping state。
        await new LocalMappingStateStore(database).SaveAsync(
            "aggregate-width-project",
            new CommittedMapping(
                DatasetKind.Gl,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [GlMappingKeys.AccountNumber] = "科目代號",
                    [GlMappingKeys.AccountName] = "科目名稱"
                },
                "flag",
                "aggregate-width-batch",
                DateTimeOffset.UtcNow),
            CancellationToken.None);
        var writer = CreateWriter(
            database,
            new LocalPrescreenPageRepository(database),
            new LegacyReportWriterOptions(17),
            new FixedPreparerSummaryRepository(preparers),
            new FixedAccountUsageRepository(accounts));
        var projection = Projection(0);
        var plan = Plan("aggregate-width-project", projection, 0);
        await using var output = new MemoryStream();

        await ((IPrescreenReportWriter)writer).WriteAsync(
            output,
            Context("aggregate-width-project"),
            projection,
            plan,
            ReportWriterTestSupport.SyntheticMetadata(),
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
                ((IPrescreenReportWriter)writer).WriteAsync(
                    prescreenOutput,
                    Context(projectId),
                    projection,
                    plan,
                    ReportWriterTestSupport.SyntheticMetadata(),
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
            new DateOnly(2025, 12, 31),
            [],
            new Dictionary<int, string>());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ((ICriteriaSelectionReportWriter)writer).WriteAsync(
                criteriaOutput,
                criteriaContext,
                ReportWriterTestSupport.SyntheticMetadata(),
                [],
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
            DateOnly.FromDateTime(scenarios[0].SavedUtc.UtcDateTime),
            scenarios,
            new Dictionary<int, string>
            {
                [1] = "摘要包含「調整」",
                [2] = "摘要包含「正常」"
            });
        await using var output = new MemoryStream();

        await ((ICriteriaSelectionReportWriter)writer).WriteAsync(
            output,
            context,
            ReportWriterTestSupport.SyntheticMetadata(),
            [],
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
                    "#Criteria Select 2 (2)",
                    // 正式入口一定寫出隱藏的中繼資料工作表。
                    ReportWorkbookMetadataFormat.WorksheetName
                },
                workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
            var summary = workbook.Worksheet("Summary Inforamtion");
            Assert.All(
                summary.Range("A3:D3").Cells(),
                cell => Assert.True(cell.IsEmpty()));
            Assert.Equal("編號", summary.Cell("A4").GetString());
            Assert.Equal("條件的內容", summary.Cell("B4").GetString());
            Assert.Equal("符合條件的傳票數量", summary.Cell("C4").GetString());
            Assert.Equal("符合條件的分錄筆數", summary.Cell("D4").GetString());
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
                // 最後一欄照舊版叫 Final_TAG，續頁同樣重複表頭；
                // 直接符合條件的分錄寫 Y，同一張傳票帶出的其他分錄留白。
                Assert.Equal("Final_TAG", first.Cell(1, OriginalHeaders.Length + 1).GetString());
                Assert.Equal("Final_TAG", second.Cell(1, OriginalHeaders.Length + 1).GetString());
                Assert.True(first.Cell(1, OriginalHeaders.Length + 2).IsEmpty());
                var keyword = position == 1 ? "調整" : "正常";
                var markers = Enumerable.Range(2, 16)
                    .Select(row => (Description: first.Cell(row, 6).GetString(), Tag: first.Cell(row, 9).GetString()))
                    .Concat(Enumerable.Range(2, 2)
                        .Select(row => (Description: second.Cell(row, 6).GetString(), Tag: second.Cell(row, 9).GetString())))
                    .ToArray();
                Assert.Equal(9, markers.Count(marker => marker.Tag == "Y"));
                Assert.All(markers, marker => Assert.Equal(
                    marker.Description.StartsWith(keyword, StringComparison.Ordinal) ? "Y" : "",
                    marker.Tag));
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
                .RequiredWorksheet().Elements<Drawing>());
        var appearanceMismatches = new List<string>();
        var summaryWorksheet = WorksheetPartFor(
            document,
            "Summary Inforamtion").RequiredWorksheet();
        if (!RowsAreAutoFit(summaryWorksheet))
        {
            appearanceMismatches.Add(
                "criteria-summary-row-autofit: Summary Inforamtion!all rows row.autoFit");
        }
        foreach (var sheetName in document.WorkbookPart!.RequiredWorkbook().Sheets!
                     .Elements<Sheet>()
                     .Select(sheet => sheet.Name?.Value!)
                     .Where(name => name.StartsWith(
                         "#Criteria Select",
                         StringComparison.Ordinal)))
        {
            Assert.Empty(
                WorksheetPartFor(document, sheetName)
                    .RequiredWorksheet().Elements<Drawing>());
            if (!ColumnsAreAutoFit(
                    WorksheetPartFor(document, sheetName).RequiredWorksheet(),
                    first: 1,
                    last: 20))
            {
                appearanceMismatches.Add(
                    $"criteria-detail-autofit: {sheetName}!columns 1-20 column.autoFit");
            }
        }
        Assert.True(
            appearanceMismatches.Count == 0,
            "Criteria registry diff:\n"
            + string.Join("\n", appearanceMismatches));

        // AuditCore 只會把舊 IDEA 的單一「未預期借貸組合」位置放入此集合。
        // Writer 必須只改該位置的 D 欄，不能把所有情境都改成整張傳票列數。
        await using var wholeVoucherSummaryOutput = new MemoryStream();
        await ((ICriteriaSelectionReportWriter)writer).WriteAsync(
            wholeVoucherSummaryOutput,
            context with
            {
                WholeVoucherSummaryPositions = new HashSet<int> { 1 }
            },
            ReportWriterTestSupport.SyntheticMetadata(),
            [],
            "tester",
            CancellationToken.None);
        using var wholeVoucherSummaryWorkbook = new XLWorkbook(
            new MemoryStream(wholeVoucherSummaryOutput.ToArray(), writable: false));
        var scopedSummary = wholeVoucherSummaryWorkbook.Worksheet("Summary Inforamtion");
        Assert.Equal(18, scopedSummary.Cell("D5").GetValue<long>());
        Assert.Equal(9, scopedSummary.Cell("D6").GetValue<long>());
        // 單獨使用「未預期借貸組合」的位置整張傳票都算符合，和底稿 Step 4-1 的標記相同，
        // Final_TAG 全部寫 Y；第 2 個位置照舊只標直接符合的分錄。
        string[] TagsOf(string sheetName)
        {
            var sheet = wholeVoucherSummaryWorkbook.Worksheet(sheetName);
            return Enumerable.Range(2, sheet.LastRowUsed()!.RowNumber() - 1)
                .Select(row => sheet.Cell(row, OriginalHeaders.Length + 1).GetString())
                .ToArray();
        }
        Assert.Equal(
            Enumerable.Repeat("Y", 18),
            TagsOf("#Criteria Select 1").Concat(TagsOf("#Criteria Select 1 (2)")));
        var directTags = TagsOf("#Criteria Select 2").Concat(TagsOf("#Criteria Select 2 (2)")).ToArray();
        Assert.Equal(9, directTags.Count(tag => tag == "Y"));
        Assert.Equal(9, directTags.Count(tag => tag.Length == 0));
    }

    [Fact]
    public async Task CriteriaSevenThroughTen_ProjectTheClosedExportDatabaseAppearanceFamily()
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
                    "Sample family marker",
                    "100.00",
                    1);
                builder.AddRow(
                    "SYN-001",
                    "2025-03-05",
                    "2025-03-06",
                    "SYN-B",
                    "Synthetic account",
                    "Sample family marker",
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
                        $"Sample family {position}",
                        "Sample family marker"))
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
                "Sample synthetic",
                "2025-01-01",
                "2025-12-31",
                null,
                10_000),
            revision,
            DateOnly.FromDateTime(scenarios[0].SavedUtc.UtcDateTime),
            scenarios,
            Enumerable.Range(1, 10).ToDictionary(
                position => position,
                position => $"Sample family {position}"));
        await using var output = new MemoryStream();

        await ((ICriteriaSelectionReportWriter)writer).WriteAsync(
            output,
            context,
            ReportWriterTestSupport.SyntheticMetadata(),
            [],
            "tester",
            CancellationToken.None);

        using var workbook = new XLWorkbook(
            new MemoryStream(output.ToArray(), writable: false));
        Assert.Equal(
            new[] { "Summary Inforamtion" }
                .Concat(Enumerable.Range(1, 10).Select(position =>
                    $"#Criteria Select {position}"))
                .Append(ReportWorkbookMetadataFormat.WorksheetName),
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

    // 傳票號碼空白的命中算進摘要頁的行層命中數，明細表不列，摘要頁加一列說明，數字不動。
    [Theory]
    [InlineData("sqlite")]
    public async Task Criteria_BlankVoucherNumberHits_AreExplainedOnTheSummary(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns(OriginalHeaders)
                .AddRow("JV-001", "2025-03-05", "2025-03-06", "1101", "現金", "風險摘要 1", "100.00", 1)
                .AddRow("JV-001", "2025-03-05", "2025-03-06", "4101", "收入", "一般摘要 1", "100.00", 0)
                .AddRow("", "2025-03-07", "2025-03-08", "1101", "現金", "風險摘要 空白 1", "50.00", 1)
                .AddRow("", "2025-03-07", "2025-03-08", "4101", "收入", "風險摘要 空白 2", "50.00", 0),
            databaseProvider: provider,
            configureTb: tb => tb
                .AddRow("1101", "現金", 150)
                .AddRow("4101", "收入", -150));
        var validation = await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        var committed = await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new { scenarios = new[] { KeywordScenario("命中摘要", "風險") } }));
        var response = await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new
            {
                validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString(),
                prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString(),
                revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()
            }));
        using var workbook = new XLWorkbook(Path.Combine(
            host.ProjectsRoot, projectId, response.GetProperty("publishedReport").GetProperty("fileName").GetString()!));

        var summary = workbook.Worksheet("Summary Inforamtion");
        Assert.Equal(1, summary.Cell("C5").GetValue<long>());
        Assert.Equal(3, summary.Cell("D5").GetValue<long>());
        var detail = workbook.Worksheet("#Criteria Select 1");
        Assert.Equal(3, detail.LastRowUsed()!.RowNumber());
        Assert.Equal(new[] { "JV-001", "JV-001" }, new[] { detail.Cell(2, 1).GetString(), detail.Cell(3, 1).GetString() });
        var note = Assert.Single(summary.RowsUsed(), row => row.Cell(1).GetString() == "Criteria Selection 1（說明）");
        Assert.Equal("另有 2 筆分錄的傳票號碼空白，未列入明細表。", note.Cell(2).GetString());
        Assert.True(note.RowNumber() >= 16);
    }

    // 摘要頁情境列下方的接續列，A 欄的標籤和 B 欄的內容都和其他報告一樣加 xml:space="preserve"。
    [Fact]
    public async Task Criteria_SummaryContinuationRow_KeepsSpacesInBothCells()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns(OriginalHeaders)
                .AddRow("JV-001", "2025-03-05", "2025-03-06", "1101", "現金", "風險摘要 1", "100.00", 1)
                .AddRow("JV-001", "2025-03-05", "2025-03-06", "4101", "收入", "一般摘要 1", "100.00", 0)
                .AddRow("", "2025-03-07", "2025-03-08", "1101", "現金", "風險摘要 空白 1", "50.00", 1)
                .AddRow("", "2025-03-07", "2025-03-08", "4101", "收入", "風險摘要 空白 2", "50.00", 0),
            configureTb: tb => tb
                .AddRow("1101", "現金", 150)
                .AddRow("4101", "收入", -150));
        var validation = await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        var committed = await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new { scenarios = new[] { KeywordScenario("命中摘要", "風險") } }));
        var response = await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new
            {
                validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString(),
                prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString(),
                revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()
            }));
        using var document = SpreadsheetDocument.Open(
            Path.Combine(host.ProjectsRoot, projectId, response.GetProperty("publishedReport").GetProperty("fileName").GetString()!),
            isEditable: false);
        var workbookPart = document.WorkbookPart!;
        var summarySheet = workbookPart.Workbook!.Sheets!.Elements<Sheet>()
            .Single(sheet => sheet.Name?.Value == "Summary Inforamtion");
        var summary = ((WorksheetPart)workbookPart.GetPartById(summarySheet.Id!.Value!)).RequiredWorksheet();
        var sharedItems = workbookPart.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().ToArray() ?? [];
        // 文字可能是 inline string，也可能寫進共用字串表；兩種都讀出放文字的元素。
        DocumentFormat.OpenXml.OpenXmlElement? TextHolder(Cell cell) =>
            cell.DataType?.Value == CellValues.InlineString ? cell.InlineString
            : cell.DataType?.Value == CellValues.SharedString
                ? sharedItems[int.Parse(cell.CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture)]
                : null;

        var noteRow = Assert.Single(
            summary.Descendants<Row>(),
            row => row.Elements<Cell>().Any(cell => TextHolder(cell)?.InnerText == "Criteria Selection 1（說明）"));
        var texts = noteRow.Elements<Cell>()
            .Where(cell => TextHolder(cell) is not null)
            .Select(cell => Assert.Single(TextHolder(cell)!.Elements<Text>()))
            .ToArray();
        Assert.Equal(
            ["Criteria Selection 1（說明）", "另有 2 筆分錄的傳票號碼空白，未列入明細表。"],
            texts.Select(text => text.Text));
        Assert.All(texts, text => Assert.Equal(DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve, text.Space?.Value));
    }

    // 條件篩選報告每一張工作表的 dimension 都等於實際寫出的格子範圍。摘要頁在情境列下方多寫一列說明，
    // 範圍跟著往下延伸，欄仍到範本最右邊的 K 欄；明細與隱藏的中繼資料也一樣。
    [Fact]
    public async Task Criteria_EveryWorksheet_DeclaresDimensionEqualToWrittenCells()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns(OriginalHeaders)
                .AddRow("JV-001", "2025-03-05", "2025-03-06", "1101", "現金", "風險摘要 1", "100.00", 1)
                .AddRow("JV-001", "2025-03-05", "2025-03-06", "4101", "收入", "一般摘要 1", "100.00", 0)
                .AddRow("", "2025-03-07", "2025-03-08", "1101", "現金", "風險摘要 空白 1", "50.00", 1)
                .AddRow("", "2025-03-07", "2025-03-08", "4101", "收入", "風險摘要 空白 2", "50.00", 0),
            configureTb: tb => tb
                .AddRow("1101", "現金", 150)
                .AddRow("4101", "收入", -150));
        var validation = await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        var committed = await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new { scenarios = new[] { KeywordScenario("命中摘要", "風險") } }));
        var response = await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new
            {
                validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString(),
                prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString(),
                revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()
            }));
        var bytes = await File.ReadAllBytesAsync(Path.Combine(
            host.ProjectsRoot, projectId, response.GetProperty("publishedReport").GetProperty("fileName").GetString()!));

        using (var workbook = new XLWorkbook(new MemoryStream(bytes, writable: false)))
        {
            var summary = workbook.Worksheet("Summary Inforamtion");
            Assert.Contains(summary.RowsUsed(), row => row.Cell(1).GetString() == "Criteria Selection 1（說明）");
            Assert.True(workbook.Worksheets.Contains("#Criteria Select 1"));
        }

        WorksheetDimensionAssert.EveryWorksheetMatchesWrittenCells(bytes);
    }

    // 條件篩選報告的文字都寫進共用字串表，和 Excel 存檔時相同：摘要頁改寫的格子、情境下方的說明列與明細都一樣。
    // 只有 JET 讀回用的 JET_Metadata 照樣寫成 inline string；共用字串表的 count 與 uniqueCount 等於實際的格數與項目數。
    [Fact]
    public async Task Criteria_TextCells_ReferToTheSharedStringTable()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns(OriginalHeaders)
                .AddRow("JV-001", "2025-03-05", "2025-03-06", "1101", "現金", "風險摘要 1", "100.00", 1)
                .AddRow("JV-001", "2025-03-05", "2025-03-06", "4101", "收入", "一般摘要 1", "100.00", 0)
                .AddRow("", "2025-03-07", "2025-03-08", "1101", "現金", "風險摘要 空白 1", "50.00", 1)
                .AddRow("", "2025-03-07", "2025-03-08", "4101", "收入", "風險摘要 空白 2", "50.00", 0),
            configureTb: tb => tb
                .AddRow("1101", "現金", 150)
                .AddRow("4101", "收入", -150));
        var validation = await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        var committed = await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new { scenarios = new[] { KeywordScenario("命中摘要", "風險") } }));
        var response = await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new
            {
                validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString(),
                prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString(),
                revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()
            }));
        var bytes = await File.ReadAllBytesAsync(Path.Combine(
            host.ProjectsRoot, projectId, response.GetProperty("publishedReport").GetProperty("fileName").GetString()!));

        SharedStringTableAssert.TextCellsReferToTheSharedStringTable(bytes);
        Assert.Empty(SharedStringTableAssert.RecordCellsNotInline(bytes));
        using var workbook = new XLWorkbook(new MemoryStream(bytes, writable: false));
        Assert.Contains(
            workbook.Worksheet("Summary Inforamtion").RowsUsed(),
            row => row.Cell(1).GetString() == "Criteria Selection 1（說明）");
        Assert.Contains(
            workbook.Worksheet("#Criteria Select 1").RowsUsed(),
            row => row.Cells().Any(cell => cell.GetString() == "風險摘要 1"));
    }

    // 預篩選報告每一張工作表的 dimension 都等於實際寫出的格子範圍：摘要頁、R1 明細、R5 與 R6 的續頁與隱藏的中繼資料。
    // 續頁的列數上限是 17，R5 與 R6 各 17 列，第一頁寫滿 16 列，續頁只有一列。
    [Fact]
    public async Task EveryWorksheet_DeclaresDimensionEqualToWrittenCells()
    {
        const int rowCount = 17;
        using var host = new HandlerTestHost();
        var projectId = await ReportExportFixture.SetupProjectAsync(host);
        var database = new SqliteProjectDatabase(
            new JetProjectFolder(host.ProjectsRoot));
        var preparers = Enumerable.Range(1, rowCount)
            .Select(index => new PreparerSummaryExportRow($"creator-{index}", index, 10_000, -10_000))
            .ToArray();
        var accounts = Enumerable.Range(1, rowCount)
            .Select(index => new AccountUsageExportRow($"ACCOUNT-{index}", $"account-{index}", index, 10_000, -10_000))
            .ToArray();
        var writer = CreateWriter(
            database,
            new LocalPrescreenPageRepository(database),
            new LegacyReportWriterOptions(17),
            new FixedPreparerSummaryRepository(preparers),
            new FixedAccountUsageRepository(accounts));
        var projection = Projection(1);
        var plan = Plan(projectId, projection, 1);
        await using var output = new MemoryStream();

        await ((IPrescreenReportWriter)writer).WriteAsync(
            output,
            Context(projectId),
            projection,
            plan,
            ReportWriterTestSupport.SyntheticMetadata(),
            "tester",
            CancellationToken.None);

        var bytes = output.ToArray();
        using (var document = SpreadsheetDocument.Open(new MemoryStream(bytes, writable: false), false))
        {
            var names = document.WorkbookPart!.RequiredWorkbook().Sheets!.Elements<Sheet>()
                .Select(sheet => sheet.Name!.Value!)
                .ToArray();
            Assert.Subset(
                new HashSet<string>(names, StringComparer.Ordinal),
                new HashSet<string>(["Pre-screening_Report", "R1", "R5", "R5 (2)", "R6", "R6 (2)", ReportWorkbookMetadataFormat.WorksheetName], StringComparer.Ordinal));
            Assert.Empty(new OpenXmlValidator().Validate(document));
        }

        WorksheetDimensionAssert.EveryWorksheetMatchesWrittenCells(bytes);
    }

    // 預篩選報告的文字都寫進共用字串表：摘要頁、R1 明細、R5 與 R6 的續頁都一樣。重複的製單人與科目名稱只存一份，
    // 只有 JET 讀回用的 JET_Metadata 照樣寫成 inline string。
    [Fact]
    public async Task TextCells_ReferToTheSharedStringTable()
    {
        const int rowCount = 17;
        using var host = new HandlerTestHost();
        var projectId = await ReportExportFixture.SetupProjectAsync(host);
        var database = new SqliteProjectDatabase(
            new JetProjectFolder(host.ProjectsRoot));
        var preparers = Enumerable.Range(1, rowCount)
            .Select(index => new PreparerSummaryExportRow($"creator-{index % 3}", index, 10_000, -10_000))
            .ToArray();
        var accounts = Enumerable.Range(1, rowCount)
            .Select(index => new AccountUsageExportRow($"ACCOUNT-{index}", "account-same", index, 10_000, -10_000))
            .ToArray();
        var writer = CreateWriter(
            database,
            new LocalPrescreenPageRepository(database),
            new LegacyReportWriterOptions(17),
            new FixedPreparerSummaryRepository(preparers),
            new FixedAccountUsageRepository(accounts));
        var projection = Projection(1);
        var plan = Plan(projectId, projection, 1);
        await using var output = new MemoryStream();

        await ((IPrescreenReportWriter)writer).WriteAsync(
            output,
            Context(projectId),
            projection,
            plan,
            ReportWriterTestSupport.SyntheticMetadata(),
            "tester",
            CancellationToken.None);

        var bytes = output.ToArray();
        SharedStringTableAssert.TextCellsReferToTheSharedStringTable(bytes);
        Assert.Empty(SharedStringTableAssert.RecordCellsNotInline(bytes));
        using (var document = SpreadsheetDocument.Open(new MemoryStream(bytes, writable: false), false))
        {
            Assert.Empty(new OpenXmlValidator().Validate(document));
        }

        using var workbook = new XLWorkbook(new MemoryStream(bytes, writable: false));
        var accountNames = new[] { "R6", "R6 (2)" }
            .SelectMany(name => workbook.Worksheet(name).CellsUsed())
            .Count(cell => cell.GetString() == "account-same");
        Assert.Equal(rowCount, accountNames);
    }

    // 同一份合成資料分別用照原樣寫出的寫法與和 Excel 相同的寫法產生預篩選報告：文字一份是 inline string、一份在共用字串表，
    // 摘要頁、R1 明細、R5 與 R6 的續頁，儲存格的內容與外觀都相同。製單人有一筆含換行。
    [Fact]
    public async Task ExcelLayout_KeepsCellContentAndAppearance()
    {
        const int rowCount = 17;
        using var host = new HandlerTestHost();
        var projectId = await ReportExportFixture.SetupProjectAsync(host);
        var database = new SqliteProjectDatabase(
            new JetProjectFolder(host.ProjectsRoot));
        var preparers = Enumerable.Range(1, rowCount)
            .Select(index => new PreparerSummaryExportRow(
                index == 1 ? "creator\n第二行" : $"creator-{index % 3}", index, 10_000, -10_000))
            .ToArray();
        var accounts = Enumerable.Range(1, rowCount)
            .Select(index => new AccountUsageExportRow($"ACCOUNT-{index}", "account-same", index, 10_000, -10_000))
            .ToArray();
        var projection = Projection(1);
        var plan = Plan(projectId, projection, 1);

        async Task<byte[]> WriteAsync(LegacyReportWriterOptions options)
        {
            var writer = CreateWriter(
                database,
                new LocalPrescreenPageRepository(database),
                options,
                new FixedPreparerSummaryRepository(preparers),
                new FixedAccountUsageRepository(accounts));
            await using var output = new MemoryStream();
            await ((IPrescreenReportWriter)writer).WriteAsync(
                output,
                Context(projectId),
                projection,
                plan,
                ReportWriterTestSupport.SyntheticMetadata(),
                "tester",
                CancellationToken.None);
            return output.ToArray();
        }

        var asWritten = await WriteAsync(new LegacyReportWriterOptions(17) { Layout = FormalWorkbookLayout.AsWritten });
        var sameAsExcel = await WriteAsync(new LegacyReportWriterOptions(17));

        // 前提：兩份的寫法真的不同，比對才有意義。
        Assert.NotEqual(0, ExcelPackageLayoutAssert.InlineTextCellCount(asWritten));
        Assert.Equal(0, ExcelPackageLayoutAssert.InlineTextCellCount(sameAsExcel));
        // 同一組檢查認得出照原樣寫出的那一份，和 Excel 相同的那一份全部通過。
        Assert.NotEmpty(ExcelPackageLayoutAssert.Problems(asWritten, ReportTemplateCatalog.Prescreen));
        Assert.Empty(ExcelPackageLayoutAssert.Problems(sameAsExcel, ReportTemplateCatalog.Prescreen));
        ExcelPackageLayoutAssert.SameContentAndAppearance(asWritten, sameAsExcel);
    }

    // 同一份合成資料分別用照原樣寫出的寫法與和 Excel 相同的寫法產生條件篩選報告：摘要頁、情境下方的說明列與明細，
    // 儲存格的內容與外觀都相同。明細有一筆摘要含換行。
    [Fact]
    public async Task Criteria_ExcelLayout_KeepsCellContentAndAppearance()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns(OriginalHeaders)
                .AddRow("JV-001", "2025-03-05", "2025-03-06", "1101", "現金", "風險摘要\n第二行", "100.00", 1)
                .AddRow("JV-001", "2025-03-05", "2025-03-06", "4101", "收入", "一般摘要 1", "100.00", 0)
                .AddRow("", "2025-03-07", "2025-03-08", "1101", "現金", "風險摘要 空白 1", "50.00", 1)
                .AddRow("", "2025-03-07", "2025-03-08", "4101", "收入", "風險摘要 空白 2", "50.00", 0),
            configureTb: tb => tb
                .AddRow("1101", "現金", 150)
                .AddRow("4101", "收入", -150));
        await host.DispatchAsync("validate.run");
        var committed = await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new { scenarios = new[] { KeywordScenario("命中摘要", "風險") } }));
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()!;
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        var scenarios = await new LocalFilterScenarioStore(database).ListAsync(projectId, CancellationToken.None);
        var context = new CriteriaSelectionReportContext(
            new ReportDocumentContext(projectId, "範本驗收公司", "2025-01-01", "2025-12-31", null, 10_000),
            revision,
            DateOnly.FromDateTime(Assert.Single(scenarios).SavedUtc.UtcDateTime),
            scenarios,
            new Dictionary<int, string> { [1] = "摘要包含「風險」" });

        async Task<byte[]> WriteAsync(LegacyReportWriterOptions options)
        {
            var writer = CreateWriter(database, new LocalPrescreenPageRepository(database), options);
            await using var output = new MemoryStream();
            await ((ICriteriaSelectionReportWriter)writer).WriteAsync(
                output,
                context,
                ReportWriterTestSupport.SyntheticMetadata(),
                [],
                "tester",
                CancellationToken.None);
            return output.ToArray();
        }

        var asWritten = await WriteAsync(new LegacyReportWriterOptions() { Layout = FormalWorkbookLayout.AsWritten });
        var sameAsExcel = await WriteAsync(new LegacyReportWriterOptions());

        // 前提：兩份的寫法真的不同，比對才有意義。
        Assert.NotEqual(0, ExcelPackageLayoutAssert.InlineTextCellCount(asWritten));
        Assert.Equal(0, ExcelPackageLayoutAssert.InlineTextCellCount(sameAsExcel));
        // 同一組檢查認得出照原樣寫出的那一份，和 Excel 相同的那一份全部通過。
        Assert.NotEmpty(ExcelPackageLayoutAssert.Problems(asWritten, ReportTemplateCatalog.CriteriaSelection));
        Assert.Empty(ExcelPackageLayoutAssert.Problems(sameAsExcel, ReportTemplateCatalog.CriteriaSelection));
        ExcelPackageLayoutAssert.SameContentAndAppearance(asWritten, sameAsExcel);
    }

    // 固定答案核對整張傳票展開，以及傳票與傳票內各列的順序。
    [Fact]
    public async Task Criteria_OutputIdentityCountsAndOrder_MatchTheFixedAnswer()
    {
        var oracle = await ExportCriteriaOracleAsync();

        Assert.Equal(2, oracle.VoucherHitCount);
        Assert.Equal(2, oracle.RowHitCount);
        Assert.Equal(4, oracle.DetailRows.Count);
        Assert.Equal(
            new[]
            {
                "JV-001|風險摘要 1",
                "JV-001|一般摘要 1",
                "JV-002|風險摘要 2",
                "JV-002|一般摘要 2"
            },
            oracle.DetailRows);
    }

    // 沒有空白傳票號碼的命中時不加說明列。
    [Fact]
    public async Task Criteria_WithoutBlankVoucherNumberHits_AddsNoExplanationRow()
    {
        var oracle = await ExportCriteriaOracleAsync();

        Assert.Equal(2, oracle.RowHitCount);
        Assert.False(oracle.HasExplanationRow);
    }

    // 摘要頁 D 欄照舊版原句。傳票核准日沒有配對寫「沒有設定傳票核准日欄位」，
    // 已執行但沒有命中寫「沒有符合預篩選條件之明細項」，有明細頁時寫「請詳 [Rn] 工作表」，空白摘要那一列寫「新增預篩選條件」；
    // 沒有命中或不適用時 E、F 欄寫 N/A。其他不適用原因仍寫「N/A（原因）」。
    [Fact]
    public async Task Summary_DColumnUsesLegacySentencesForEachOutcome()
    {
        using var workbook = new XLWorkbook(
            new MemoryStream(await WriteSummaryWithEveryOutcomeAsync(), writable: false));
        var summary = workbook.Worksheet("Pre-screening_Report");
        Assert.Equal("沒有設定傳票核准日欄位", summary.Cell("D8").GetString());
        Assert.Equal("沒有符合預篩選條件之明細項", summary.Cell("D9").GetString());
        Assert.Equal("N/A（測試未執行）", summary.Cell("D10").GetString());
        Assert.Equal("符合預篩選條件之明細項請詳 [R4] 工作表", summary.Cell("D11").GetString());
        Assert.Equal("符合新增預篩選條件之明細項請詳 [R7] 工作表", summary.Cell("D14").GetString());
        foreach (var row in new[] { 8, 9, 10 })
        {
            Assert.Equal("N/A", summary.Cell(row, "E").GetString());
            Assert.Equal("N/A", summary.Cell(row, "F").GetString());
        }
        Assert.Equal(1, summary.Cell("E11").GetValue<long>());
        Assert.Equal(2, summary.Cell("F11").GetValue<long>());
        Assert.Equal(1, summary.Cell("E14").GetValue<long>());
        Assert.Equal(1, summary.Cell("F14").GetValue<long>());
        var sheetNames = workbook.Worksheets.Select(static sheet => sheet.Name).ToArray();
        Assert.Contains("R4", sheetNames);
        Assert.Contains("R7", sheetNames);
        Assert.DoesNotContain("R1", sheetNames);
        Assert.DoesNotContain("R2", sheetNames);
        Assert.DoesNotContain("R3", sheetNames);
    }

    // 摘要頁照範本只有 14 列：第 8 到 13 列是六項預先篩選程序，第 14 列是空白摘要，JET 不另外加列。
    // JET 只寫表頭的 E1、E2、E4、E5，第 8 列與第 11 列的說明，以及第 8 到 14 列的狀態、結果、傳票數量與分錄筆數；
    // 其他格子的內容和範本相同。
    [Fact]
    public async Task Summary_KeepsTheFourteenTemplateRowsAndWritesOnlyTheResultCells()
    {
        var bytes = await WriteSummaryWithEveryOutcomeAsync();
        var templateBytes = await TemplateBytesAsync(ReportTemplateCatalog.Prescreen);

        using (var document = SpreadsheetDocument.Open(new MemoryStream(bytes, writable: false), false))
        {
            var worksheet = WorksheetPartFor(document, "Pre-screening_Report").RequiredWorksheet();
            Assert.Equal("A1:F14", worksheet.GetFirstChild<SheetDimension>()?.Reference?.Value);
            Assert.Equal(
                Enumerable.Range(1, 14).Select(static row => (uint)row).ToArray(),
                worksheet.GetFirstChild<SheetData>()!.Elements<Row>()
                    .Select(static row => row.RowIndex!.Value)
                    .ToArray());
        }

        using var workbook = new XLWorkbook(new MemoryStream(bytes, writable: false));
        using var template = new XLWorkbook(new MemoryStream(templateBytes, writable: false));
        var summary = workbook.Worksheet("Pre-screening_Report");
        var templateSummary = template.Worksheet("Pre-screening_Report");
        Assert.Equal(14, summary.LastRowUsed()!.RowNumber());
        Assert.Equal(6, summary.LastColumnUsed()!.ColumnNumber());

        Assert.Equal(
            new[]
            {
                "預先篩選的程序",
                "# 1. 於期末財務報表準備期間核准並入到查核年度總帳之分錄",
                "# 2. 分錄摘要出現特定描述",
                "# 3. 未預期出現之特定借貸組合",
                "# 4. 分錄金額中有連續0的尾數(0的位數依分錄金額平均數決定)",
                "# 5. 依分錄編製者彙總分錄",
                "# 6. 較少使用之科目",
                "# 分錄無摘要描述(即空白摘要)"
            },
            Enumerable.Range(7, 8).Select(row => summary.Cell(row, "B").GetString().Trim()).ToArray());
        Assert.Equal(
            new[] { "na", "V", "na", "V", "V", "V", "V" },
            Enumerable.Range(8, 7).Select(row => summary.Cell(row, "A").GetString()).ToArray());
        Assert.Equal(
            new[]
            {
                "沒有設定傳票核准日欄位",
                "沒有符合預篩選條件之明細項",
                "N/A（測試未執行）",
                "符合預篩選條件之明細項請詳 [R4] 工作表",
                "符合預篩選條件之明細項請詳 [R5] 工作表",
                "符合預篩選條件之明細項請詳 [R6] 工作表",
                "符合新增預篩選條件之明細項請詳 [R7] 工作表"
            },
            Enumerable.Range(8, 7).Select(row => summary.Cell(row, "D").GetString()).ToArray());
        Assert.Equal(
            new[] { "N/A|N/A", "N/A|N/A", "N/A|N/A", "1|2", "N/A|N/A", "N/A|N/A", "1|1" },
            Enumerable.Range(8, 7)
                .Select(row => $"{summary.Cell(row, "E").GetString()}|{summary.Cell(row, "F").GetString()}")
                .ToArray());

        var written = new HashSet<string>(StringComparer.Ordinal) { "E1", "E2", "E4", "E5", "C8", "C11" };
        foreach (var row in Enumerable.Range(8, 7))
        {
            written.UnionWith(new[] { $"A{row}", $"D{row}", $"E{row}", $"F{row}" });
        }
        foreach (var cell in summary.Range("A1:F14").Cells())
        {
            var address = cell.Address.ToString()!;
            if (written.Contains(address))
            {
                Assert.False(cell.IsEmpty(), $"{address} 應該由 JET 寫入。");
            }
            else
            {
                Assert.True(
                    templateSummary.Cell(address).GetString() == cell.GetString(),
                    $"{address} 的內容應該和範本相同。");
            }
        }
    }

    private static async Task<byte[]> WriteSummaryWithEveryOutcomeAsync()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportExportFixture.SetupProjectAsync(host);
        var database = new SqliteProjectDatabase(
            new JetProjectFolder(host.ProjectsRoot));
        var writer = CreateWriter(database, new LocalPrescreenPageRepository(database));
        var projection = new PrescreenReportProjection(
            PrepPeriodApproval: new("na", "「傳票核准日」尚未選擇來源欄，請回第三步欄位配對補上。"),
            SuspiciousKeywords: new("V", null, 0),
            UnexpectedAccountPair: new("na", "測試未執行"),
            TrailingZeros: new("V", null, 2),
            PreparerSummary: new("V", null, 1),
            RareAccounts: new("V", null, 2),
            BlankDescription: new("V", null, 1))
        {
            ZerosThreshold = 6
        };
        var plan = JetAuditProgram.Finalize(
            JetAuditProgram.Plan(new PrescreenReportRequest(
                projectId,
                // 尾數規則的明細頁要帶那次預篩選的 0 的個數，和投影的 ZerosThreshold 相同。
                new FilterConditionContext(10_000, "2025-12-31", "2025-01-01", "2025-12-31") { ZerosThreshold = 6 },
                projection.PrepPeriodApproval.NaReason,
                projection.SuspiciousKeywords.NaReason,
                projection.UnexpectedAccountPair.NaReason,
                projection.TrailingZeros.NaReason,
                projection.BlankDescription.NaReason)),
            new PrescreenReportPlanningFacts(
                new Dictionary<PrescreenReportDetailKind, PrescreenHitCounts>
                {
                    [PrescreenReportDetailKind.SuspiciousKeywords] = new(VoucherHitCount: 0, RowHitCount: 0),
                    [PrescreenReportDetailKind.TrailingZeros] = new(VoucherHitCount: 1, RowHitCount: 2),
                    [PrescreenReportDetailKind.BlankDescription] = new(VoucherHitCount: 1, RowHitCount: 1)
                }));
        await using var output = new MemoryStream();

        await ((IPrescreenReportWriter)writer).WriteAsync(
            output,
            Context(projectId),
            projection,
            plan,
            ReportWriterTestSupport.SyntheticMetadata(),
            "tester",
            CancellationToken.None);

        return output.ToArray();
    }

    // 配對了人工傳票欄位時，R5 照舊版在人員後面多一欄「人工傳票否_JE_S」，
    // 同一位人員依人工旗標分列，值寫「人工建立分錄」或「自動拋轉分錄」，旗標無法判斷時留白。
    [Fact]
    public async Task PreparerSummary_ManualFlagMapped_AddsTheManualColumnAndKeepsOneRowPerFlag()
    {
        PreparerSummaryExportRow[] preparers =
        [
            new("creator-1", 2, 30_000, 0, IsManual: true),
            new("creator-1", 1, 0, 10_000, IsManual: false),
            new("creator-2", 1, 10_000, 0, IsManual: null)
        ];
        using var root = new TempProjectRoot();
        var database = new SqliteProjectDatabase(
            new JetProjectFolder(root.Path));
        // 照建案的做法先建表。
        await database.EnsureCreatedAsync("manual-column-project", CancellationToken.None);
        await new LocalMappingStateStore(database).SaveAsync(
            "manual-column-project",
            new CommittedMapping(
                DatasetKind.Gl,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [GlMappingKeys.AccountNumber] = "科目代號",
                    [GlMappingKeys.AccountName] = "科目名稱",
                    [GlMappingKeys.IsManual] = "人工傳票"
                },
                "flag",
                "manual-column-batch",
                DateTimeOffset.UtcNow),
            CancellationToken.None);
        var writer = CreateWriter(
            database,
            new LocalPrescreenPageRepository(database),
            new LegacyReportWriterOptions(),
            new FixedPreparerSummaryRepository(preparers),
            new FixedAccountUsageRepository([]));
        var projection = Projection(0);
        await using var output = new MemoryStream();

        await ((IPrescreenReportWriter)writer).WriteAsync(
            output,
            Context("manual-column-project"),
            projection,
            Plan("manual-column-project", projection, 0),
            ReportWriterTestSupport.SyntheticMetadata(),
            "tester",
            CancellationToken.None);

        using var workbook = new XLWorkbook(
            new MemoryStream(output.ToArray(), writable: false));
        var r5 = workbook.Worksheet("R5");
        Assert.Equal(
            new[] { "CREATED_BY", "人工傳票否_JE_S", "ENTRY_COUNT", "DEBIT_TOTAL", "CREDIT_TOTAL" },
            ReadRow(r5, 1).Take(5).ToArray());
        Assert.Equal(new[] { "creator-1", "人工建立分錄", "2" }, ReadRow(r5, 2).Take(3).ToArray());
        Assert.Equal(new[] { "creator-1", "自動拋轉分錄", "1" }, ReadRow(r5, 3).Take(3).ToArray());
        Assert.Equal(new[] { "creator-2", "", "1" }, ReadRow(r5, 4).Take(3).ToArray());
        Assert.Equal(3, r5.Cell("D2").GetValue<decimal>());
        Assert.Equal(1, r5.Cell("E3").GetValue<decimal>());
    }

    private static LegacyReportWriter CreateWriter(
        SqliteProjectDatabase database,
        IPrescreenHitEntryStreamRepository pages) =>
        new(
            new LocalCompletenessAccountPageRepository(database),
            new LocalCompletenessDiffPageRepository(database),
            new LocalUnbalancedVoucherPageRepository(database),
            new LocalBlankRecordsPageRepository(database),
            new LocalInfSamplePageRepository(database),
            pages,
            new LocalRawGlExportRepository(database),
            new LocalImportRepository(database),
            new LocalMappingStateStore(database),
            new LocalPreparerSummaryExportRepository(database),
            new LocalAccountUsageExportRepository(database),
            new LocalHighRiskSummaryScenariosRepository(database),
            new LocalSourceQualityPageRepository(database),
            new LocalHighRiskSummaryRowPageRepository(database));

    private static LegacyReportWriter CreateWriter(
        SqliteProjectDatabase database,
        IPrescreenHitEntryStreamRepository pages,
        LegacyReportWriterOptions options) =>
        CreateWriter(
            database,
            pages,
            options,
            new LocalPreparerSummaryExportRepository(database),
            new LocalAccountUsageExportRepository(database));

    private static LegacyReportWriter CreateWriter(
        SqliteProjectDatabase database,
        IPrescreenHitEntryStreamRepository pages,
        LegacyReportWriterOptions options,
        IPreparerSummaryExportRepository preparerSummaries,
        IAccountUsageExportRepository accountUsage) =>
        new(
            new LocalCompletenessAccountPageRepository(database),
            new LocalCompletenessDiffPageRepository(database),
            new LocalUnbalancedVoucherPageRepository(database),
            new LocalBlankRecordsPageRepository(database),
            new LocalInfSamplePageRepository(database),
            pages,
            new LocalRawGlExportRepository(database),
            new LocalImportRepository(database),
            new LocalMappingStateStore(database),
            preparerSummaries,
            accountUsage,
            new LocalHighRiskSummaryScenariosRepository(database),
            new LocalSourceQualityPageRepository(database),
            new LocalHighRiskSummaryRowPageRepository(database),
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
                conditions = new[]
                {
                    new
                    {
                        join = "AND",
                        type = "fieldValue", field = "description", @operator = "contains",
                        values = new[] { keyword }
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

    private static async Task<CriteriaProviderOracle> ExportCriteriaOracleAsync()
    {
        using var host = new HandlerTestHost();
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
                    validationResultId = validation.GetProperty("resultRef")
                        .GetProperty("resultId")
                        .GetString(),
                    prescreenResultId = prescreen.GetProperty("resultRef")
                        .GetProperty("resultId")
                        .GetString(),
                    revision = committed.GetProperty("resultRef")
                        .GetProperty("revision")
                        .GetString()
                }));
            var fileName = response.GetProperty("publishedReport")
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
                detailRows,
                summary.RowsUsed().Any(row => row.Cell(1).GetString().EndsWith("（說明）", StringComparison.Ordinal)));
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
        IReadOnlyList<string> DetailRows,
        bool HasExplanationRow);

    private static PrescreenReportProjection Projection(long rowCount)
    {
        var notApplicable = new PrescreenReportRuleProjection(
            "na",
            "測試未執行");
        return new PrescreenReportProjection(
            PrepPeriodApproval: new("V", null, rowCount),
            SuspiciousKeywords: notApplicable,
            UnexpectedAccountPair: notApplicable,
            TrailingZeros: notApplicable,
            PreparerSummary: new("V", null, 1),
            RareAccounts: new("V", null, 2),
            BlankDescription: notApplicable)
        {
            // 0 的個數照 IDEA 版由 GL 算出，投影帶那次預篩選的個數；這裡的尾零規則不執行，帶 6 只為了組出完整的投影。
            ZerosThreshold = 6
        };
    }

    private static PrescreenReportPlan Plan(
        string projectId,
        PrescreenReportProjection projection,
        long rowCount)
    {
        var unfinalized = JetAuditProgram.Plan(new PrescreenReportRequest(
            projectId,
            new FilterConditionContext(
                10_000,
                "2025-12-31",
                "2025-01-01",
                "2025-12-31"),
            projection.PrepPeriodApproval.NaReason,
            projection.SuspiciousKeywords.NaReason,
            projection.UnexpectedAccountPair.NaReason,
            projection.TrailingZeros.NaReason,
            projection.BlankDescription.NaReason));
        return JetAuditProgram.Finalize(
            unfinalized,
            new PrescreenReportPlanningFacts(
                new Dictionary<PrescreenReportDetailKind, PrescreenHitCounts>
                {
                    [PrescreenReportDetailKind.PrepPeriodApproval] =
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
        new DateOnly(2025, 12, 31),
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
        var sheet = workbookPart.RequiredWorkbook().Sheets!.Elements<Sheet>()
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
        document.WorkbookPart!.WorkbookStylesPart!.Stylesheet!.CellFormats!
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

    // 報告一個查詢讀完一條規則的明細；這裡記下讀了幾次明細。
    // 寫出程式拿到的讀取介面只能讀明細，沒有取筆數的方法，所以不必另外擋它取筆數。
    private sealed class TrackingPrescreenHitEntryStream(
        IPrescreenHitEntryStreamRepository inner,
        bool throwOnDetailRead) : IPrescreenHitEntryStreamRepository
    {
        internal int DetailReads { get; private set; }

        public IAsyncEnumerable<long> StreamEntryIdsAsync(
            string projectId,
            string ruleKey,
            FilterConditionContext context,
            CancellationToken cancellationToken)
        {
            DetailReads++;
            if (throwOnDetailRead)
            {
                throw new InvalidOperationException(
                    "Summary-only Pre-screening detail must not be read.");
            }

            return inner.StreamEntryIdsAsync(
                projectId,
                ruleKey,
                context,
                cancellationToken);
        }
    }

    private sealed class FixedPreparerSummaryRepository(
        IReadOnlyList<PreparerSummaryExportRow> rows)
        : IPreparerSummaryExportRepository
    {
        public Task<IReadOnlyList<PreparerSummaryExportRow>> FetchAllAsync(
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
