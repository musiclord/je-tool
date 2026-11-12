using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// JET 更新篩選規則後不要求逐案重新儲存情境。開案時整批仍符合目前規則就自動改版，
/// 只要有一個不符合，整批都不動並列出問題。
/// </summary>
public sealed class FilterScenarioRuleUpgradeOnLoadTests
{
    [Theory]
    [InlineData("groupsNotArray")]
    [InlineData("groupNotObject")]
    [InlineData("conditionsMissing")]
    [InlineData("conditionsNotArray")]
    [InlineData("nestedGroupMissingConditions")]
    [InlineData("nestedVoucherMissingConditions")]
    // 巢狀條件缺子條件時仍須能移除；
    [InlineData("deepUnusedMetadata")]
    public async Task DefinitionWithUnreadableStructure_UsesEmptyGroupsAndCanBeRemoved(string damage)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await CommitAsync(host, DebitScenario("合成結構情境"), DebitScenario("正常情境"));
        await RewriteDefinitionAsync(host, context.ProjectId, "sqlite", 1, definition =>
        {
            switch (damage)
            {
                case "groupsNotArray": definition["groups"] = new JsonObject(); break;
                case "groupNotObject": definition["groups"]![0] = 0; break;
                case "conditionsMissing": definition["groups"]![0]!.AsObject().Remove("conditions"); break;
                case "conditionsNotArray": definition["groups"]![0]!["conditions"] = new JsonObject(); break;
                case "nestedGroupMissingConditions":
                    definition["groups"]![0]!["conditions"] = new JsonArray(new JsonObject { ["type"] = "group" });
                    break;
                case "nestedVoucherMissingConditions":
                    definition["groups"]![0]!["conditions"] = new JsonArray(new JsonObject { ["type"] = "voucher" });
                    break;
                case "deepUnusedMetadata":
                    definition["editorOrigins"] = JsonNode.Parse(new string('[', 62) + "0" + new string(']', 62));
                    break;
            }
        });
        var before = await ReadScenariosAsync(host, context.ProjectId, "sqlite");

        var loaded = await LoadAsync(host, context.ProjectId);

