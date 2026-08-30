using System.Text.Json;
using ClosedXML.Excel;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 底稿欄位來源忠實性 synthetic regression（master spec「正式底稿欄位來源忠實性」節）。
/// R2 的欄位集合、實際名稱與 ordinal 由獨立的資料庫 schema metadata
/// （import_field_definition 的 source scope，依 ordinal）驗證，且必須包含未被欄位配對
/// 選中的來源欄位；R6 取自 GL 來源欄位的標題必須沿用與 R2 相同的 schema lineage，
/// 任意合法來源欄名都不得被改寫成 ACCOUNT_CODE／ACCOUNT_NAME 等固定 alias；
/// 彙總結果本身的衍生欄位維持既有輸出契約名稱，不冒充來源欄位。
/// </summary>
public sealed class PrescreenReportSourceLineageTests
{
    private const string DocNumColumn = "傳票 No.";
    private const string PostDateColumn = "過帳日※";
    private const string UnmappedNoteColumn = "備註-未配對";
    private const string DescriptionColumn = "摘要說明";
    private const string AmountColumn = "金額NTD";
    private const string DebitFlagColumn = "借方旗標X";

    [Fact]
    public async Task R2_EmitsCompleteSourceSchemaCatalogIncludingUnmappedColumns()
    {
        using var host = new HandlerTestHost();
        var project = await SetupAndExportAsync(
            host,
            accountCodeColumn: "科目代號#1",
            accountNameColumn: "科目名稱※中",
            unmappedTrapColumn: "ACCOUNT_NAME");

        // 獨立 schema metadata oracle：import_field_definition（source scope）依 ordinal。
        var catalog = await ReadSourceSchemaCatalogAsync(host, project.ProjectId);
        Assert.Equal(project.Columns, catalog);

        using var workbook = new XLWorkbook(project.ArtifactPath);
        var r2 = workbook.Worksheet("R2");
        var headers = Enumerable.Range(1, catalog.Count)
            .Select(column => r2.Cell(1, column).GetString())
            .ToArray();
        Assert.Equal(catalog, headers);
        Assert.Equal(string.Empty, r2.Cell(1, catalog.Count + 1).GetString());

        // 未被配對的來源欄位值必須逐格保真輸出（含名稱恰為固定 alias 的來源欄）。
        var hitRow = FindRowByCell(
            r2,
            Array.IndexOf(project.Columns.ToArray(), DescriptionColumn) + 1,
            "調整移轉分錄");
        Assert.Equal(
            "自由文字A",
            r2.Cell(hitRow, Array.IndexOf(project.Columns.ToArray(), UnmappedNoteColumn) + 1)
                .GetString());
        Assert.Equal(
            "陷阱值1",
            r2.Cell(hitRow, Array.IndexOf(project.Columns.ToArray(), "ACCOUNT_NAME") + 1)
                .GetString());
    }

