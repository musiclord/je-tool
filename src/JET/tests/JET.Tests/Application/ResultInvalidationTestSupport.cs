using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 結果失效驗收：規則執行結果(validation_result、prescreen_result 與抽樣)是衍生資料,
/// 一旦其實際上游被改寫，project.load 的 validationResult 或 prescreenResult 必須回 null（要求重跑），
/// GL 與 TB 同層級修改，影響兩種 run，
/// 科目配對／行事曆／授權清單只影響 prescreen。
///
/// 設計技術：狀態轉換 —— 每條合法的「上游改寫」轉換各一測試,涵蓋三個失效機制:
///   (a) 匯入 replace 清理交易（GL/TB/科目配對 re-import）
///   (b) 重投影交易（mapping re-commit）
///   (c) 行事曆 replace 交易（假日/補班匯入）
/// oracle：依賴到該上游的 latest run 應為 JSON null；無依賴者必須保留。篩選命中失效時，
/// 已存篩選情境也一起清掉（上游修改清除下游）。
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
        string? ValidationResultJson,
        string? PrescreenResultJson,
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

    internal static async Task<JsonElement> LoadAsync(HandlerTestHost host, string projectId)
    {
        return await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId }));
    }

    /// <summary>project.load 回應裡放驗證結果或預篩選結果的欄位；resultKind 是 "validate" 或 "prescreen"。</summary>
    internal static string LoadedResultProperty(string resultKind) => resultKind switch
    {
        "validate" => "validationResult",
        "prescreen" => "prescreenResult",
        _ => throw new ArgumentOutOfRangeException(nameof(resultKind), resultKind, null)
    };

    /// <summary>存驗證結果或預篩選結果的資料表；resultKind 是 "validate" 或 "prescreen"。</summary>
    internal static string ResultTable(string resultKind) => resultKind switch
    {
        "validate" => "validation_result",
        "prescreen" => "prescreen_result",
        _ => throw new ArgumentOutOfRangeException(nameof(resultKind), resultKind, null)
    };

    internal static JsonValueKind LatestResultKind(JsonElement loaded, string resultKind)
    {
        return loaded.GetProperty(LoadedResultProperty(resultKind)).ValueKind;
    }

    /// <summary>讀取抽樣表的 (voucher_number, line_item) 身分清單(依抽樣排序鍵還原)。</summary>
    internal static async Task<List<(string Doc, string Line)>> ReadSampleKeysAsync(
        HandlerTestHost host, string projectId)
    {
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        // 依身分排序(非插入序):比對的是「抽中哪些列」的集合,不綁抽樣 tie-break 的插入順序。
        command.CommandText =
            "SELECT voucher_number, line_item FROM inf_sample ORDER BY voucher_number, line_item;";

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
        MatrixMutation mutation,
        bool clearsValidation,
        bool clearsPrescreen,
        bool clearsFilterHits)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(
            host,
            databaseProvider: databaseProvider);
        var before = await SeedInvalidationMatrixAsync(
            host,
            context.ProjectId,
            databaseProvider);

        await ExecuteMatrixMutationAsync(host, context, mutation);

        var after = await CaptureInvalidationMatrixStateAsync(
            host,
            context.ProjectId,
            databaseProvider);
        AssertInvalidationMatrix(
            before,
            after,
            clearsValidation,
            clearsPrescreen,
            clearsFilterHits,
            expectedGlControlTotal: before.GlControlTotal);
    }

    internal static async Task RunGlProjectionInvalidationAsync(
        string databaseProvider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(
            host,
            databaseProvider: databaseProvider);
        var projected = await SeedInvalidationMatrixAsync(
            host,
            context.ProjectId,
            databaseProvider);

        await ExecuteMatrixNonQueryAsync(
            host,
            context.ProjectId,
            databaseProvider,
            """
            UPDATE {s}.gl_control_total
            SET pbc_row_count = -1,
                stored_row_count = -1,
                stored_debit_scaled = -1,
                stored_credit_scaled = -1
            WHERE singleton = 1;
            """);

        var sentinel = await CaptureInvalidationMatrixStateAsync(
            host,
            context.ProjectId,
            databaseProvider);
        Assert.False(
            projected.GlControlTotal.SequenceEqual(sentinel.GlControlTotal, StringComparer.Ordinal),
            "測試前置失敗：gl_control_total sentinel 未生效。");

        await RecommitGlMappingAsync(host, context);

        var after = await CaptureInvalidationMatrixStateAsync(
            host,
            context.ProjectId,
            databaseProvider);
        AssertInvalidationMatrix(
            sentinel,
            after,
            clearsValidation: true,
            clearsPrescreen: true,
            clearsFilterHits: true,
            expectedGlControlTotal: projected.GlControlTotal);
    }

    internal static async Task<InvalidationMatrixState> SeedInvalidationMatrixAsync(
        HandlerTestHost host,
        string projectId,
        string databaseProvider)
    {
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        await CommitBroadScenarioAsync(host);

        var state = await CaptureInvalidationMatrixStateAsync(
            host,
            projectId,
            databaseProvider);
        Assert.NotNull(state.ValidationResultJson);
        Assert.NotNull(state.PrescreenResultJson);
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
        string databaseProvider)
    {
        var loaded = await LoadAsync(host, projectId);
        var definitions = await RequiredMatrixStringsAsync(
            host,
            projectId,
            databaseProvider,
            "SELECT definition_json FROM {s}.filter_scenario ORDER BY position;");
        // 每個情境各自儲存，情境的版本是「情境編號@保存時間」，整批版本依順序用分號接起來。
        var savedTimes = await RequiredMatrixStringsAsync(
            host,
            projectId,
            databaseProvider,
            "SELECT saved_utc FROM {s}.filter_scenario ORDER BY position;");
        var scenarioIds = await RequiredMatrixStringsAsync(
            host,
            projectId,
            databaseProvider,
            "SELECT CAST(scenario_id AS VARCHAR(20)) FROM {s}.filter_scenario ORDER BY position;");
        var revisions = scenarioIds.Zip(savedTimes, (id, savedUtc) => id + "@" + savedUtc).ToArray();
        var glControlTotal = await ReadGlControlTotalAsync(
            host,
            projectId,
            databaseProvider,
            """
            SELECT pbc_row_count,
                   stored_row_count,
                   stored_debit_scaled,
                   stored_credit_scaled
            FROM {s}.gl_control_total
            WHERE singleton = 1;
            """);
        var filterResultRef = loaded.GetProperty("filterResultRef");

        return new InvalidationMatrixState(
            ResultJson(loaded, "validate"),
            ResultJson(loaded, "prescreen"),
            await QueryMatrixScalarAsync(
                host,
                projectId,
                databaseProvider,
                "SELECT COUNT(*) FROM {s}.filter_hit;"),
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
        AssertResultState(before.ValidationResultJson, after.ValidationResultJson, clearsValidation);
        AssertResultState(before.PrescreenResultJson, after.PrescreenResultJson, clearsPrescreen);
        Assert.Equal(clearsFilterHits ? 0 : before.FilterHitCount, after.FilterHitCount);

        // 上游修改會清除下游：命中失效時，已存情境定義與版本也一起清掉；
        // 若政策保留命中，定義與版本也必須逐字保留。
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

    internal static void AssertResultState(string? before, string? after, bool clears)
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

    internal static string? ResultJson(JsonElement loaded, string resultKind)
    {
        var run = loaded.GetProperty(LoadedResultProperty(resultKind));
        return run.ValueKind == JsonValueKind.Null ? null : run.GetRawText();
    }

    internal static async Task<string[]> RequiredMatrixStringsAsync(
        HandlerTestHost host,
        string projectId,
        string databaseProvider,
        string sql)
    {
        await using var connection = CreateMatrixConnection(
            host,
            projectId,
            databaseProvider);
        await connection.OpenAsync();
        await using var command = CreateMatrixCommand(
            connection,
            projectId,
            databaseProvider,
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
        string sql)
    {
        await using var connection = CreateMatrixConnection(
            host,
            projectId,
            databaseProvider);
        await connection.OpenAsync();
        await using var command = CreateMatrixCommand(
            connection,
            projectId,
            databaseProvider,
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
        string sql)
    {
        await using var connection = CreateMatrixConnection(
            host,
            projectId,
            databaseProvider);
        await connection.OpenAsync();
        await using var command = CreateMatrixCommand(
            connection,
            projectId,
            databaseProvider,
            sql);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    internal static async Task<int> ExecuteMatrixNonQueryAsync(
        HandlerTestHost host,
        string projectId,
        string databaseProvider,
        string sql)
    {
        await using var connection = CreateMatrixConnection(
            host,
            projectId,
            databaseProvider);
        await connection.OpenAsync();
        await using var command = CreateMatrixCommand(
            connection,
            projectId,
            databaseProvider,
            sql);
        return await command.ExecuteNonQueryAsync();
    }

    internal static DbConnection CreateMatrixConnection(
        HandlerTestHost host,
        string projectId,
        string databaseProvider) =>
        databaseProvider switch
        {
            "sqlite" => new SqliteProjectDatabase(
                new JetProjectFolder(host.ProjectsRoot)).CreateConnection(projectId),
            _ => throw new ArgumentOutOfRangeException(
                nameof(databaseProvider),
                databaseProvider,
                "未知的測試資料庫 provider。")
        };

    internal static DbCommand CreateMatrixCommand(
        DbConnection connection,
        string projectId,
        string databaseProvider,
        string sql)
    {

        var command = connection.CreateCommand();
        command.CommandText = sql.Replace("{s}.", string.Empty, StringComparison.Ordinal);
        return command;
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
                // 匯入人員清單必須指定 sourceColumn。
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
                            conditions = new[]
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
                            conditions = new[]
                            {
                                new { join = "AND", type = "prescreen", prescreenKey = "weekendPosting" }
                            }
                        }
                    }
                }
            }
        }));
}