        Assert.Equal("needsEdit", loaded.GetProperty("filterScenarioCheck").GetProperty("status").GetString());
        var problem = Assert.Single(loaded.GetProperty("filterScenarioCheck").GetProperty("problems").EnumerateArray());
        Assert.Equal("條件內容無法讀取", Assert.Single(problem.GetProperty("messages").EnumerateArray()).GetString());
        Assert.Empty(loaded.GetProperty("filterScenarios")[0].GetProperty("groups").EnumerateArray());
        Assert.Equal(before, await ReadScenariosAsync(host, context.ProjectId, "sqlite"));
        var removed = await host.DispatchAsync("filter.scenario.remove", """{"position":1,"expectedName":"合成結構情境"}""");
        Assert.Equal("正常情境", Assert.Single(removed.GetProperty("scenarios").EnumerateArray()).GetProperty("name").GetString());
    }

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
        // revision 是「情境編號@保存時間」，保存時間在 generatedUtc。
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
            // 這組變體只有一種失效寫法：條件指向已不存在的攸關資料元素。
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
                // 第二個情境用指向已不存在的攸關資料元素的條件讓它失效。
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

    /// <summary>
    /// 已存情境的定義裡有讀不出來的文字（不成對代理字元的跳脫寫法）時，不論出現在篩選範圍、規則版本、來源、條件、
    /// 編輯來源、規則版本較舊的情境裡沒有用到的欄位或欄名，都和整份讀不出來的情境一樣：開案照常，這個情境列成「條件內容無法讀取」、
    /// 條件組是空的，定義原文不動，其他情境不受影響，審計員可以移除它。
    /// JET 自己寫不出這種內容，所以直接改資料庫裡的原文。
    /// </summary>
    [Theory]
    [InlineData("populationScope")]
    [InlineData("logicVersion")]
    [InlineData("source")]
    [InlineData("conditionValue")]
    [InlineData("editorOrigins")]
    [InlineData("unusedFieldOfOlderScenario")]
    [InlineData("propertyName")]
    public async Task DefinitionWithUnreadableText_IsListedAsUnreadableOnLoad_AndCanBeRemoved(string where)
    {
        const string provider = "sqlite";
        const string marker = "synthetic-unreadable-marker";
        const string unpairedSurrogate = @"\ud800";
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await CommitAsync(host, DebitScenario("合成情境甲"), DebitScenario("合成情境乙"));
        await RewriteDefinitionAsync(host, context.ProjectId, provider, 1, definition =>
        {
            switch (where)
            {
                case "populationScope":
                    definition["populationScope"] = marker;
                    break;
                case "logicVersion":
                    definition["logicVersion"] = marker;
                    break;
                case "source":
                    definition["source"] = marker;
                    break;
                case "conditionValue":
                    definition["groups"]![0]!["conditions"]![0]!["drCr"] = marker;
                    break;
                case "editorOrigins":
                    // 編輯來源照現行寫法（第 2 版、一組一條），這一列唯一不正常的地方是讀不出來的文字。
                    definition["editorOrigins"] = new JsonObject
                    {
                        ["version"] = 2,
                        ["groups"] = new JsonArray(new JsonObject { ["cards"] = new JsonArray((JsonNode?)null) }),
                        ["note"] = marker
                    };
                    break;
                case "unusedFieldOfOlderScenario":
                    definition["logicVersion"] = OlderVersion;
                    definition["note"] = marker;
                    break;
                case "propertyName":
                    definition[marker] = 1;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(where), where, null);
            }
        });
        // 欄名那一列換成「logicVersio」加跳脫寫法：比要找的 logicVersion 長、開頭相同、排在它後面，找欄位時就會讀到它。
        await ExecuteAsync(host, context.ProjectId, provider,
            "UPDATE filter_scenario SET definition_json = replace(definition_json, @marker, @text) WHERE position = 1;",
            ("@marker", marker),
            ("@text", where == "propertyName" ? "logicVersio" + unpairedSurrogate : unpairedSurrogate));
        var damaged = (await ReadScenariosAsync(host, context.ProjectId, provider)).Single(item => item.Position == 1);
        Assert.Contains(unpairedSurrogate, damaged.DefinitionJson);
        // 讀篩選範圍的那一處排在「是不是目前規則」的檢查後面，現在的流程走不到；直接呼叫，確認它自己也回結果過期。
        var unusable = Assert.Throws<JetActionException>(() => FilterPopulationScopeParser.ResolveSaved(
            [new SavedFilterScenario(damaged.Position, damaged.Name, damaged.Rationale, damaged.DefinitionJson, DateTimeOffset.UnixEpoch)]));
        Assert.Equal(JetErrorCodes.StaleResult, unusable.Code);

        var loaded = await LoadAsync(host, context.ProjectId);

        var check = loaded.GetProperty("filterScenarioCheck");
        Assert.Equal("needsEdit", check.GetProperty("status").GetString());
        var problem = Assert.Single(check.GetProperty("problems").EnumerateArray());
        Assert.Equal(1, problem.GetProperty("position").GetInt32());
        Assert.Equal(
            new[] { "條件內容無法讀取" },
            problem.GetProperty("messages").EnumerateArray().Select(item => item.GetString()!).ToArray());
        var listed = loaded.GetProperty("filterScenarios").EnumerateArray().ToArray();
        Assert.Equal(new[] { "合成情境甲", "合成情境乙" }, listed.Select(item => item.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(0, listed[0].GetProperty("groups").GetArrayLength());
        Assert.False(listed[0].TryGetProperty("editorOrigins", out _));
        Assert.Equal(1, listed[1].GetProperty("groups").GetArrayLength());
        Assert.Equal(damaged, (await ReadScenariosAsync(host, context.ProjectId, provider)).Single(item => item.Position == 1));

        var removed = await host.DispatchAsync("filter.scenario.remove", new JsonObject
        {
            ["populationScope"] = "auditPeriod",
            ["position"] = 1,
            ["expectedName"] = "合成情境甲"
        }.ToJsonString());

        Assert.Equal(
            new[] { "合成情境乙" },
            removed.GetProperty("scenarios").EnumerateArray().Select(item => item.GetProperty("name").GetString()!).ToArray());
        var reopened = await LoadAsync(host, context.ProjectId);
        Assert.Equal("current", reopened.GetProperty("filterScenarioCheck").GetProperty("status").GetString());
        Assert.Equal(
            new[] { "合成情境乙" },
            reopened.GetProperty("filterScenarios").EnumerateArray().Select(item => item.GetProperty("name").GetString()!).ToArray());
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
