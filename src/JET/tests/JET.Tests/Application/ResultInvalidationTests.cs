using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 結果失效驗收（plan Phase 1）：規則執行結果(result_rule_run / 抽樣)是衍生資料,
/// 一旦其實際上游被改寫，project.load 的對應 latestRuns 必須回 null（要求重跑），
/// 但不得把無依賴的 run 一併清除：GL 影響兩種 run、TB 只影響 validate、
/// 科目配對／行事曆／授權清單只影響 prescreen。
///
/// 設計技術：狀態轉換 —— 每條合法的「上游改寫」轉換各一測試,涵蓋三個失效機制:
///   (a) 匯入 replace 清理交易（GL/TB/科目配對 re-import）
///   (b) 重投影交易（mapping re-commit）
///   (c) 行事曆 replace 交易（假日/補班匯入）
/// oracle：依賴到該上游的 latest run 應為 JSON null；無依賴者必須保留。
/// 每個測試自建 host（會變更狀態,不可共用 DemoProjectFixture）。
/// </summary>
public sealed class ResultInvalidationTests
{
    public enum MatrixMutation
    {
        GlImportReplace,
        GlImportAppend,
        TbImportReplace,
        TbImportAppend,
        TbProjection,
        Calendar,
        NonWorkingDays,
        AccountMapping,
        AuthorizedPreparer
    }

    private sealed record InvalidationMatrixState(
        string? ValidationRun,
        string? PrescreenRun,
        long FilterHitCount,
        string[] ScenarioDefinitions,
        string[] ScenarioRevisions,
        string? FilterResultRevision,
        string[] GlControlTotal);

    private static readonly (MatrixMutation Mutation, bool Validation, bool Prescreen, bool FilterHits)[]
        MatrixExpectations =
        [
            (MatrixMutation.GlImportReplace, true, true, true),
            (MatrixMutation.GlImportAppend, true, true, true),
            (MatrixMutation.TbImportReplace, true, false, false),
            (MatrixMutation.TbImportAppend, true, false, false),
            (MatrixMutation.TbProjection, true, false, false),
            (MatrixMutation.Calendar, false, true, true),
            (MatrixMutation.NonWorkingDays, false, true, true),
            (MatrixMutation.AccountMapping, false, true, true),
            (MatrixMutation.AuthorizedPreparer, false, true, true)
        ];

    public static IEnumerable<object[]> LocalMatrixCases()
    {
        foreach (var provider in new[] { "sqlite", "duckdb" })
        {
            foreach (var expected in MatrixExpectations)
            {
                yield return
                [
                    provider,
                    expected.Mutation,
                    expected.Validation,
                    expected.Prescreen,
                    expected.FilterHits
                ];
            }
        }
    }

    public static IEnumerable<object[]> SqlServerMatrixCases() =>
        MatrixExpectations.Select(expected => new object[]
        {
            expected.Mutation,
            expected.Validation,
            expected.Prescreen,
            expected.FilterHits
        });

