using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 裁定 2026-10-02-05：JET 更新篩選規則後不再要求逐案重新儲存情境。開案時整批仍符合目前規則就自動改版，
/// 只要有一個不符合，整批都不動並列出問題。SQLite 與 DuckDB 都要成立。
/// </summary>
public sealed class FilterScenarioRuleUpgradeOnLoadTests
{
    private const string OlderVersion = "filter-2026-09-18-v15";

    private sealed record StoredScenario(int Position, string Name, string Rationale, string DefinitionJson, string SavedUtc);

    [Theory]
    [InlineData("sqlite")]
    public async Task OlderValidScenario_IsRecalculatedWithCurrentRulesOnLoad(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await CommitAsync(host, DebitScenario("借方情境"));
        await RewriteDefinitionAsync(host, context.ProjectId, provider, 1, definition =>
        {
            definition["logicVersion"] = OlderVersion;
            definition.Remove("populationScope");
        });
        await ExecuteAsync(host, context.ProjectId, provider, "DELETE FROM filter_hit;");
        var before = Assert.Single(await ReadScenariosAsync(host, context.ProjectId, provider));

        var loaded = await LoadAsync(host, context.ProjectId);

        var check = loaded.GetProperty("filterScenarioCheck");
        Assert.Equal("recalculated", check.GetProperty("status").GetString());
        Assert.Equal(1, check.GetProperty("recalculatedCount").GetInt32());
        Assert.Empty(check.GetProperty("problems").EnumerateArray());
        Assert.True(loaded.GetProperty("staleState").GetProperty("filter").GetBoolean());

        var after = Assert.Single(await ReadScenariosAsync(host, context.ProjectId, provider));
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.Rationale, after.Rationale);
        Assert.NotEqual(before.SavedUtc, after.SavedUtc);
        var resultRef = loaded.GetProperty("filterResultRef");
        // 2026-10-31 起 revision 是「情境編號@保存時間」，保存時間在 generatedUtc；原本斷言 revision 就是保存時間，
        // 第一次失敗的收據 20261031-015438711-e019d04ff2b64ad08bc34c916334bd83。
        Assert.Equal("1@" + after.SavedUtc, resultRef.GetProperty("revision").GetString());
        Assert.Equal(after.SavedUtc, resultRef.GetProperty("generatedUtc").GetString());
        Assert.Equal(RuleLogicVersions.Filter, resultRef.GetProperty("logicVersion").GetString());

        var beforeJson = JsonNode.Parse(before.DefinitionJson)!.AsObject();
        var afterJson = JsonNode.Parse(after.DefinitionJson)!.AsObject();
        Assert.Equal(RuleLogicVersions.Filter, afterJson["logicVersion"]!.GetValue<string>());
        Assert.Equal(GlPopulationScopeValues.AuditPeriod, afterJson["populationScope"]!.GetValue<string>());
        beforeJson.Remove("logicVersion");
        afterJson.Remove("logicVersion");
        afterJson.Remove("populationScope");
        Assert.True(JsonNode.DeepEquals(beforeJson, afterJson));

        Assert.Equal(0, await ScalarAsync(host, context.ProjectId, provider, "SELECT COUNT(*) FROM filter_hit;"));
        Assert.Equal(1, await ScalarAsync(host, context.ProjectId, provider,
            "SELECT filter_stale FROM result_stale_state WHERE singleton=1;"));

