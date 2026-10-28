using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 第五步篩選情境一次只存或刪一個（使用者 2026-10-13 對「情境卡住」選「本計畫內修好」）。
/// 開案時有情境不能套用目前規則，審計員仍要能一個一個改或刪：其他情境照資料庫原樣保留，不再整批重新檢查；
/// 錯誤只會屬於正在編輯的情境；全部改好或刪掉之前，矩陣、報告與第六步照樣鎖住。SQLite 與 DuckDB 都要成立。
/// </summary>
/// <remarks>
/// 每個操作都透過「照畫面送法」的輔助方法送出。輔助方法記住畫面上的已存清單（開案與每次回應的 <c>scenarios</c>），
/// 照畫面的送法組請求，所以第一次失敗重現的是審計員在畫面上遇到的錯誤，而不是「動作不存在」。
/// </remarks>
public sealed class FilterScenarioSingleChangeTests
{
    private const string OlderVersion = "filter-2026-10-06-v18";
    private const string UnknownRdeFieldId = "rde.aaaa0000aaaa0000aaaa0000aaaa0000";

    private sealed record StoredScenario(int Position, string Name, string Rationale, string DefinitionJson, string SavedUtc);

    // ── 使用者 2026-10-13 裁定「開案也逐一檢查」：開案與儲存用同一套檢查 ────────────────

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task OnLoad_CurrentVersionThatNoLongerValidates_IsListedAndKeepsResultsLocked(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { Scenario("情境甲", "debit"), Scenario("情境乙", "credit") }
        }));
        // 規則版本仍是目前版本，只是條件用的攸關資料元素欄位已不在配對裡（合成的「其他資料改變」）。
        await RewriteDefinitionAsync(host, context.ProjectId, provider, 2, definition =>
            definition["groups"]![0]!["rules"]!.AsArray().Insert(0, JsonNode.Parse(
                "{\"join\":\"AND\",\"type\":\"typed\",\"fieldId\":\"" + UnknownRdeFieldId + "\",\"operator\":\"isBlank\"}")));
        var before = await ReadScenariosAsync(host, context.ProjectId, provider);
        Assert.All(before, row => Assert.True(IsCurrent(row)));

        var loaded = await LoadAsync(host, context.ProjectId);

        var check = loaded.GetProperty("filterScenarioCheck");
        Assert.Equal("needsEdit", check.GetProperty("status").GetString());
        var problem = Assert.Single(check.GetProperty("problems").EnumerateArray());
        Assert.Equal(2, problem.GetProperty("position").GetInt32());
        Assert.Equal("情境乙", problem.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("filterResultRef").ValueKind);
        Assert.True(loaded.GetProperty("staleState").GetProperty("filter").GetBoolean());
        Assert.Equal(before, await ReadScenariosAsync(host, context.ProjectId, provider));

        // 開案列出後，可以照樣一個一個改好。
        var screen = new Screen();
        screen.Show(loaded.GetProperty("filterScenarios"));
        var response = await SaveLikeTheScreenAsync(host, screen, 2, Fixed(screen, 2));
        Assert.Equal("current", response.GetProperty("filterScenarioCheck").GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task OnLoad_CurrentValidScenarios_StayCurrentWithoutRecalculatingHits(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var committed = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { Scenario("情境甲", "debit"), Scenario("情境乙", "credit") }
        }));
        var before = await ReadScenariosAsync(host, context.ProjectId, provider);
        var hits = await ScalarAsync(host, context.ProjectId, provider, "SELECT COUNT(*) FROM result_filter_run;");

        var loaded = await LoadAsync(host, context.ProjectId);

        var check = loaded.GetProperty("filterScenarioCheck");
        Assert.Equal("current", check.GetProperty("status").GetString());
        Assert.Equal(0, check.GetProperty("recalculatedCount").GetInt32());
        Assert.Equal(
            committed.GetProperty("resultRef").GetProperty("revision").GetString(),
            loaded.GetProperty("filterResultRef").GetProperty("revision").GetString());
        Assert.Equal(before, await ReadScenariosAsync(host, context.ProjectId, provider));
        Assert.Equal(hits, await ScalarAsync(host, context.ProjectId, provider, "SELECT COUNT(*) FROM result_filter_run;"));
        Assert.Equal(0, await StaleFlagAsync(host, context.ProjectId, provider));
    }

    // ── 第一批：修正前會失敗（設計第 5.1 節第 1 到 5 項）──────────────────────────────

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task TwoInvalid_FixingTheFirst_SavesItAndListsOnlyTheSecond(string provider)
    {
        var (host, screen, _) = await TwoInvalidAsync(provider);
        using var _host = host;

        var response = await SaveLikeTheScreenAsync(host, screen, 1, Fixed(screen, 1));

        var check = response.GetProperty("filterScenarioCheck");
        Assert.Equal("needsEdit", check.GetProperty("status").GetString());
        var problem = Assert.Single(check.GetProperty("problems").EnumerateArray());
        Assert.Equal(2, problem.GetProperty("position").GetInt32());
        Assert.Equal("情境乙", problem.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("resultRef").ValueKind);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task TwoInvalid_FixingTheSecond_SavesItAndListsOnlyTheFirst(string provider)
    {
        var (host, screen, _) = await TwoInvalidAsync(provider);
        using var _host = host;

        var response = await SaveLikeTheScreenAsync(host, screen, 2, Fixed(screen, 2));

        var check = response.GetProperty("filterScenarioCheck");
        Assert.Equal("needsEdit", check.GetProperty("status").GetString());
        var problem = Assert.Single(check.GetProperty("problems").EnumerateArray());
        Assert.Equal(1, problem.GetProperty("position").GetInt32());
        Assert.Equal("情境甲", problem.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("resultRef").ValueKind);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task OnlySecondInvalid_EditingAValidScenario_IsNotBlockedAndKeepsTheInvalidOne(string provider)
    {
        var (host, screen, _) = await PrepareAsync(provider, invalidFirst: false, invalidSecond: true);
        using var _host = host;
        var edited = Wire(screen.Saved[0]);
        edited["rationale"] = "只改動機";

        var response = await SaveLikeTheScreenAsync(host, screen, 1, edited);

        Assert.Equal(["情境甲", "情境乙", "情境丙"], Names(response));
        var check = response.GetProperty("filterScenarioCheck");
        Assert.Equal("needsEdit", check.GetProperty("status").GetString());
        var problem = Assert.Single(check.GetProperty("problems").EnumerateArray());
        Assert.Equal(2, problem.GetProperty("position").GetInt32());
        Assert.Equal("情境乙", problem.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task TwoInvalid_RemovingTheValidOne_KeepsBothInvalidVerbatim(string provider)
    {
        var (host, screen, projectId) = await TwoInvalidAsync(provider);
        using var _host = host;
        var before = await ReadScenariosAsync(host, projectId, provider);

        var response = await RemoveLikeTheScreenAsync(host, screen, 3);

        Assert.Equal(["情境甲", "情境乙"], Names(response));
        var check = response.GetProperty("filterScenarioCheck");
        Assert.Equal("needsEdit", check.GetProperty("status").GetString());
        Assert.Equal([1, 2], check.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("position").GetInt32()));
        var after = await ReadScenariosAsync(host, projectId, provider);
        Assert.Equal(before.Take(2).Select(s => s.DefinitionJson), after.Select(s => s.DefinitionJson));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task TwoInvalid_Recheck_SucceedsAndListsBoth(string provider)
    {
        var (host, screen, _) = await TwoInvalidAsync(provider);
        using var _host = host;

        var response = await RecheckLikeTheScreenAsync(host, screen);

        var check = response.GetProperty("filterScenarioCheck");
        Assert.Equal("needsEdit", check.GetProperty("status").GetString());
        Assert.Equal(
            ["情境甲", "情境乙"],
            check.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        Assert.Equal(JsonValueKind.Null, response.GetProperty("resultRef").ValueKind);
    }

    // ── 修正後要成立的情境（設計第 5.2 節）──────────────────────────────────────

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task OnlyOneInvalid_FixingIt_PublishesHitsAndUnlocksResults(string provider)
    {
        var (host, screen, projectId) = await PrepareAsync(provider, invalidFirst: false, invalidSecond: true);
        using var _host = host;

        var response = await SaveLikeTheScreenAsync(host, screen, 2, Fixed(screen, 2));

        var check = response.GetProperty("filterScenarioCheck");
        Assert.Equal("current", check.GetProperty("status").GetString());
        Assert.Empty(check.GetProperty("problems").EnumerateArray());
        Assert.Equal(2, check.GetProperty("recalculatedCount").GetInt32());
        Assert.Equal(2, response.GetProperty("position").GetInt32());
        var stored = await ReadScenariosAsync(host, projectId, provider);
        Assert.Equal(stored[0].SavedUtc, response.GetProperty("resultRef").GetProperty("revision").GetString());
        Assert.All(stored, row => Assert.True(IsCurrent(row)));
        Assert.True(await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM result_filter_run;") > 0);
        Assert.Equal(0, await StaleFlagAsync(host, projectId, provider));
        Assert.False(response.GetProperty("staleState").GetProperty("filter").GetBoolean());

        var reloaded = await LoadAsync(host, projectId);
        Assert.Equal("current", reloaded.GetProperty("filterScenarioCheck").GetProperty("status").GetString());
        var page = await host.DispatchAsync("query.filterHitsPage", JsonSerializer.Serialize(new { scenarioPosition = 1, pageSize = 20 }));
        Assert.NotEmpty(page.GetProperty("rows").EnumerateArray());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task OnlyOneInvalid_RemovingIt_UnlocksResults(string provider)
    {
        var (host, screen, projectId) = await PrepareAsync(provider, invalidFirst: false, invalidSecond: true);
        using var _host = host;

        var response = await RemoveLikeTheScreenAsync(host, screen, 2);

        Assert.Equal(["情境甲", "情境丙"], Names(response));
        Assert.Equal("current", response.GetProperty("filterScenarioCheck").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Object, response.GetProperty("resultRef").ValueKind);
        Assert.Equal(0, await StaleFlagAsync(host, projectId, provider));
        Assert.Equal([1, 2], (await ReadScenariosAsync(host, projectId, provider)).Select(row => row.Position));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task TwoInvalid_FixingOneThenTheOther_UnlocksMatrixAndHits(string provider)
    {
        var (host, screen, _) = await TwoInvalidAsync(provider);
        using var _host = host;

        var first = await SaveLikeTheScreenAsync(host, screen, 1, Fixed(screen, 1));
        var second = await SaveLikeTheScreenAsync(host, screen, 2, Fixed(screen, 2));

        Assert.Equal("needsEdit", first.GetProperty("filterScenarioCheck").GetProperty("status").GetString());
        Assert.Equal("current", second.GetProperty("filterScenarioCheck").GetProperty("status").GetString());
        var matrix = await host.DispatchAsync("query.tagMatrixScenarios");
        Assert.Equal(3, matrix.GetProperty("scenarios").GetArrayLength());
        var page = await host.DispatchAsync("query.filterHitsPage", JsonSerializer.Serialize(new { scenarioPosition = 3, pageSize = 20 }));
        Assert.NotEmpty(page.GetProperty("rows").EnumerateArray());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task TwoInvalid_FixingOneThenRemovingTheOther_UnlocksResults(string provider)
    {
        var (host, screen, _) = await TwoInvalidAsync(provider);
        using var _host = host;

        await SaveLikeTheScreenAsync(host, screen, 1, Fixed(screen, 1));
        var response = await RemoveLikeTheScreenAsync(host, screen, 2);

        Assert.Equal("current", response.GetProperty("filterScenarioCheck").GetProperty("status").GetString());
        Assert.Equal(["情境甲", "情境丙"], Names(response));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task SavingWhileOthersNeedEdits_KeepsThemVerbatimAndKeepsResultsLocked(string provider)
    {
        var (host, screen, projectId) = await TwoInvalidAsync(provider);
        using var _host = host;
        var prescreen = await host.DispatchAsync("prescreen.run");
        var validation = await host.DispatchAsync("validate.run");
        var before = await ReadScenariosAsync(host, projectId, provider);

        await SaveLikeTheScreenAsync(host, screen, 1, Fixed(screen, 1));

        var after = await ReadScenariosAsync(host, projectId, provider);
        Assert.Equal([1, 2, 3], after.Select(row => row.Position));
        Assert.True(IsCurrent(after[0]));
        Assert.Equal(before[1].DefinitionJson, after[1].DefinitionJson);
        Assert.Equal(before[2].DefinitionJson, after[2].DefinitionJson);
        Assert.Single(after.Select(row => row.SavedUtc).Distinct());
        Assert.NotEqual(before[0].SavedUtc, after[0].SavedUtc);
        Assert.Equal(0, await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM result_filter_run;"));
        Assert.Equal(1, await StaleFlagAsync(host, projectId, provider));

        var hits = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "query.filterHitsPage", JsonSerializer.Serialize(new { scenarioPosition = 1 })));
        Assert.Equal(JetErrorCodes.StaleResult, hits.Code);
        Assert.Equal(FilterScenarioRuleMessages.NotYetUpgraded, hits.Message);
        var validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString();
        var prescreenRunId = prescreen.GetProperty("resultRef").GetProperty("runId").GetString();
        var report = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("export.criteriaSelectionReport",
            JsonSerializer.Serialize(new { validationRunId, prescreenRunId, revision = after[0].SavedUtc })));
        Assert.Equal(JetErrorCodes.StaleResult, report.Code);
        var workpaper = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("export.workpaperStream",
            JsonSerializer.Serialize(new
            {
                validationRunId, prescreenRunId, scenarioRevision = after[0].SavedUtc, scenarioPositions = new[] { 1 }
            })));
        Assert.Equal(JetErrorCodes.StaleResult, workpaper.Code);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task SavingACopy_AppendsItAndKeepsTheOthersVerbatim(string provider)
    {
        var (host, screen, projectId) = await TwoInvalidAsync(provider);
        using var _host = host;
        var before = await ReadScenariosAsync(host, projectId, provider);
        var copy = Fixed(screen, 1);
        copy["name"] = "情境甲（副本）";

        var response = await SaveLikeTheScreenAsync(host, screen, null, copy);

        Assert.Equal(4, response.GetProperty("position").GetInt32());
        var after = await ReadScenariosAsync(host, projectId, provider);
        Assert.Equal(before.Select(row => row.DefinitionJson), after.Take(3).Select(row => row.DefinitionJson));
        Assert.Equal("情境甲（副本）", after[3].Name);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task TenSaved_AddingAnotherIsRejectedButUpdatingOneSucceeds(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = Enumerable.Range(1, 10).Select(index => Scenario("情境" + index, "debit")).ToArray()
        }));
        var screen = await OpenAsync(host, context.ProjectId);
        var before = await ReadScenariosAsync(host, context.ProjectId, provider);

        var eleventh = Wire(screen.Saved[0]);
        eleventh["name"] = "第十一個";
        var limit = await Assert.ThrowsAsync<JetActionException>(() =>
            SaveLikeTheScreenAsync(host, screen, null, eleventh));

        Assert.Equal(JetErrorCodes.ScenarioLimitReached, limit.Code);
        Assert.Equal(before, await ReadScenariosAsync(host, context.ProjectId, provider));
        var renamed = Wire(screen.Saved[9]);
        renamed["name"] = "第十個改名";
        var response = await SaveLikeTheScreenAsync(host, screen, 10, renamed);
        Assert.Equal(10, response.GetProperty("savedCount").GetInt32());
        Assert.Equal("第十個改名", (await ReadScenariosAsync(host, context.ProjectId, provider))[9].Name);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task RemovingTheMiddleScenario_RenumbersInOrder(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { Scenario("情境甲", "debit"), Scenario("情境乙", "credit"), Scenario("情境丙", "debit") }
        }));
        var screen = await OpenAsync(host, context.ProjectId);

        var response = await RemoveLikeTheScreenAsync(host, screen, 2);

        Assert.Equal(["情境甲", "情境丙"], Names(response));
        var stored = await ReadScenariosAsync(host, context.ProjectId, provider);
        Assert.Equal([1, 2], stored.Select(row => row.Position));
        Assert.Equal(["情境甲", "情境丙"], stored.Select(row => row.Name));
        Assert.Equal("current", response.GetProperty("filterScenarioCheck").GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task RemovingTheLastScenario_ClearsTheListAndTheResultFlag(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { Scenario("情境甲", "debit") } }));
        await RewriteDefinitionAsync(host, context.ProjectId, provider, 1, definition =>
        {
            definition["logicVersion"] = OlderVersion;
            definition["groups"]![0]!["rules"]!.AsArray().Add(
                JsonNode.Parse("""{"join":"AND","type":"prescreen","prescreenKey":"backdatedPosting"}"""));
        });
        var screen = await OpenAsync(host, context.ProjectId);

        var response = await RemoveLikeTheScreenAsync(host, screen, 1);

        Assert.Empty(response.GetProperty("scenarios").EnumerateArray());
        Assert.Equal("current", response.GetProperty("filterScenarioCheck").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("resultRef").ValueKind);
        Assert.Equal(0, await StaleFlagAsync(host, context.ProjectId, provider));
        Assert.Empty(await ReadScenariosAsync(host, context.ProjectId, provider));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Recheck_UpgradesTheValidOldScenarioAndKeepsTheInvalidOnesVerbatim(string provider)
    {
        var (host, screen, projectId) = await TwoInvalidAsync(provider);
        using var _host = host;
        var before = await ReadScenariosAsync(host, projectId, provider);

        var response = await RecheckLikeTheScreenAsync(host, screen);

        var after = await ReadScenariosAsync(host, projectId, provider);
        Assert.Equal(before[0].DefinitionJson, after[0].DefinitionJson);
        Assert.Equal(before[1].DefinitionJson, after[1].DefinitionJson);
        Assert.True(IsCurrent(after[2]));
        var check = response.GetProperty("filterScenarioCheck");
        Assert.Equal(2, check.GetProperty("problems").GetArrayLength());
        Assert.Equal(1, check.GetProperty("recalculatedCount").GetInt32());
        Assert.Equal(1, await StaleFlagAsync(host, projectId, provider));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Recheck_AllValidOldScenarios_PublishesHits(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { Scenario("情境甲", "debit"), Scenario("情境乙", "credit"), Scenario("情境丙", "debit") }
        }));
        foreach (var position in new[] { 1, 2, 3 })
        {
            await RewriteDefinitionAsync(host, context.ProjectId, provider, position, definition =>
                definition["logicVersion"] = OlderVersion);
        }

        var response = await RecheckLikeTheScreenAsync(host, new Screen());

        var check = response.GetProperty("filterScenarioCheck");
        Assert.Equal("current", check.GetProperty("status").GetString());
        Assert.Equal(3, check.GetProperty("recalculatedCount").GetInt32());
        Assert.True(await ScalarAsync(host, context.ProjectId, provider, "SELECT COUNT(*) FROM result_filter_run;") > 0);
        Assert.All(await ReadScenariosAsync(host, context.ProjectId, provider), row => Assert.True(IsCurrent(row)));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ErrorInTheEditedScenario_NamesThatScenarioAndChangesNothing(string provider)
    {
        var (host, screen, projectId) = await TwoInvalidAsync(provider);
        using var _host = host;
        var before = await ReadScenariosAsync(host, projectId, provider);
        var stillInvalid = Wire(screen.Saved[0]);
        stillInvalid["groups"]![0]!["rules"]!.AsArray().Add(JsonNode.Parse(
            "{\"join\":\"AND\",\"type\":\"typed\",\"fieldId\":\"" + UnknownRdeFieldId + "\",\"operator\":\"isBlank\"}"));

        var error = await Assert.ThrowsAsync<JetActionException>(() =>
            SaveLikeTheScreenAsync(host, screen, 1, stillInvalid));

        Assert.Equal(JetErrorCodes.InvalidScenario, error.Code);
        Assert.NotEmpty(error.Details!);
        Assert.All(error.Details!, detail =>
        {
            Assert.Equal(1, detail.ScenarioPosition);
            Assert.Equal("情境甲", detail.ScenarioName);
        });
        Assert.Equal(before, await ReadScenariosAsync(host, projectId, provider));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task DuplicateName_IsRejectedAndChangesNothing(string provider)
    {
        var (host, screen, projectId) = await TwoInvalidAsync(provider);
        using var _host = host;
        var before = await ReadScenariosAsync(host, projectId, provider);
        var renamed = Fixed(screen, 1);
        renamed["name"] = "情境丙";

        var error = await Assert.ThrowsAsync<JetActionException>(() => SaveLikeTheScreenAsync(host, screen, 1, renamed));

        Assert.Equal(JetErrorCodes.InvalidScenario, error.Code);
        Assert.Equal("情境名稱重複：「情境丙」。", error.Message);
        Assert.Equal(before, await ReadScenariosAsync(host, projectId, provider));
    }

    public static TheoryData<string, string, int, string> OutOfDateLists => new()
    {
        { "sqlite", "save", 2, "不存在的名稱" },
        { "sqlite", "remove", 2, "不存在的名稱" },
        { "sqlite", "save", 9, "情境甲" },
        { "duckdb", "save", 2, "不存在的名稱" },
        { "duckdb", "remove", 2, "不存在的名稱" },
        { "duckdb", "remove", 9, "情境甲" }
    };

    [Theory]
    [MemberData(nameof(OutOfDateLists))]
    public async Task ScreenListOutOfDate_IsRejectedAndChangesNothing(string provider, string kind, int position, string expectedName)
    {
        var (host, screen, projectId) = await TwoInvalidAsync(provider);
        using var _host = host;
        var before = await ReadScenariosAsync(host, projectId, provider);
        var payload = new JsonObject
        {
            ["populationScope"] = "auditPeriod",
            ["position"] = position,
            ["expectedName"] = expectedName
        };
        if (kind == "save")
        {
            payload["scenario"] = Fixed(screen, 1);
        }

        var error = await Assert.ThrowsAsync<JetActionException>(() =>
            host.DispatchAsync("filter.scenario." + kind, payload.ToJsonString()));

        Assert.Equal(JetErrorCodes.StaleResult, error.Code);
        Assert.Equal("畫面上的情境清單與已儲存的內容不一致，本次未儲存。請重新開啟案件後再操作。", error.Message);
        Assert.Equal(before, await ReadScenariosAsync(host, projectId, provider));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task MissingOrMalformedPayload_IsInvalidPayload(string provider)
    {
        var (host, screen, _) = await TwoInvalidAsync(provider);
        using var _host = host;

        foreach (var (action, payload) in new[]
        {
            ("filter.scenario.save", """{"position":null}"""),
            ("filter.scenario.save", """{"position":0,"expectedName":"情境甲","scenario":{}}"""),
            ("filter.scenario.save", """{"position":1,"scenario":{}}"""),
            ("filter.scenario.remove", """{"expectedName":"情境甲"}"""),
            ("filter.scenario.remove", """{"position":"1","expectedName":"情境甲"}""")
        })
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(action, payload));
            Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
        }

        Assert.Equal(3, screen.Saved.Count);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task MixedSavedTimes_AreMadeConsistentByTheNextWrite(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { Scenario("情境甲", "debit"), Scenario("情境乙", "credit") }
        }));
        await ExecuteAsync(host, context.ProjectId, provider,
            "UPDATE config_filter_scenario SET saved_utc = @savedUtc WHERE position = 2;",
            ("@savedUtc", "2026-01-01T00:00:00.0000000+00:00"));
        var loaded = await LoadAsync(host, context.ProjectId);
        Assert.Equal("inconsistent", loaded.GetProperty("filterScenarioCheck").GetProperty("status").GetString());
        var screen = new Screen();
        screen.Show(loaded.GetProperty("filterScenarios"));
        var renamed = Wire(screen.Saved[0]);
        renamed["rationale"] = "改動機";

        var response = await SaveLikeTheScreenAsync(host, screen, 1, renamed);

        Assert.Equal("current", response.GetProperty("filterScenarioCheck").GetProperty("status").GetString());
        Assert.Single((await ReadScenariosAsync(host, context.ProjectId, provider)).Select(row => row.SavedUtc).Distinct());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task OldScenarioWithExclusions_KeepsThemWhenAnotherScenarioIsSaved(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { Scenario("情境甲", "debit"), Scenario("情境乙", "credit") }
        }));
        await RewriteDefinitionAsync(host, context.ProjectId, provider, 2, definition =>
        {
            definition["logicVersion"] = OlderVersion;
            definition["exclusions"] = new JsonArray(JsonNode.Parse("""{"type":"drCrOnly","drCr":"debit"}"""));
        });
        var screen = await OpenAsync(host, context.ProjectId);
        var edited = Wire(screen.Saved[0]);
        edited["rationale"] = "改動機";

        var response = await SaveLikeTheScreenAsync(host, screen, 1, edited);

        var stored = await ReadScenariosAsync(host, context.ProjectId, provider);
        Assert.Contains("\"exclusions\"", stored[1].DefinitionJson, StringComparison.Ordinal);
        var problem = Assert.Single(response.GetProperty("filterScenarioCheck").GetProperty("problems").EnumerateArray());
        Assert.Equal("情境乙", problem.GetProperty("name").GetString());
    }

    // 預篩選收斂計畫階段四（2026-10-23）：10 月以前存下、帶排除區的舊情境，開案的摘要照原樣帶出排除區，編輯器才看得到。
    // 審計員沒移除就儲存，畫面照原樣送回，後端照既有行為擋下並寫出原因；移除後才能儲存，存回去的定義不再有排除區。
    // 原本摘要不帶排除區，修改這個情境後存回去就默默丟掉（KCT 需求計畫第八節操作測試 F10）。
    // 第一次失敗：Focused 收據 20261023-051033801-effa418fbc1c4f11bcd21566413ea49e（開案的摘要沒有排除區）。
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task OldScenarioWithExclusions_ShowsThemToTheEditorAndSavesOnlyAfterTheyAreRemoved(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { Scenario("情境甲", "debit"), Scenario("情境乙", "credit") }
        }));
        var exclusions = JsonNode.Parse(
            """[{"scope":"voucher","rule":{"type":"fieldValue","field":"postDate","operator":"in","values":["2025-01-15"]}}]""")!;
        await RewriteDefinitionAsync(host, context.ProjectId, provider, 2, definition =>
        {
            definition["logicVersion"] = OlderVersion;
            definition["exclusions"] = exclusions.DeepClone();
        });
        var before = await ReadScenariosAsync(host, context.ProjectId, provider);
        var screen = await OpenAsync(host, context.ProjectId);

        Assert.True(screen.Saved[1].TryGetProperty("exclusions", out var shown), "開案的摘要要帶出排除區");
        Assert.True(JsonNode.DeepEquals(exclusions, JsonNode.Parse(shown.GetRawText())));
        Assert.False(screen.Saved[0].TryGetProperty("exclusions", out _));
        var edited = Wire(screen.Saved[1]);
        edited["rationale"] = "改動機";
        var error = await Assert.ThrowsAsync<JetActionException>(() =>
            SaveLikeTheScreenAsync(host, screen, 2, (JsonObject)edited.DeepClone()));
        Assert.Equal(JetErrorCodes.InvalidScenario, error.Code);
        Assert.Contains("使用了已移除的「排除區域」，請改用條件中的「不屬於」或「不在區間」", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await ReadScenariosAsync(host, context.ProjectId, provider));

        edited.Remove("exclusions");
        var response = await SaveLikeTheScreenAsync(host, screen, 2, edited);

        var stored = await ReadScenariosAsync(host, context.ProjectId, provider);
        Assert.DoesNotContain("\"exclusions\"", stored[1].DefinitionJson, StringComparison.Ordinal);
        Assert.Equal("改動機", stored[1].Rationale);
        Assert.Equal("current", response.GetProperty("filterScenarioCheck").GetProperty("status").GetString());
        Assert.False(screen.Saved[1].TryGetProperty("exclusions", out _));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task AllValidCase_StoresTheSameHitsAsThePreview(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var scenario = Scenario("借方情境", "debit");
        var preview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario }));

        var response = await host.DispatchAsync("filter.scenario.save", JsonSerializer.Serialize(new { position = (int?)null, scenario }));

        Assert.Equal("current", response.GetProperty("filterScenarioCheck").GetProperty("status").GetString());
        Assert.Equal(
            preview.GetProperty("scenario").GetProperty("count").GetInt64(),
            await ScalarAsync(host, context.ProjectId, provider, "SELECT COUNT(*) FROM result_filter_run WHERE scenario_position = 1;"));
        Assert.Equal(0, await StaleFlagAsync(host, context.ProjectId, provider));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task FixingTheLastScenario_MarksTheOlderCriteriaReportStaleWithoutTouchingTheFile(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var validation = await host.DispatchAsync("validate.run");
        var validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString();
        var prescreen = await host.DispatchAsync("prescreen.run");
        var prescreenRunId = prescreen.GetProperty("resultRef").GetProperty("runId").GetString();
        var committed = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { Scenario("情境甲", "debit") } }));
        var oldRevision = committed.GetProperty("resultRef").GetProperty("revision").GetString();
        await host.DispatchAsync("export.criteriaSelectionReport",
            JsonSerializer.Serialize(new { validationRunId, prescreenRunId, revision = oldRevision }));
        var report = Assert.Single(CriteriaReports(await LoadAsync(host, context.ProjectId)));
        var path = report.GetProperty("fullPath").GetString()!;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        await RewriteDefinitionAsync(host, context.ProjectId, provider, 1, definition =>
        {
            definition["logicVersion"] = OlderVersion;
            definition["groups"]![0]!["rules"]!.AsArray().Add(
                JsonNode.Parse("""{"join":"AND","type":"prescreen","prescreenKey":"backdatedPosting"}"""));
        });
        var screen = await OpenAsync(host, context.ProjectId);

        var response = await SaveLikeTheScreenAsync(host, screen, 1, Fixed(screen, 1));

        Assert.NotEqual(oldRevision, response.GetProperty("resultRef").GetProperty("revision").GetString());
        var reloaded = Assert.Single(CriteriaReports(await LoadAsync(host, context.ProjectId)));
        Assert.True(reloaded.GetProperty("stale").GetBoolean());
        Assert.Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
    }

    public static TheoryData<string, string> Actions => new()
    {
        { "sqlite", "filter.scenario.save" },
        { "sqlite", "filter.scenario.remove" },
        { "sqlite", "filter.scenario.recheck" },
        { "duckdb", "filter.scenario.save" },
        { "duckdb", "filter.scenario.remove" },
        { "duckdb", "filter.scenario.recheck" }
    };

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task WithoutACurrentValidation_EveryActionAnswersLikeFilterCommit(string provider, string action)
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, runValidation: false, databaseProvider: provider);
        var commit = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { Scenario("情境甲", "debit") } })));

        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(action, JsonSerializer.Serialize(new
        {
            position = 1, expectedName = "情境甲", scenario = Scenario("情境甲", "debit")
        })));

        Assert.Equal(commit.Code, error.Code);
        Assert.Equal(commit.Message, error.Message);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task Response_ShapeIsLocked(string provider, string action)
    {
        var (host, screen, _) = await TwoInvalidAsync(provider);
        using var _host = host;

        var response = action switch
        {
            "filter.scenario.save" => await SaveLikeTheScreenAsync(host, screen, 1, Fixed(screen, 1)),
            "filter.scenario.remove" => await RemoveLikeTheScreenAsync(host, screen, 3),
            _ => await RecheckLikeTheScreenAsync(host, screen)
        };

        Assert.Equal(
            ["filterScenarioCheck", "ok", "position", "resultRef", "savedCount", "scenarios", "staleState"],
            response.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal(
            ["problems", "recalculatedCount", "status"],
            response.GetProperty("filterScenarioCheck").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["filter", "prescreen", "validation"],
            response.GetProperty("staleState").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var problem = response.GetProperty("filterScenarioCheck").GetProperty("problems")[0];
        Assert.Equal(["messages", "name", "position"], problem.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var summary = response.GetProperty("scenarios")[0];
        Assert.Equal(
            ["groups", "name", "populationScope", "rationale", "savedUtc", "source"],
            summary.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    // ── 錯誤細節帶情境位置（純函式）─────────────────────────────────────────────

    [Fact]
    public void ErrorDetails_ForAScenario_TagEveryItemAndKeepTheMessage()
    {
        var exception = FilterScenarioErrorDetails.InvalidScenario(["第 1 組第 2 條：合成原因。", "整體原因"], 3, "情境丙");

        Assert.Equal("第 1 組第 2 條：合成原因。；整體原因", exception.Message);
        Assert.Equal(
            [new JetErrorDetail(1, 2, "合成原因。") { ScenarioPosition = 3, ScenarioName = "情境丙" },
             new JetErrorDetail(null, null, "整體原因") { ScenarioPosition = 3, ScenarioName = "情境丙" }],
            exception.Details!);
        Assert.All(FilterScenarioErrorDetails.InvalidScenario(["第 1 組：x"]).Details!, detail =>
        {
            Assert.Null(detail.ScenarioPosition);
            Assert.Null(detail.ScenarioName);
        });
    }

    [Fact]
    public void ErrorDetails_ForAShapeError_AddOneScenarioItem()
    {
        var tagged = FilterScenarioErrorDetails.ForScenario(
            new JetActionException(JetErrorCodes.InvalidScenario, "合成形狀錯誤。"), 2, "情境乙");

        Assert.Equal(JetErrorCodes.InvalidScenario, tagged.Code);
        Assert.Equal("合成形狀錯誤。", tagged.Message);
        Assert.Equal([new JetErrorDetail(null, null, "合成形狀錯誤。") { ScenarioPosition = 2, ScenarioName = "情境乙" }], tagged.Details!);
    }

    [Fact]
    public void ErrorDetails_WithoutAScenario_DoNotWriteTheNewKeys()
    {
        var json = JsonSerializer.Serialize(new JetErrorDetail(1, 1, "x"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("scenarioPosition", json, StringComparison.Ordinal);
        Assert.DoesNotContain("scenarioName", json, StringComparison.Ordinal);
        var tagged = JsonSerializer.Serialize(new JetErrorDetail(1, 1, "x") { ScenarioPosition = 2, ScenarioName = "乙" },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var parsed = JsonDocument.Parse(tagged);
        Assert.Equal(2, parsed.RootElement.GetProperty("scenarioPosition").GetInt32());
        Assert.Equal("乙", parsed.RootElement.GetProperty("scenarioName").GetString());
    }

    // ── 佈置 ────────────────────────────────────────────────────────────────────

    private static async Task<Screen> OpenAsync(HandlerTestHost host, string projectId)
    {
        var loaded = await LoadAsync(host, projectId);
        var screen = new Screen();
        screen.Show(loaded.GetProperty("filterScenarios"));
        return screen;
    }

    private static Task<JsonElement> LoadAsync(HandlerTestHost host, string projectId) =>
        host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));

    private static JsonElement[] CriteriaReports(JsonElement loaded) =>
        loaded.GetProperty("reportArtifacts").EnumerateArray()
            .Where(item => item.GetProperty("kind").GetString() == "criteriaSelectionReport")
            .ToArray();

    private static bool IsCurrent(StoredScenario row) =>
        RuleLogicVersions.IsCurrent(new SavedFilterScenario(row.Position, row.Name, row.Rationale, row.DefinitionJson,
            DateTimeOffset.Parse(row.SavedUtc, System.Globalization.CultureInfo.InvariantCulture)));

    private static Task<long> StaleFlagAsync(HandlerTestHost host, string projectId, string provider) =>
        ScalarAsync(host, projectId, provider, "SELECT filter_stale FROM config_result_stale_state WHERE singleton=1;");

    /// <summary>畫面上目前的已存清單：開案時取得，每次成功回應後換成回應的 <c>scenarios</c>。</summary>
    private sealed class Screen
    {
        public List<JsonElement> Saved { get; } = [];

        public void Show(JsonElement scenarios)
        {
            Saved.Clear();
            Saved.AddRange(scenarios.EnumerateArray().Select(item => item.Clone()));
        }

        public string NameAt(int position) => Saved[position - 1].GetProperty("name").GetString()!;
    }

    private static Task<(HandlerTestHost Host, Screen Screen, string ProjectId)> TwoInvalidAsync(string provider) =>
        PrepareAsync(provider, invalidFirst: true, invalidSecond: true);

    /// <summary>
    /// 存三個合法情境「情境甲」（借方）、「情境乙」（貸方）、「情境丙」（借方），三個都改成舊版本。
    /// 甲在第 1 組最後加上已停用的回溯過帳條件；乙在第 1 組最前面插一條配對裡沒有的攸關資料元素欄位條件。
    /// 開案確認待修正清單，畫面清單取自開案回應。開案之前的狀態每種組合只建一次，每個測試複製一份再開案（<see cref="CaseTemplates"/>）。
    /// </summary>
    private static async Task<(HandlerTestHost Host, Screen Screen, string ProjectId)> PrepareAsync(
        string provider, bool invalidFirst, bool invalidSecond)
    {
        var host = new HandlerTestHost();
        try
        {
            var copied = await CaseTemplates.CopyIntoAsync(
                host,
                $"filter-scenario-single-change:{invalidFirst}:{invalidSecond}:{provider}",
                async templateHost =>
                {
                    var context = await DemoProjectPipeline.SetupAsync(templateHost, databaseProvider: provider);
                    await templateHost.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
                    {
                        scenarios = new[] { Scenario("情境甲", "debit"), Scenario("情境乙", "credit"), Scenario("情境丙", "debit") }
                    }));
                    foreach (var position in new[] { 1, 2, 3 })
                    {
                        await RewriteDefinitionAsync(templateHost, context.ProjectId, provider, position, definition =>
                        {
                            definition["logicVersion"] = OlderVersion;
                            var rules = definition["groups"]![0]!["rules"]!.AsArray();
                            if (position == 1 && invalidFirst)
                            {
                                rules.Add(JsonNode.Parse("""{"join":"AND","type":"prescreen","prescreenKey":"backdatedPosting"}"""));
                            }
                            else if (position == 2 && invalidSecond)
                            {
                                rules.Insert(0, JsonNode.Parse(
                                    "{\"join\":\"AND\",\"type\":\"typed\",\"fieldId\":\"" + UnknownRdeFieldId + "\",\"operator\":\"isBlank\"}"));
                            }
                        });
                    }

                    return (context.ProjectId, (object?)null);
                });

            var loaded = copied.Loaded;
            var check = loaded.GetProperty("filterScenarioCheck");
            Assert.Equal("needsEdit", check.GetProperty("status").GetString());
            Assert.Equal(
                (invalidFirst ? 1 : 0) + (invalidSecond ? 1 : 0),
                check.GetProperty("problems").GetArrayLength());
            var screen = new Screen();
            screen.Show(loaded.GetProperty("filterScenarios"));
            return (host, screen, copied.ProjectId);
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private static object Scenario(string name, string drCr) => new
    {
        name,
        rationale = name + "的合成動機",
        groups = new[] { new { join = "AND", rules = new[] { new { join = "AND", type = "drCrOnly", drCr } } } }
    };

    /// <summary>照前端 toWireScenario 的欄位規則，把畫面上的已存情境組成送出的形狀。</summary>
    private static JsonObject Wire(JsonElement summary)
    {
        var wire = new JsonObject
        {
            ["name"] = summary.GetProperty("name").GetString(),
            ["rationale"] = summary.GetProperty("rationale").GetString(),
            ["groups"] = new JsonArray(summary.GetProperty("groups").EnumerateArray()
                .Where(group => group.TryGetProperty("rules", out var rules) && rules.GetArrayLength() > 0)
                .Select(group => JsonNode.Parse(group.GetRawText()))
                .ToArray())
        };
        if (summary.TryGetProperty("editorOrigins", out var origins))
        {
            wire["editorOrigins"] = JsonNode.Parse(origins.GetRawText());
        }

        if (summary.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String)
        {
            wire["source"] = source.GetString();
        }

        // 前端 projectWireScenario 把草稿上的舊排除區原樣送出，讓後端擋下並寫出原因。預篩選收斂計畫階段四（2026-10-23）起
        // 開案的摘要會帶出排除區，這裡跟著前端補上這一條；在那之前摘要沒有這個欄位，這一條不會作用。
        if (summary.TryGetProperty("exclusions", out var exclusions)
            && exclusions.ValueKind == JsonValueKind.Array && exclusions.GetArrayLength() > 0)
        {
            wire["exclusions"] = JsonNode.Parse(exclusions.GetRawText());
        }

        return wire;
    }

    /// <summary>審計員改好這個情境：只留原本的借貸條件。</summary>
    private static JsonObject Fixed(Screen screen, int position)
    {
        var wire = Wire(screen.Saved[position - 1]);
        var rules = wire["groups"]![0]!["rules"]!.AsArray();
        foreach (var rule in rules.Where(rule => rule!["type"]!.GetValue<string>() != "drCrOnly").ToArray())
        {
            rules.Remove(rule);
        }

        return wire;
    }

    private static string[] Names(JsonElement response) =>
        response.GetProperty("scenarios").EnumerateArray().Select(item => item.GetProperty("name").GetString()!).ToArray();

    // ── 照畫面送法 ──────────────────────────────────────────────────────────────
    // 第一版照修正前的畫面，把全部已存情境連同草稿整批送給 filter.commit；第一次失敗收據
    // 20261013-102455912-39b9e3db64d248ed99336afed02d5731（10 個全部失敗，錯誤就是審計員看到的「第 1 組第 N 條：…」）。
    // 修正後畫面只送正在處理的那一個情境，這裡跟著改送新動作；上面的斷言一字未改。

    private static Task<JsonElement> SaveLikeTheScreenAsync(
        HandlerTestHost host, Screen screen, int? position, JsonObject scenario) =>
        SendAsync(host, screen, "filter.scenario.save", new JsonObject
        {
            ["populationScope"] = "auditPeriod",
            ["position"] = position,
            ["expectedName"] = position is int index ? screen.NameAt(index) : null,
            ["scenario"] = scenario
        });

    private static Task<JsonElement> RemoveLikeTheScreenAsync(HandlerTestHost host, Screen screen, int position) =>
        SendAsync(host, screen, "filter.scenario.remove", new JsonObject
        {
            ["populationScope"] = "auditPeriod",
            ["position"] = position,
            ["expectedName"] = screen.NameAt(position)
        });

    private static Task<JsonElement> RecheckLikeTheScreenAsync(HandlerTestHost host, Screen screen) =>
        SendAsync(host, screen, "filter.scenario.recheck", new JsonObject { ["populationScope"] = "auditPeriod" });

    private static async Task<JsonElement> SendAsync(HandlerTestHost host, Screen screen, string action, JsonObject payload)
    {
        var response = await host.DispatchAsync(action, payload.ToJsonString());
        screen.Show(response.GetProperty("scenarios"));
        return response;
    }

    // ── 資料庫讀寫 ──────────────────────────────────────────────────────────────

    private static ILocalProjectDatabase Database(HandlerTestHost host, string provider)
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        return provider == "sqlite" ? new SqliteProjectDatabase(folder) : new DuckDbProjectDatabase(folder);
    }

    private static async Task<IReadOnlyList<StoredScenario>> ReadScenariosAsync(
        HandlerTestHost host, string projectId, string provider)
    {
        await using var connection = Database(host, provider).CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT position, name, rationale, definition_json, saved_utc FROM config_filter_scenario ORDER BY position;";
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
            "UPDATE config_filter_scenario SET definition_json = @definition WHERE position = @position;",
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