    private static async Task<JsonElement> LoadAsync(HandlerTestHost host, string projectId)
    {
        return await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId }));
    }

    private static JsonValueKind LatestRunKind(JsonElement loaded, string runKind)
    {
        return loaded.GetProperty("latestRuns").GetProperty(runKind).ValueKind;
    }

    [Theory]
    [MemberData(nameof(LocalMatrixCases))]
    public Task MutationActionCaller_LocalProviders_PreserveExactInvalidationMatrixAndExceptions(
        string databaseProvider,
        MatrixMutation mutation,
        bool clearsValidation,
        bool clearsPrescreen,
        bool clearsFilterHits) =>
        RunInvalidationMatrixAsync(
            databaseProvider,
            sqlServerConnectionString: null,
            mutation,
            clearsValidation,
            clearsPrescreen,
            clearsFilterHits);

    [SqlServerTheory]
    [MemberData(nameof(SqlServerMatrixCases))]
    public async Task MutationActionCaller_SqlServer_PreservesExactInvalidationMatrixAndExceptions(
        MatrixMutation mutation,
        bool clearsValidation,
        bool clearsPrescreen,
        bool clearsFilterHits)
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        Assert.NotNull(connectionString);

        await RunInvalidationMatrixAsync(
            "sqlServer",
            connectionString,
            mutation,
            clearsValidation,
            clearsPrescreen,
            clearsFilterHits);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public Task GlProjection_LocalProviders_UpdateControlTotalAndPreserveExactInvalidationMatrixExceptions(
        string databaseProvider) =>
        RunGlProjectionInvalidationAsync(databaseProvider, sqlServerConnectionString: null);

    [SqlServerFact]
    public async Task GlProjection_SqlServer_UpdatesControlTotalAndPreservesExactInvalidationMatrixExceptions()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        Assert.NotNull(connectionString);

        await RunGlProjectionInvalidationAsync("sqlServer", connectionString);
    }

    [Fact]
    public async Task ReimportGl_AfterValidate_InvalidatesValidateRun()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");

        // 前置:結果確實已保存（否則「失效」測試會假性通過）。
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));

        // 上游改寫:重匯入 GL（replace 清理交易,機制 a）。
        var glFile = await DemoProjectPipeline.DispatchDemoFixtureAsync("demo.exportGlFile");
        await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
        {
            filePath = glFile.GetProperty("filePath").GetString(),
            fileName = glFile.GetProperty("fileName").GetString()
        }));

        Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
        Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));
    }

    [Fact]
    public async Task RecommitGlMapping_AfterValidate_InvalidatesValidateRun()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));

        // 上游改寫:重新提交 GL 配對（重投影交易,機制 b）。
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                context.Demo.GetProperty("gl").GetProperty("mapping").GetRawText()),
            amountMode = context.Demo.GetProperty("gl").GetProperty("amountMode").GetString()
        }));

        Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
        Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));
    }

    [Fact]
    public async Task ReimportTb_AfterValidate_InvalidatesValidateRun()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));

        // 上游改寫:重匯入 TB（replace 清理交易,機制 a;TB 餵完整性測試）。
        var tbFile = await DemoProjectPipeline.DispatchDemoFixtureAsync("demo.exportTbFile");
        await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new
        {
            filePath = tbFile.GetProperty("filePath").GetString(),
            fileName = tbFile.GetProperty("fileName").GetString()
        }));

        Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));
    }

    [Fact]
    public async Task ImportHoliday_AfterPrescreen_InvalidatesPrescreenRun()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));

        // 上游改寫:重匯入假日曆（行事曆 replace 交易,機制 c;餵週末/假日預篩選）。
        var holidays = context.Demo.GetProperty("holidays").EnumerateArray().Select(h => h.GetString()).ToList();
        await host.DispatchAsync("import.holiday", JsonSerializer.Serialize(new { dates = holidays }));

        Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
    }

    [Fact]
    public async Task ImportHolidayFromFile_AfterPrescreen_InvalidatesPrescreenRun()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));

        // 上游改寫:行事曆檔案匯入(行事曆 replace 交易,機制 c)。
        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 2).Value = "Holiday Table";
            ws.Cell(2, 1).Value = "Date_of_Holiday";
            ws.Cell(2, 2).Value = "Holiday_Name";
            ws.Cell(2, 3).Value = "IS_Holiday";
            ws.Cell(3, 1).Value = new DateTime(2025, 1, 1);
            ws.Cell(3, 2).Value = "元旦";
            ws.Cell(3, 3).Value = "Y";
        });

        try
        {
            await host.DispatchAsync("import.holiday.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));
            Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    /// <summary>
    /// 狀態轉換：非工作日設定實際改變時，其可觀察狀態必須與重匯行事曆一致。
    /// oracle：prescreen run 失效、filter 命中惰性補算後為空；validate run 與情境定義保留。
    /// </summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ChangeNonWorkingDays_AfterRunsAndFilterCommit_InvalidatesDependentResultsAfterReopen(
        string databaseProvider)
    {
        using var root = new TempProjectRoot();
        string projectId;

        using (var host = new HandlerTestHost(projectsRootPath: root.Path))
        {
            var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: databaseProvider);
            projectId = context.ProjectId;

            await host.DispatchAsync("calendar.setNonWorkingDays", """{ "days": [0, 1, 2, 3, 4, 5, 6] }""");
            await host.DispatchAsync("validate.run");
            await host.DispatchAsync("prescreen.run");
            await CommitWeekendScenarioAsync(host);

            var before = await host.DispatchAsync(
                "query.filterHitsPage", """{ "scenarioPosition": 1, "pageSize": 500 }""");
            Assert.NotEmpty(before.GetProperty("rows").EnumerateArray());

            await host.DispatchAsync("calendar.setNonWorkingDays", """{ "days": [] }""");
        }

        using var reopened = new HandlerTestHost(projectsRootPath: root.Path);
        var loaded = await LoadAsync(reopened, projectId);

        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(loaded, "validate"));
        Assert.Equal(JsonValueKind.Null, LatestRunKind(loaded, "prescreen"));
        Assert.Single(loaded.GetProperty("filterScenarios").EnumerateArray());

        var after = await reopened.DispatchAsync(
            "query.filterHitsPage", """{ "scenarioPosition": 1, "pageSize": 500 }""");
        Assert.Empty(after.GetProperty("rows").EnumerateArray());
    }

    /// <summary>
    /// 等價分割：排序與重複值不同、正規化集合相同時，不得誤清已有結果。
    /// </summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task SaveEquivalentNonWorkingDays_AfterRunsAndFilterCommit_PreservesResults(
        string databaseProvider)
    {
        using var root = new TempProjectRoot();
        string projectId;

        using (var host = new HandlerTestHost(projectsRootPath: root.Path))
        {
            var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: databaseProvider);
            projectId = context.ProjectId;

            await host.DispatchAsync("validate.run");
            await host.DispatchAsync("prescreen.run");
            await CommitWeekendScenarioAsync(host);

            await host.DispatchAsync(
                "calendar.setNonWorkingDays", """{ "days": [6, 0, 6] }""");
        }

        using var reopened = new HandlerTestHost(projectsRootPath: root.Path);
        var loaded = await LoadAsync(reopened, projectId);

        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(loaded, "validate"));
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(loaded, "prescreen"));
        Assert.Single(loaded.GetProperty("filterScenarios").EnumerateArray());

        var hits = await reopened.DispatchAsync(
            "query.filterHitsPage", """{ "scenarioPosition": 1, "pageSize": 500 }""");
        Assert.NotEmpty(hits.GetProperty("rows").EnumerateArray());
    }

    [Fact]
    public async Task ImportAccountMapping_AfterPrescreen_InvalidatesPrescreenRun()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, importAccountMapping: false);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));

        // 上游改寫:匯入科目配對（replace 清理交易,機制 a;餵未預期借貸組合規則）。
        var file = await DemoProjectPipeline.DispatchDemoFixtureAsync(
            "demo.exportAccountMappingFile");
        await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
        {
            filePath = file.GetProperty("filePath").GetString(),
            fileName = file.GetProperty("fileName").GetString()
        }));

        Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
    }

    [Fact]
    public async Task ImportAuthorizedPreparer_AfterRuns_InvalidatesOnlyPrescreenRun()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");

        var file = await DemoProjectPipeline.DispatchDemoFixtureAsync(
            "demo.exportAuthorizedPreparerFile");
        await host.DispatchAsync("import.authorizedPreparer.fromFile", JsonSerializer.Serialize(new
        {
            filePath = file.GetProperty("filePath").GetString(),
            fileName = file.GetProperty("fileName").GetString()
        }));

        var loaded = await LoadAsync(host, context.ProjectId);
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(loaded, "validate"));
        Assert.Equal(JsonValueKind.Null, LatestRunKind(loaded, "prescreen"));
    }

    [Fact]
    public async Task ReimportTb_AfterFilterCommit_PreservesGlOnlyFilterHits()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await CommitBroadScenarioAsync(host);
        var before = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM result_filter_run;");
        Assert.True(before > 0);

        var tbFile = await DemoProjectPipeline.DispatchDemoFixtureAsync("demo.exportTbFile");
        await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new
        {
            filePath = tbFile.GetProperty("filePath").GetString(),
            fileName = tbFile.GetProperty("fileName").GetString()
        }));

        Assert.Equal(before, await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM result_filter_run;"));
    }

    [Fact]
    public async Task ImportAccountMapping_AfterFilterCommit_ClearsHitsButPreservesScenarioDefinition()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, importAccountMapping: false);
        await CommitBroadScenarioAsync(host);
        Assert.True(await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM result_filter_run;") > 0);

        var file = await DemoProjectPipeline.DispatchDemoFixtureAsync(
            "demo.exportAccountMappingFile");
        await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
        {
            filePath = file.GetProperty("filePath").GetString(),
            fileName = file.GetProperty("fileName").GetString()
        }));

        Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM result_filter_run;"));
        Assert.Equal(1, await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM config_filter_scenario;"));
    }

    /// <summary>
    /// 原子性(plan Phase 1):結果清除與上游改寫同一交易,失敗即一併回退,不出現
    /// 「資料已換/已清、舊結果還在」或「舊結果已清、資料未換」的半態。
    /// oracle:科目配對 re-import 投影失敗(非法分類)→ projection_failed,
    /// 既有 target(100 列)與既有 prescreen 結果皆完整保留。
    /// </summary>
    [Fact]
    public async Task FailedAccountMappingReimport_RollsBackResultClearAndKeepsOldData()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, importAccountMapping: false);

        // 先成功匯入科目配對(target=100)並跑預篩選(結果已保存)。
        var goodFile = await DemoProjectPipeline.DispatchDemoFixtureAsync(
            "demo.exportAccountMappingFile");
        await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
        {
            filePath = goodFile.GetProperty("filePath").GetString(),
            fileName = goodFile.GetProperty("fileName").GetString()
        }));
        await host.DispatchAsync("prescreen.run");
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));

        // 失敗的 re-import:含非法分類,投影層整批 rollback。
        var badPath = Path.Combine(
            Path.GetTempPath(), "jet-invalidation-tests", Guid.NewGuid().ToString("N") + ".csv");
        Directory.CreateDirectory(Path.GetDirectoryName(badPath)!);
        await File.WriteAllTextAsync(badPath, "科目代號,科目名稱,標準化分類\n1101,現金,Cash\n9999,神祕科目,NotACategory\n");

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = badPath })));
        Assert.Equal("projection_failed", ex.Code);

        // 舊 target 完整保留(replace 清理與結果清除都隨交易回退)。
        Assert.Equal(DemoDataFactory.TbAccountCount, await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM target_account_mapping;"));
        // 舊結果一併保留:結果清除不在資料改寫成功之前「先行落地」。
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));
    }

    /// <summary>
    /// entry_id 紀律(plan Phase 2):重投影使 AUTOINCREMENT 的 entry_id 重新編號,但
    /// INF 抽樣以批次穩定的 source_row_number 排序,故同一 staging 重投影 + 重跑必得相同樣本。
    /// 同時驗證 Phase 1 不變量:重投影後、重跑前,抽樣表為空(舊樣本隨結果失效清除)。
    /// oracle:可重現性性質(metamorphic)—— 抽中的 (document_number, line_item) 集合不變。
    /// </summary>
    [Fact]
    public async Task RecommitGlMapping_SampleReproducesAcrossReprojection()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, runValidation: false);

        await host.DispatchAsync("validate.run");
        var before = await ReadSampleKeysAsync(host, context.ProjectId);
        Assert.Equal(59, before.Count); // 前置:正式 INF 表格固定 59 筆，樣本確已落地

        // 重投影(entry_id 全部重新編號)；Phase 1 使舊抽樣失效清除。
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                context.Demo.GetProperty("gl").GetProperty("mapping").GetRawText()),
            amountMode = context.Demo.GetProperty("gl").GetProperty("amountMode").GetString()
        }));

        Assert.Empty(await ReadSampleKeysAsync(host, context.ProjectId)); // Phase 1:失效清除

        await host.DispatchAsync("validate.run");
        var after = await ReadSampleKeysAsync(host, context.ProjectId);

        // source_row_number 排序穩定 → 重投影 + 重跑得到完全相同的抽樣身分。
        Assert.Equal(before, after);
    }

    /// <summary>讀取抽樣表的 (document_number, line_item) 身分清單(依抽樣排序鍵還原)。</summary>
    private static async Task<List<(string Doc, string Line)>> ReadSampleKeysAsync(
        HandlerTestHost host, string projectId)
    {
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        // 依身分排序(非插入序):比對的是「抽中哪些列」的集合,不綁抽樣 tie-break 的插入順序。
        command.CommandText =
            "SELECT document_number, line_item FROM result_inf_sampling_test_sample ORDER BY document_number, line_item;";

        var keys = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            keys.Add((reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
        }

        return keys;
    }

    private static async Task RunInvalidationMatrixAsync(
        string databaseProvider,
        string? sqlServerConnectionString,
        MatrixMutation mutation,
        bool clearsValidation,
        bool clearsPrescreen,
        bool clearsFilterHits)
    {
        using var host = new HandlerTestHost(sqlServerConnectionString: sqlServerConnectionString);
        try
        {
            var context = await DemoProjectPipeline.SetupAsync(
                host,
                databaseProvider: databaseProvider);
            var before = await SeedInvalidationMatrixAsync(
                host,
                context.ProjectId,
                databaseProvider,
                sqlServerConnectionString);

            await ExecuteMatrixMutationAsync(host, context, mutation);

            var after = await CaptureInvalidationMatrixStateAsync(
                host,
                context.ProjectId,
                databaseProvider,
                sqlServerConnectionString);
            AssertInvalidationMatrix(
                before,
                after,
                clearsValidation,
                clearsPrescreen,
                clearsFilterHits,
                expectedGlControlTotal: before.GlControlTotal);
        }
        finally
        {
            await CleanupSqlServerProjectsAsync(
                host.ProjectsRoot,
                databaseProvider,
                sqlServerConnectionString);
        }
    }

    private static async Task RunGlProjectionInvalidationAsync(
        string databaseProvider,
        string? sqlServerConnectionString)
    {
        using var host = new HandlerTestHost(sqlServerConnectionString: sqlServerConnectionString);
        try
        {
            var context = await DemoProjectPipeline.SetupAsync(
                host,
                databaseProvider: databaseProvider);
            var projected = await SeedInvalidationMatrixAsync(
                host,
                context.ProjectId,
                databaseProvider,
                sqlServerConnectionString);

            await ExecuteMatrixNonQueryAsync(
                host,
                context.ProjectId,
                databaseProvider,
                sqlServerConnectionString,
                """
                UPDATE {s}.gl_control_total
                SET source_row_count = -1,
                    target_row_count = -1,
                    target_debit_scaled = -1,
                    target_credit_scaled = -1
                WHERE singleton = 1;
                """);

            var sentinel = await CaptureInvalidationMatrixStateAsync(
                host,
                context.ProjectId,
                databaseProvider,
                sqlServerConnectionString);
            Assert.False(
                projected.GlControlTotal.SequenceEqual(sentinel.GlControlTotal, StringComparer.Ordinal),
                "測試前置失敗：gl_control_total sentinel 未生效。");

            await RecommitGlMappingAsync(host, context);

            var after = await CaptureInvalidationMatrixStateAsync(
                host,
                context.ProjectId,
                databaseProvider,
                sqlServerConnectionString);
            AssertInvalidationMatrix(
                sentinel,
                after,
                clearsValidation: true,
                clearsPrescreen: true,
                clearsFilterHits: true,
                expectedGlControlTotal: projected.GlControlTotal);
        }
        finally
        {
            await CleanupSqlServerProjectsAsync(
                host.ProjectsRoot,
                databaseProvider,
                sqlServerConnectionString);
        }
    }

    private static async Task<InvalidationMatrixState> SeedInvalidationMatrixAsync(
        HandlerTestHost host,
        string projectId,
        string databaseProvider,
        string? sqlServerConnectionString)
    {
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        await CommitBroadScenarioAsync(host);

        var state = await CaptureInvalidationMatrixStateAsync(
            host,
            projectId,
            databaseProvider,
            sqlServerConnectionString);
        Assert.NotNull(state.ValidationRun);
        Assert.NotNull(state.PrescreenRun);
        Assert.True(state.FilterHitCount > 0);
        Assert.Single(state.ScenarioDefinitions);
        Assert.Single(state.ScenarioRevisions);
        Assert.Equal(state.ScenarioRevisions[0], state.FilterResultRevision);
        Assert.Single(state.GlControlTotal);
        return state;
    }

    private static async Task<InvalidationMatrixState> CaptureInvalidationMatrixStateAsync(
        HandlerTestHost host,
        string projectId,
        string databaseProvider,
        string? sqlServerConnectionString)
    {
        var loaded = await LoadAsync(host, projectId);
        var definitions = await RequiredMatrixStringsAsync(
            host,
            projectId,
            databaseProvider,
            sqlServerConnectionString,
            "SELECT definition_json FROM {s}.config_filter_scenario ORDER BY position;");
        var revisions = await RequiredMatrixStringsAsync(
            host,
            projectId,
            databaseProvider,
            sqlServerConnectionString,
            "SELECT saved_utc FROM {s}.config_filter_scenario ORDER BY position;");
        var glControlTotal = await ReadGlControlTotalAsync(
            host,
            projectId,
            databaseProvider,
            sqlServerConnectionString,
            """
            SELECT source_row_count,
                   target_row_count,
                   target_debit_scaled,
                   target_credit_scaled
            FROM {s}.gl_control_total
            WHERE singleton = 1;
            """);
        var filterResultRef = loaded.GetProperty("filterResultRef");

        return new InvalidationMatrixState(
            RunJson(loaded, "validate"),
            RunJson(loaded, "prescreen"),
            await QueryMatrixScalarAsync(
                host,
                projectId,
                databaseProvider,
                sqlServerConnectionString,
                "SELECT COUNT(*) FROM {s}.result_filter_run;"),
            definitions,
            revisions,
            filterResultRef.ValueKind == JsonValueKind.Null
                ? null
                : filterResultRef.GetProperty("revision").GetString(),
            glControlTotal);
    }

    private static void AssertInvalidationMatrix(
        InvalidationMatrixState before,
        InvalidationMatrixState after,
        bool clearsValidation,
        bool clearsPrescreen,
        bool clearsFilterHits,
        string[] expectedGlControlTotal)
    {
        AssertRunState(before.ValidationRun, after.ValidationRun, clearsValidation);
        AssertRunState(before.PrescreenRun, after.PrescreenRun, clearsPrescreen);
        Assert.Equal(clearsFilterHits ? 0 : before.FilterHitCount, after.FilterHitCount);

        // Filter 失效只清 materialized hits；著作定義與 revision 必須逐字保留。
        Assert.Equal(before.ScenarioDefinitions, after.ScenarioDefinitions);
        Assert.Equal(before.ScenarioRevisions, after.ScenarioRevisions);
        Assert.Equal(before.FilterResultRevision, after.FilterResultRevision);

        // gl_control_total 不屬共用 reset；只有 GL projection 會在自己的交易內 upsert。
        Assert.Equal(expectedGlControlTotal, after.GlControlTotal);
    }

    private static void AssertRunState(string? before, string? after, bool clears)
    {
        if (clears)
        {
            Assert.Null(after);
        }
        else
        {
            Assert.Equal(before, after);
        }
    }

    private static string? RunJson(JsonElement loaded, string runKind)
    {
        var run = loaded.GetProperty("latestRuns").GetProperty(runKind);
        return run.ValueKind == JsonValueKind.Null ? null : run.GetRawText();
    }

    private static async Task<string[]> RequiredMatrixStringsAsync(
        HandlerTestHost host,
        string projectId,
        string databaseProvider,
        string? sqlServerConnectionString,
        string sql)
    {
        await using var connection = CreateMatrixConnection(
            host,
            projectId,
            databaseProvider,
            sqlServerConnectionString);
        await connection.OpenAsync();
        await using var command = CreateMatrixCommand(
            connection,
            projectId,
            databaseProvider,
            sqlServerConnectionString,
            sql);
        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(Assert.IsType<string>(reader.GetValue(0)));
        }

        return values.ToArray();
    }

    private static async Task<string[]> ReadGlControlTotalAsync(
        HandlerTestHost host,
        string projectId,
        string databaseProvider,
        string? sqlServerConnectionString,
        string sql)
    {
        await using var connection = CreateMatrixConnection(
            host,
            projectId,
            databaseProvider,
            sqlServerConnectionString);
        await connection.OpenAsync();
        await using var command = CreateMatrixCommand(
            connection,
            projectId,
            databaseProvider,
            sqlServerConnectionString,
            sql);
        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(string.Join(
                "|",
                Enumerable.Range(0, 4).Select(index =>
                    Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))));
        }

        return values.ToArray();
    }

    private static async Task<long> QueryMatrixScalarAsync(
        HandlerTestHost host,
        string projectId,
        string databaseProvider,
        string? sqlServerConnectionString,
        string sql)
    {
        await using var connection = CreateMatrixConnection(
            host,
            projectId,
            databaseProvider,
            sqlServerConnectionString);
        await connection.OpenAsync();
        await using var command = CreateMatrixCommand(
            connection,
            projectId,
            databaseProvider,
            sqlServerConnectionString,
            sql);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<int> ExecuteMatrixNonQueryAsync(
        HandlerTestHost host,
        string projectId,
        string databaseProvider,
        string? sqlServerConnectionString,
        string sql)
    {
        await using var connection = CreateMatrixConnection(
            host,
            projectId,
            databaseProvider,
            sqlServerConnectionString);
        await connection.OpenAsync();
        await using var command = CreateMatrixCommand(
            connection,
            projectId,
            databaseProvider,
            sqlServerConnectionString,
            sql);
        return await command.ExecuteNonQueryAsync();
    }

    private static DbConnection CreateMatrixConnection(
        HandlerTestHost host,
        string projectId,
        string databaseProvider,
        string? sqlServerConnectionString) =>
        databaseProvider switch
        {
            "sqlite" => new SqliteProjectDatabase(
                new JetProjectFolder(host.ProjectsRoot)).CreateConnection(projectId),
            "duckdb" => new DuckDbProjectDatabase(
                new JetProjectFolder(host.ProjectsRoot)).CreateConnection(projectId),
            "sqlServer" => new SqlServerProjectDatabase(
                new SqlServerConnectionOptions(sqlServerConnectionString)).CreateConnection(projectId),
            _ => throw new ArgumentOutOfRangeException(
                nameof(databaseProvider),
                databaseProvider,
                "未知的測試資料庫 provider。")
        };

    private static DbCommand CreateMatrixCommand(
        DbConnection connection,
        string projectId,
        string databaseProvider,
        string? sqlServerConnectionString,
        string sql)
    {
        if (databaseProvider == "sqlServer")
        {
            var database = new SqlServerProjectDatabase(
                new SqlServerConnectionOptions(sqlServerConnectionString));
            return database.CreateCommand((SqlConnection)connection, projectId, sql);
        }

        var command = connection.CreateCommand();
        command.CommandText = sql.Replace("{s}.", string.Empty, StringComparison.Ordinal);
        return command;
    }

    private static async Task CleanupSqlServerProjectsAsync(
        string projectsRoot,
        string databaseProvider,
        string? sqlServerConnectionString)
    {
        if (databaseProvider != "sqlServer"
            || string.IsNullOrWhiteSpace(sqlServerConnectionString)
            || !Directory.Exists(projectsRoot))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(projectsRoot))
        {
            await TempSqlServerProject.DropDatabaseAsync(
                sqlServerConnectionString,
                Path.GetFileName(directory));
        }
    }

    private static async Task ExecuteMatrixMutationAsync(
        HandlerTestHost host,
        DemoProjectPipeline.Context context,
        MatrixMutation mutation)
    {
        switch (mutation)
        {
            case MatrixMutation.GlImportReplace:
                await ImportDemoDatasetAsync(host, "gl", mode: "replace");
                break;
            case MatrixMutation.GlImportAppend:
                await ImportDemoDatasetAsync(host, "gl", mode: "append");
                break;
            case MatrixMutation.TbImportReplace:
                await ImportDemoDatasetAsync(host, "tb", mode: "replace");
                break;
            case MatrixMutation.TbImportAppend:
                await ImportDemoDatasetAsync(host, "tb", mode: "append");
                break;
            case MatrixMutation.TbProjection:
                await RecommitTbMappingAsync(host, context);
                break;
            case MatrixMutation.Calendar:
                await host.DispatchAsync(
                    "import.holiday",
                    """{ "dates": ["2025-02-28"] }""");
                break;
            case MatrixMutation.NonWorkingDays:
                await host.DispatchAsync(
                    "calendar.setNonWorkingDays",
                    """{ "days": [1] }""");
                break;
            case MatrixMutation.AccountMapping:
                await ImportReferenceFileAsync(
                    host,
                    "demo.exportAccountMappingFile",
                    "import.accountMapping.fromFile");
                break;
            case MatrixMutation.AuthorizedPreparer:
                await ImportReferenceFileAsync(
                    host,
                    "demo.exportAuthorizedPreparerFile",
                    "import.authorizedPreparer.fromFile");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
    }

    private static async Task ImportDemoDatasetAsync(
        HandlerTestHost host,
        string dataset,
        string mode)
    {
        var file = await DemoProjectPipeline.DispatchDemoFixtureAsync(
            dataset == "gl" ? "demo.exportGlFile" : "demo.exportTbFile");
        await host.DispatchAsync(
            $"import.{dataset}.fromFile",
            JsonSerializer.Serialize(new
            {
                filePath = file.GetProperty("filePath").GetString(),
                fileName = file.GetProperty("fileName").GetString(),
                mode
            }));
    }

    private static async Task ImportReferenceFileAsync(
        HandlerTestHost host,
        string exportAction,
        string importAction)
    {
        var file = await DemoProjectPipeline.DispatchDemoFixtureAsync(exportAction);
        await host.DispatchAsync(
            importAction,
            JsonSerializer.Serialize(new
            {
                filePath = file.GetProperty("filePath").GetString(),
                fileName = file.GetProperty("fileName").GetString()
            }));
    }

    private static Task<JsonElement> RecommitGlMappingAsync(
        HandlerTestHost host,
        DemoProjectPipeline.Context context) =>
        host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                context.Demo.GetProperty("gl").GetProperty("mapping").GetRawText()),
            amountMode = context.Demo.GetProperty("gl").GetProperty("amountMode").GetString()
        }));

    private static Task<JsonElement> RecommitTbMappingAsync(
        HandlerTestHost host,
        DemoProjectPipeline.Context context) =>
        host.DispatchAsync("mapping.commit.tb", JsonSerializer.Serialize(new
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                context.Demo.GetProperty("tb").GetProperty("mapping").GetRawText()),
            changeMode = context.Demo.GetProperty("tb").GetProperty("changeMode").GetString()
        }));

    private static Task<JsonElement> CommitBroadScenarioAsync(HandlerTestHost host) =>
        host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[]
            {
                new
                {
                    name = "失效範圍測試",
                    rationale = "以廣域金額條件建立可觀察命中",
                    groups = new[]
                    {
                        new
                        {
                            join = "AND",
                            rules = new[]
                            {
                                new { join = "AND", type = "numRange", field = "amount", from = "0" }
                            }
                        }
                    }
                }
            }
        }));

    private static Task<JsonElement> CommitWeekendScenarioAsync(HandlerTestHost host) =>
        host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[]
            {
                new
                {
                    name = "非工作日失效測試",
                    rationale = "鎖住非工作日設定與已存命中的依賴",
                    groups = new[]
                    {
                        new
                        {
                            join = "AND",
                            rules = new[]
                            {
                                new { join = "AND", type = "prescreen", prescreenKey = "weekendPosting" }
                            }
                        }
                    }
                }
            }
        }));
}