        var page = await host.DispatchAsync(
            "query.filterHitsPage", JsonSerializer.Serialize(new { scenarioPosition = 1, pageSize = 20 }));
        Assert.NotEmpty(page.GetProperty("rows").EnumerateArray());
        Assert.True(await ScalarAsync(host, context.ProjectId, provider, "SELECT COUNT(*) FROM filter_hit;") > 0);
    }

    // 已移除的排除區原本也是一種失效寫法；裁定 2026-10-30-25 拿掉讀回舊寫法的程式後，排除區不再被當成失效，
    // 這兩列跟著拿掉（第一次失敗收據 20261030-072351291-9e3e967b81c8442aa4708c391db02bbf）。
    public static TheoryData<string, string> InvalidVariants => new()
    {
        { "sqlite", "missingRdeField" }
    };

    [Theory]
    [MemberData(nameof(InvalidVariants))]
    public async Task OlderScenarioThatNoLongerFitsCurrentRules_IsLeftUntouchedAndListed(string provider, string variant)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await CommitAsync(host, DebitScenario("借方情境"));
        await RewriteDefinitionAsync(host, context.ProjectId, provider, 1, definition =>
        {
            definition["logicVersion"] = OlderVersion;
            // 排除區那一列拿掉後只剩這一種失效寫法：條件指向已不存在的攸關資料元素。
            Assert.Equal("missingRdeField", variant);
            definition["groups"]![0]!["conditions"]!.AsArray().Add(JsonNode.Parse(
                """{"join":"AND","type":"typed","fieldId":"rde.aaaa0000aaaa0000aaaa0000aaaa0000","operator":"isBlank"}"""));
        });
        var before = await ReadScenariosAsync(host, context.ProjectId, provider);

        var loaded = await LoadAsync(host, context.ProjectId);

        var check = loaded.GetProperty("filterScenarioCheck");
        Assert.Equal("needsEdit", check.GetProperty("status").GetString());
        Assert.Equal(0, check.GetProperty("recalculatedCount").GetInt32());
        var problem = Assert.Single(check.GetProperty("problems").EnumerateArray());
        Assert.Equal(1, problem.GetProperty("position").GetInt32());
        Assert.Equal("借方情境", problem.GetProperty("name").GetString());
        Assert.NotEmpty(problem.GetProperty("messages").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("filterResultRef").ValueKind);
        Assert.Equal(before, await ReadScenariosAsync(host, context.ProjectId, provider));

        var stale = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "query.filterHitsPage", JsonSerializer.Serialize(new { scenarioPosition = 1 })));
        Assert.Equal(JetErrorCodes.StaleResult, stale.Code);
        Assert.Equal(FilterScenarioRuleMessages.NotYetUpgraded, stale.Message);
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task OneInvalidScenario_KeepsTheWholeBatchUntouched(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await CommitAsync(host, DebitScenario("借方情境"), DebitScenario("第二情境"));
        foreach (var position in new[] { 1, 2 })
        {
            await RewriteDefinitionAsync(host, context.ProjectId, provider, position, definition =>
            {
                definition["logicVersion"] = OlderVersion;
                // 第二個情境原本靠已移除的排除區失效；排除區不再被讀回後，改用指向已不存在的攸關資料元素的條件
                // （裁定 2026-10-30-25；第一次失敗收據 20261030-072351291-9e3e967b81c8442aa4708c391db02bbf）。
                if (position == 2)
                {
                    definition["groups"]![0]!["conditions"]!.AsArray().Add(JsonNode.Parse(
                        """{"join":"AND","type":"typed","fieldId":"rde.aaaa0000aaaa0000aaaa0000aaaa0000","operator":"isBlank"}"""));
                }
            });
        }
        var before = await ReadScenariosAsync(host, context.ProjectId, provider);

        var loaded = await LoadAsync(host, context.ProjectId);

        var check = loaded.GetProperty("filterScenarioCheck");
        Assert.Equal("needsEdit", check.GetProperty("status").GetString());
        var problem = Assert.Single(check.GetProperty("problems").EnumerateArray());
        Assert.Equal(2, problem.GetProperty("position").GetInt32());
        Assert.Equal("第二情境", problem.GetProperty("name").GetString());
        Assert.Equal(before, await ReadScenariosAsync(host, context.ProjectId, provider));
        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("filterResultRef").ValueKind);
    }

    public static TheoryData<string, string> UnknownVersions => new()
    {
        { "sqlite", "filter-2099-01-01-v99" },
        { "sqlite", "legacy-rules-v3" }
    };

    [Theory]
    [MemberData(nameof(UnknownVersions))]
    public async Task UnknownOrNewerVersion_IsNotUpgraded(string provider, string version)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await CommitAsync(host, DebitScenario("借方情境"));
        await RewriteDefinitionAsync(host, context.ProjectId, provider, 1, definition =>
            definition["logicVersion"] = version);
        var before = await ReadScenariosAsync(host, context.ProjectId, provider);

        var loaded = await LoadAsync(host, context.ProjectId);

        var check = loaded.GetProperty("filterScenarioCheck");
        Assert.Equal("needsEdit", check.GetProperty("status").GetString());
        var problem = Assert.Single(check.GetProperty("problems").EnumerateArray());
        Assert.Contains(
            FilterScenarioRuleUpgrade.UnknownVersionMessage,
            problem.GetProperty("messages").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(before, await ReadScenariosAsync(host, context.ProjectId, provider));
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task OlderVersionWithNonAuditPeriodScope_IsNotUpgraded(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await CommitAsync(host, DebitScenario("借方情境"));
        await RewriteDefinitionAsync(host, context.ProjectId, provider, 1, definition =>
        {
            definition["logicVersion"] = OlderVersion;
            definition["populationScope"] = "allProjected";
        });
        var before = await ReadScenariosAsync(host, context.ProjectId, provider);

        var loaded = await LoadAsync(host, context.ProjectId);

        var check = loaded.GetProperty("filterScenarioCheck");
        Assert.Equal("needsEdit", check.GetProperty("status").GetString());
        var problem = Assert.Single(check.GetProperty("problems").EnumerateArray());
        Assert.Contains(
            FilterScenarioRuleUpgrade.NonAuditPeriodMessage,
            problem.GetProperty("messages").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(before, await ReadScenariosAsync(host, context.ProjectId, provider));
        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("filterResultRef").ValueKind);
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task Upgrade_MarksCriteriaReportAndWorkingPaperStaleWithoutTouchingFiles(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var validation = await host.DispatchAsync("validate.run");
        var validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString();
        var prescreen = await host.DispatchAsync("prescreen.run");
        var prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString();
        var committed = await CommitAsync(host, DebitScenario("借方情境"));
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString();
        await host.DispatchAsync("export.criteriaSelectionReport",
            JsonSerializer.Serialize(new { validationResultId, prescreenResultId, revision }));
        await host.DispatchAsync("export.workingPaperStream", JsonSerializer.Serialize(new
        {
            validationResultId, prescreenResultId, scenarioRevision = revision, scenarioPositions = new[] { 1 }
        }));

        var current = await LoadAsync(host, context.ProjectId);
        Assert.Equal("current", current.GetProperty("filterScenarioCheck").GetProperty("status").GetString());
        var reports = FilterReports(current);
        Assert.Equal(2, reports.Length);
        Assert.All(reports, report => Assert.False(report.GetProperty("stale").GetBoolean()));
        var hashes = reports.ToDictionary(
            report => report.GetProperty("fullPath").GetString()!,
            report => Sha256(report.GetProperty("fullPath").GetString()!));

        await RewriteDefinitionAsync(host, context.ProjectId, provider, 1, definition =>
            definition["logicVersion"] = OlderVersion);

        var upgraded = await LoadAsync(host, context.ProjectId);

        Assert.Equal("recalculated", upgraded.GetProperty("filterScenarioCheck").GetProperty("status").GetString());
        var staleReports = FilterReports(upgraded);
        Assert.Equal(2, staleReports.Length);
        Assert.All(staleReports, report => Assert.True(report.GetProperty("stale").GetBoolean()));
        foreach (var (path, hash) in hashes)
        {
            Assert.Equal(hash, Sha256(path));
        }
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task SecondLoadAfterUpgrade_IsCurrentAndChangesNothing(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await CommitAsync(host, DebitScenario("借方情境"));
        await RewriteDefinitionAsync(host, context.ProjectId, provider, 1, definition =>
            definition["logicVersion"] = OlderVersion);

        var first = await LoadAsync(host, context.ProjectId);
        Assert.Equal("recalculated", first.GetProperty("filterScenarioCheck").GetProperty("status").GetString());
        var afterFirst = await ReadScenariosAsync(host, context.ProjectId, provider);

        var second = await LoadAsync(host, context.ProjectId);

        var check = second.GetProperty("filterScenarioCheck");
        Assert.Equal("current", check.GetProperty("status").GetString());
        Assert.Equal(0, check.GetProperty("recalculatedCount").GetInt32());
        Assert.Equal(afterFirst, await ReadScenariosAsync(host, context.ProjectId, provider));
        Assert.Equal(
            first.GetProperty("filterResultRef").GetProperty("revision").GetString(),
            second.GetProperty("filterResultRef").GetProperty("revision").GetString());
    }

    private static JsonElement[] FilterReports(JsonElement loaded) =>
        loaded.GetProperty("reports").EnumerateArray()
            .Where(item => item.GetProperty("kind").GetString() is "criteriaSelectionReport" or "workingPaper")
            .ToArray();

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static object DebitScenario(string name) => new
    {
        name,
        rationale = "合成規則改版測試",
        groups = new[] { new { join = "AND", conditions = new[] { new { join = "AND", type = "drCrOnly", drCr = "debit" } } } }
    };

    private static Task<JsonElement> CommitAsync(HandlerTestHost host, params object[] scenarios) =>
        host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios }));

    private static Task<JsonElement> LoadAsync(HandlerTestHost host, string projectId) =>
        host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));

    private static ILocalProjectDatabase Database(HandlerTestHost host, string provider)
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        return new SqliteProjectDatabase(folder);
    }

    private static async Task<IReadOnlyList<StoredScenario>> ReadScenariosAsync(
        HandlerTestHost host, string projectId, string provider)
    {
        await using var connection = Database(host, provider).CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT position, name, rationale, definition_json, saved_utc FROM filter_scenario ORDER BY position;";
        var rows = new List<StoredScenario>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new StoredScenario(
                Convert.ToInt32(reader.GetValue(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return rows;
    }

    private static async Task RewriteDefinitionAsync(
        HandlerTestHost host, string projectId, string provider, int position, Action<JsonObject> mutate)
    {
        var stored = (await ReadScenariosAsync(host, projectId, provider)).Single(item => item.Position == position);
        var definition = JsonNode.Parse(stored.DefinitionJson)!.AsObject();
        mutate(definition);
        await ExecuteAsync(host, projectId, provider,
            "UPDATE filter_scenario SET definition_json = @definition WHERE position = @position;",
            ("@definition", definition.ToJsonString()),
            ("@position", (long)position));
    }

    private static async Task ExecuteAsync(
        HandlerTestHost host, string projectId, string provider, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = Database(host, provider).CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(HandlerTestHost host, string projectId, string provider, string sql)
    {
        await using var connection = Database(host, provider).CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