    [Theory]
    [InlineData("科目代號#1", "科目名稱※中")]
    [InlineData("Acct No.(1)", "Acct Desc-中文")]
    public async Task R6_SourceDerivedHeaders_UseActualMappedSourceColumnNames(
        string accountCodeColumn,
        string accountNameColumn)
    {
        using var host = new HandlerTestHost();
        var project = await SetupAndExportAsync(
            host,
            accountCodeColumn,
            accountNameColumn,
            unmappedTrapColumn: "ACCOUNT_NAME");

        using var workbook = new XLWorkbook(project.ArtifactPath);
        var r6 = workbook.Worksheet("R6");
        var headers = Enumerable.Range(1, 5)
            .Select(column => r6.Cell(1, column).GetString())
            .ToArray();

        // 取自 GL 來源欄位的標題沿用 R2 的 schema lineage；彙總衍生欄維持既有輸出契約名稱。
        Assert.Equal(
            new[]
            {
                accountCodeColumn,
                accountNameColumn,
                "ENTRY_COUNT",
                "DEBIT_TOTAL",
                "CREDIT_TOTAL"
            },
            headers);
        Assert.DoesNotContain("ACCOUNT_CODE", headers);
        Assert.Equal(string.Empty, r6.Cell(1, 6).GetString());

        // 彙總資料本身仍是既有科目彙總語意。
        var firstAccounts = new[]
        {
            r6.Cell(2, 1).GetString(),
            r6.Cell(3, 1).GetString()
        };
        Assert.Equal(
            new[] { "1101", "4101" },
            firstAccounts.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task R6_EmitsAliasNamesOnlyWhenSourceColumnsActuallyBearThem()
    {
        using var host = new HandlerTestHost();
        var project = await SetupAndExportAsync(
            host,
            accountCodeColumn: "ACCOUNT_CODE",
            accountNameColumn: "ACCOUNT_NAME",
            unmappedTrapColumn: "OTHER_NOTE");

        using var workbook = new XLWorkbook(project.ArtifactPath);
        var r6 = workbook.Worksheet("R6");

        // 底層資料表確實存在同名欄位時，同名輸出是 lineage 的自然結果，不是 alias。
        Assert.Equal("ACCOUNT_CODE", r6.Cell(1, 1).GetString());
        Assert.Equal("ACCOUNT_NAME", r6.Cell(1, 2).GetString());
    }

    [Fact]
    public async Task R6_MissingGlMapping_FailsClosedInsteadOfEmittingAliases()
    {
        var projectId = $"lineage-fail-closed-{Guid.NewGuid():N}";
        var projectsRoot = Path.Combine(
            Path.GetTempPath(),
            $"jet-lineage-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectsRoot);
        try
        {
            var database = new SqliteProjectDatabase(new JetProjectFolder(projectsRoot));
            var writer = CreateWriter(database);
            var projection = ZeroHitProjection();
            var plan = ZeroHitPlan(projectId, projection);
            await using var output = new MemoryStream();

            // 專案沒有 committed GL mapping 時，R6 沒有可沿用的 schema lineage；
            // 必須 fail closed，不得退回固定 alias 標題。
            var exception = await Assert.ThrowsAsync<JetActionException>(() =>
                ((IPlannedPrescreenReportWriter)writer).WritePlannedAsync(
                    output,
                    Context(projectId),
                    projection,
                    plan,
                    "tester",
                    CancellationToken.None));
            Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(projectsRoot, recursive: true);
        }
    }

    private sealed record LineageProject(
        string ProjectId,
        string ArtifactPath,
        IReadOnlyList<string> Columns);

    private static async Task<LineageProject> SetupAndExportAsync(
        HandlerTestHost host,
        string accountCodeColumn,
        string accountNameColumn,
        string unmappedTrapColumn)
    {
        var columns = new[]
        {
            DocNumColumn,
            PostDateColumn,
            accountCodeColumn,
            UnmappedNoteColumn,
            accountNameColumn,
            unmappedTrapColumn,
            DescriptionColumn,
            AmountColumn,
            DebitFlagColumn
        };
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = "LINEAGE-2025-001",
            entityName = "欄位來源忠實性案件",
            operatorId = "tester",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            databaseProvider = "sqlite"
        }));
        var projectId = created.GetProperty("projectId").GetString()!;

        var gl = new InlineGlWorkbookBuilder()
            .WithColumns(columns)
            .AddRow("JV-001", "2025-03-05", "1101", "自由文字A", "現金", "陷阱值1", "調整移轉分錄", "100.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "自由文字B", "收入", "陷阱值2", "一般摘要甲", "100.00", 0)
            .AddRow("JV-002", "2025-03-06", "1101", "自由文字C", "現金", "陷阱值3", "一般摘要乙", "200.00", 1)
            .AddRow("JV-002", "2025-03-06", "4101", "自由文字D", "收入", "陷阱值4", "調整回沖", "200.00", 0);
        var glPath = gl.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
            {
                filePath = glPath,
                fileName = "lineage-gl.xlsx"
            }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(glPath);
        }

        var tb = new InlineTbWorkbookBuilder()
            .AddRow("1101", "現金", 300m)
            .AddRow("4101", "收入", -300m);
        var tbPath = tb.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new
            {
                filePath = tbPath,
                fileName = "lineage-tb.xlsx"
            }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(tbPath);
        }

        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["docNum"] = DocNumColumn,
                ["postDate"] = PostDateColumn,
                ["accNum"] = accountCodeColumn,
                ["accName"] = accountNameColumn,
                ["description"] = DescriptionColumn,
                ["amount"] = AmountColumn,
                ["dcField"] = DebitFlagColumn,
                ["dcDebitCode"] = "1"
            },
            amountMode = "flag"
        }));
        await host.DispatchAsync("mapping.commit.tb", JsonSerializer.Serialize(new
        {
            mapping = InlineTbWorkbookBuilder.BuildDirectModeMapping(),
            changeMode = "direct"
        }));

        await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        var runId = prescreen.GetProperty("resultRef").GetProperty("runId").GetString();
        var exported = await host.DispatchAsync(
            "export.prescreenReport",
            JsonSerializer.Serialize(new { runId }));
        var fileName = exported.GetProperty("artifact").GetProperty("fileName").GetString()!;

        return new LineageProject(
            projectId,
            Path.Combine(host.ProjectsRoot, projectId, fileName),
            columns);
    }

    private static async Task<IReadOnlyList<string>> ReadSourceSchemaCatalogAsync(
        HandlerTestHost host,
        string projectId)
    {
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT d.field_name
            FROM import_field_definition d
            JOIN import_batch b ON b.batch_id = d.batch_id
            WHERE b.dataset_kind = 'gl' AND d.definition_scope = 'source'
            ORDER BY d.ordinal;
            """;
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static int FindRowByCell(IXLWorksheet sheet, int column, string value)
    {
        for (var row = 2; row <= sheet.LastRowUsed()!.RowNumber(); row++)
        {
            if (string.Equals(sheet.Cell(row, column).GetString(), value, StringComparison.Ordinal))
            {
                return row;
            }
        }

        throw new InvalidOperationException($"找不到值為 '{value}' 的資料列。");
    }

    private static LegacyReportWriter CreateWriter(SqliteProjectDatabase database) =>
        new(
            new LocalCompletenessAccountPageRepository(database),
            new LocalCompletenessDiffPageRepository(database),
            new LocalUnbalancedGlEntryPageRepository(database),
            new LocalNullRecordsPageRepository(database),
            new LocalInfSamplePageRepository(database),
            new LocalPrescreenPageRepository(database),
            new LocalRawGlExportRepository(database),
            new LocalImportRepository(database),
            new LocalMappingStateStore(database),
            new LocalCreatorSummaryExportRepository(database),
            new LocalAccountUsageExportRepository(database),
            new LocalTagMatrixScenariosRepository(database),
            new LocalTagMatrixRowPageRepository(database));

    private static PrescreenReportProjection ZeroHitProjection()
    {
        var notApplicable = new PrescreenReportRuleProjection("na", "測試未執行");
        return new PrescreenReportProjection(
            PostPeriodApproval: new("V", null, 0),
            SuspiciousKeywords: notApplicable,
            UnexpectedAccountPair: notApplicable,
            TrailingZeros: notApplicable,
            CreatorSummary: new("V", null, 0),
            RareAccounts: new("V", null, 0),
            BlankDescription: notApplicable);
    }

    private static PrescreenReportPlan ZeroHitPlan(
        string projectId,
        PrescreenReportProjection projection)
    {
        var unfinalized = JetAuditProgram.Plan(new PrescreenReportRequest(
            projectId,
            new FilterRuleContext(10_000, "2025-12-31", "2025-01-01", "2025-12-31"),
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
                    [PrescreenReportDetailKind.PostPeriodApproval] = new(0, 0)
                }));
    }

    private static PrescreenReportContext Context(string projectId) => new(
        new ReportDocumentContext(
            projectId,
            "欄位來源忠實性公司",
            "2025-01-01",
            "2025-12-31",
            "2025-12-31",
            10_000),
        "prescreen-run",
        new DateTimeOffset(2025, 12, 31, 10, 30, 0, TimeSpan.Zero),
        "{}");
}
