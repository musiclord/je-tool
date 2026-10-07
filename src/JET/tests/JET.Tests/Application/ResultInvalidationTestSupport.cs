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
/// 依同層級修改裁定，GL 與 TB 影響兩種 run，
/// 科目配對／行事曆／授權清單只影響 prescreen。
///
/// 設計技術：狀態轉換 —— 每條合法的「上游改寫」轉換各一測試,涵蓋三個失效機制:
///   (a) 匯入 replace 清理交易（GL/TB/科目配對 re-import）
///   (b) 重投影交易（mapping re-commit）
///   (c) 行事曆 replace 交易（假日/補班匯入）
/// oracle：依賴到該上游的 latest run 應為 JSON null；無依賴者必須保留。篩選命中失效時，
/// 已存篩選情境也一起清掉（使用者 2026-10-07 裁定上游修改清除下游）。
/// 每個測試自建 host（會變更狀態,不可共用 DemoProjectFixture）。
/// </summary>
public static class ResultInvalidationTestSupport
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

    internal sealed record InvalidationMatrixState(
        string? ValidationRun,
        string? PrescreenRun,
        long FilterHitCount,
        string[] ScenarioDefinitions,
        string[] ScenarioRevisions,
        string? FilterResultRevision,
        string[] GlControlTotal);

    internal static readonly (MatrixMutation Mutation, bool Validation, bool Prescreen, bool FilterHits)[]
        MatrixExpectations =
        [
            (MatrixMutation.GlImportReplace, true, true, true),
            (MatrixMutation.GlImportAppend, true, true, true),
            (MatrixMutation.TbImportReplace, true, true, true),
            (MatrixMutation.TbImportAppend, true, true, true),
            (MatrixMutation.TbProjection, true, true, true),
            (MatrixMutation.Calendar, false, true, true),
            (MatrixMutation.NonWorkingDays, false, true, true),
            (MatrixMutation.AccountMapping, false, true, true),
            (MatrixMutation.AuthorizedPreparer, false, true, true)
        ];

    public static IEnumerable<object[]> LocalMatrixCases(string provider)
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

    public static IEnumerable<object[]> SqlServerMatrixCases() =>
        MatrixExpectations.Select(expected => new object[]
        {
            expected.Mutation,
            expected.Validation,
            expected.Prescreen,
            expected.FilterHits
        });

    internal static async Task<JsonElement> LoadAsync(HandlerTestHost host, string projectId)
    {
        return await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId }));
    }

    internal static JsonValueKind LatestRunKind(JsonElement loaded, string runKind)
    {
        return loaded.GetProperty("latestRuns").GetProperty(runKind).ValueKind;
    }

    /// <summary>讀取抽樣表的 (document_number, line_item) 身分清單(依抽樣排序鍵還原)。</summary>
    internal static async Task<List<(string Doc, string Line)>> ReadSampleKeysAsync(
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

    internal static async Task RunInvalidationMatrixAsync(
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

    internal static async Task RunGlProjectionInvalidationAsync(
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

    internal static async Task<InvalidationMatrixState> SeedInvalidationMatrixAsync(
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

    internal static async Task<InvalidationMatrixState> CaptureInvalidationMatrixStateAsync(
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

    internal static void AssertInvalidationMatrix(
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

        // 使用者 2026-10-07 裁定上游修改清除下游：命中失效時，已存情境定義與版本也一起清掉；
        // 若政策保留命中，定義與版本也必須逐字保留。原本斷言情境一律保留，第一次失敗收據
        // 20261007-032952783-7147565fec1c42be845dac22acf7263b。
        if (clearsFilterHits)
        {
            Assert.Empty(after.ScenarioDefinitions);
            Assert.Empty(after.ScenarioRevisions);
            Assert.Null(after.FilterResultRevision);
        }
        else
        {
            Assert.Equal(before.ScenarioDefinitions, after.ScenarioDefinitions);
            Assert.Equal(before.ScenarioRevisions, after.ScenarioRevisions);
            Assert.Equal(before.FilterResultRevision, after.FilterResultRevision);
        }

        // gl_control_total 不屬共用 reset；只有 GL projection 會在自己的交易內 upsert。
        Assert.Equal(expectedGlControlTotal, after.GlControlTotal);
    }

    internal static void AssertRunState(string? before, string? after, bool clears)
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

    internal static string? RunJson(JsonElement loaded, string runKind)
    {
        var run = loaded.GetProperty("latestRuns").GetProperty(runKind);
        return run.ValueKind == JsonValueKind.Null ? null : run.GetRawText();
    }

    internal static async Task<string[]> RequiredMatrixStringsAsync(
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

    internal static async Task<string[]> ReadGlControlTotalAsync(
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

    internal static async Task<long> QueryMatrixScalarAsync(
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

    internal static async Task<int> ExecuteMatrixNonQueryAsync(
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

    internal static DbConnection CreateMatrixConnection(
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

    internal static DbCommand CreateMatrixCommand(
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

    internal static async Task CleanupSqlServerProjectsAsync(
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

    internal static async Task ExecuteMatrixMutationAsync(
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
                    "import.authorizedPreparer.fromFile",
                    sourceColumn: "姓名");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
    }

    internal static async Task ImportDemoDatasetAsync(
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

    internal static async Task ImportReferenceFileAsync(
        HandlerTestHost host,
        string exportAction,
        string importAction,
        string? sourceColumn = null)
    {
        var file = await DemoProjectPipeline.DispatchDemoFixtureAsync(exportAction);
        await host.DispatchAsync(
            importAction,
            JsonSerializer.Serialize(new
            {
                filePath = file.GetProperty("filePath").GetString(),
                fileName = file.GetProperty("fileName").GetString(),
                // 2026-10-04 第 3 批 L12 裁定 sourceColumn 必填；保留上游改動的結果失效測試。
                sourceColumn
            }));
    }

    internal static Task<JsonElement> RecommitGlMappingAsync(
        HandlerTestHost host,
        DemoProjectPipeline.Context context) =>
        host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                context.Demo.GetProperty("gl").GetProperty("mapping").GetRawText()),
            amountMode = context.Demo.GetProperty("gl").GetProperty("amountMode").GetString()
        }));

    internal static Task<JsonElement> RecommitTbMappingAsync(
        HandlerTestHost host,
        DemoProjectPipeline.Context context) =>
        host.DispatchAsync("mapping.commit.tb", JsonSerializer.Serialize(new
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                context.Demo.GetProperty("tb").GetProperty("mapping").GetRawText()),
            changeMode = context.Demo.GetProperty("tb").GetProperty("changeMode").GetString()
        }));

    internal static Task<JsonElement> CommitBroadScenarioAsync(HandlerTestHost host) =>
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

    internal static Task<JsonElement> CommitWeekendScenarioAsync(HandlerTestHost host) =>
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
