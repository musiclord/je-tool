using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 開案時判斷已儲存情境能否改用目前規則的純函式。只有整批都符合目前規則、版本是較舊的已知格式、
/// 母體是查核期間時才改版；其餘情況整批不動。
/// </summary>
public sealed class FilterScenarioRuleUpgradeTests
{
    private static readonly FilterValidationContext Context =
        new(HasLastPeriodStart: true, HasAccountMapping: false, HasAuthorizedPreparers: false);

    private static readonly DateTimeOffset SavedUtc = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 2, 9, 30, 0, TimeSpan.Zero);

    private const string DebitRule = """{"join":"AND","type":"drCrOnly","drCr":"debit"}""";
    private const string UnknownRdeRule =
        """{"join":"AND","type":"typed","fieldId":"rde.aaaa0000aaaa0000aaaa0000aaaa0000","operator":"isBlank"}""";

    private static string Definition(
        string? logicVersion = "filter-2026-09-18-v15",
        string? populationScope = "auditPeriod",
        string rule = DebitRule,
        string extra = "")
    {
        var properties = new List<string>
        {
            "\"name\":\"合成情境\"",
            "\"rationale\":\"合成動機\"",
            $"\"groups\":[{{\"join\":\"AND\",\"rules\":[{rule}]}}]"
        };
        if (logicVersion is not null) properties.Add($"\"logicVersion\":{JsonSerializer.Serialize(logicVersion)}");
        if (populationScope is not null) properties.Add($"\"populationScope\":{JsonSerializer.Serialize(populationScope)}");
        if (extra.Length > 0) properties.Add(extra);
        return "{" + string.Join(",", properties) + "}";
    }

    private static SavedFilterScenario Saved(int position, string definition, DateTimeOffset? savedUtc = null) =>
        new(position, $"情境{position}", $"動機{position}", definition, savedUtc ?? SavedUtc);

    private static FilterScenarioUpgradeResult Evaluate(params SavedFilterScenario[] scenarios) =>
        FilterScenarioRuleUpgrade.Evaluate(scenarios, Context, moneyScale: 100, NowUtc);

    [Fact]
    public void NoSavedScenarios_IsCurrent()
    {
        var result = Evaluate();

        Assert.Equal(FilterScenarioUpgradeStatus.Current, result.Status);
        Assert.Null(result.Scenarios);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void CurrentBatch_IsRevalidatedOnLoad()
    {
        // 改動原因：使用者 2026-10-13 裁定「開案也逐一檢查」，拿掉「已是目前版本就不驗證」的捷徑；原本斷言 Current
        // （問題留給補算時回報）。第一次失敗：Focused 收據 20261013-133703293-a159a47c0ec64441a7dbc9702c86d15a。
        // 同一個不合格的目前版本情境，現在開案就列在待修正清單；合格的目前版本仍回 Current，見 CurrentValidBatch_StaysCurrentWithoutNewVersions。
        var result = Evaluate(Saved(1, Definition(RuleLogicVersions.Filter, rule: UnknownRdeRule)));

        Assert.Equal(FilterScenarioUpgradeStatus.NeedsEdit, result.Status);
        Assert.Null(result.Scenarios);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(1, problem.Position);
        Assert.Equal("情境1", problem.Name);
    }

    // 使用者 2026-10-13 裁定「開案也逐一檢查」：已是目前版本、但因其他資料改變而不合格的情境，開案時就列在待修正清單；
    // 合格的目前版本照舊回 Current，不重新蓋版本。
    [Fact]
    public void CurrentVersionThatNoLongerValidates_IsListedOnLoad()
    {
        var result = Evaluate(
            Saved(1, Definition(RuleLogicVersions.Filter)),
            Saved(2, Definition(RuleLogicVersions.Filter, rule: UnknownRdeRule)));

        Assert.Equal(FilterScenarioUpgradeStatus.NeedsEdit, result.Status);
        Assert.Null(result.Scenarios);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(2, problem.Position);
        Assert.Equal("情境2", problem.Name);
        Assert.NotEmpty(problem.Messages);
    }

    [Fact]
    public void CurrentValidBatch_StaysCurrentWithoutNewVersions()
    {
        var result = Evaluate(Saved(1, Definition(RuleLogicVersions.Filter)), Saved(2, Definition(RuleLogicVersions.Filter)));

        Assert.Equal(FilterScenarioUpgradeStatus.Current, result.Status);
        Assert.Null(result.Scenarios);
        Assert.Empty(result.Problems);
    }

    [Theory]
    [InlineData("filter-2026-08-04-v6", "auditPeriod")]
    [InlineData("filter-2026-09-18-v15", null)]
    [InlineData("filter-2026-09-18-v16", null)]
    public void OlderKnownVersion_ValidAgainstCurrentRules_IsUpgradedWithNewSavedTime(
        string logicVersion, string? populationScope)
    {
        var original = Definition(logicVersion, populationScope, extra: "\"unknownTop\":1.50");
        var result = Evaluate(Saved(1, original), Saved(2, Definition(logicVersion, populationScope)));

        Assert.Equal(FilterScenarioUpgradeStatus.Upgraded, result.Status);
        Assert.Empty(result.Problems);
        var upgraded = Assert.IsAssignableFrom<IReadOnlyList<SavedFilterScenario>>(result.Scenarios);
        Assert.Equal([1, 2], upgraded.Select(item => item.Position));
        Assert.All(upgraded, item => Assert.Equal(NowUtc, item.SavedUtc));
        Assert.Equal("情境1", upgraded[0].Name);
        Assert.Equal("動機1", upgraded[0].Rationale);
        Assert.All(upgraded, item => Assert.True(RuleLogicVersions.IsCurrent(item)));

        var before = JsonNode.Parse(original)!.AsObject();
        var after = JsonNode.Parse(upgraded[0].DefinitionJson)!.AsObject();
        Assert.Equal(RuleLogicVersions.Filter, after["logicVersion"]!.GetValue<string>());
        Assert.Equal(GlPopulationScopeValues.AuditPeriod, after["populationScope"]!.GetValue<string>());
        Assert.Equal("1.50", after["unknownTop"]!.ToJsonString());
        before.Remove("logicVersion");
        before.Remove("populationScope");
        after.Remove("logicVersion");
        after.Remove("populationScope");
        Assert.True(JsonNode.DeepEquals(before, after));
    }

    [Theory]
    [InlineData("filter-2099-01-01-v99")]
    [InlineData("filter-2026-09-18-v16-beta")]
    [InlineData("legacy-v3")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownOrNewerVersion_NeedsEdit(string? logicVersion)
    {
        var result = Evaluate(Saved(1, Definition(logicVersion)));

        Assert.Equal(FilterScenarioUpgradeStatus.NeedsEdit, result.Status);
        Assert.Null(result.Scenarios);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(1, problem.Position);
        Assert.Equal("情境1", problem.Name);
        Assert.Contains(FilterScenarioRuleUpgrade.UnknownVersionMessage, problem.Messages);
    }

    [Fact]
    public void NonAuditPeriodScope_NeedsEditInsteadOfSilentlyChangingThePopulation()
    {
        var result = Evaluate(Saved(1, Definition(populationScope: "allProjected")));

        Assert.Equal(FilterScenarioUpgradeStatus.NeedsEdit, result.Status);
        var problem = Assert.Single(result.Problems);
        Assert.Contains(FilterScenarioRuleUpgrade.NonAuditPeriodMessage, problem.Messages);
    }

    [Fact]
    public void RemovedExclusions_NeedsEditWithParserMessage()
    {
        var result = Evaluate(Saved(1, Definition(extra: "\"exclusions\":[" + DebitRule + "]")));

        Assert.Equal(FilterScenarioUpgradeStatus.NeedsEdit, result.Status);
        var message = Assert.Single(Assert.Single(result.Problems).Messages);
        Assert.Contains("排除區域", message, StringComparison.Ordinal);
    }

    [Fact]
    public void OneInvalidScenario_KeepsTheWholeBatchAndListsOnlyThatScenario()
    {
        var result = Evaluate(
            Saved(1, Definition()),
            Saved(2, Definition(rule: UnknownRdeRule)));

        Assert.Equal(FilterScenarioUpgradeStatus.NeedsEdit, result.Status);
        Assert.Null(result.Scenarios);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(2, problem.Position);
        Assert.Equal("情境2", problem.Name);
        Assert.NotEmpty(problem.Messages);
    }

    [Fact]
    public void MixedSavedTimes_AreInconsistent()
    {
        var result = Evaluate(
            Saved(1, Definition()),
            Saved(2, Definition(), SavedUtc.AddSeconds(1)));

        Assert.Equal(FilterScenarioUpgradeStatus.Inconsistent, result.Status);
        Assert.Null(result.Scenarios);
        Assert.Empty(result.Problems);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, FilterScenarioLimits.MaxSavedScenarios + 1)]
    public void DuplicateOrOutOfRangePositions_AreInconsistent(int first, int second)
    {
        var result = Evaluate(Saved(first, Definition()), Saved(second, Definition()));

        Assert.Equal(FilterScenarioUpgradeStatus.Inconsistent, result.Status);
    }

    // 改動原因：使用者 2026-10-13「損毀的情境」裁定開案清單一律寫「條件內容讀不出來」，「情境卡住」裁定待修正的情境要能一個一個刪；
    // 讀不出來的情境因此改列成這個情境的一筆問題，不再讓整批變成沒有清單的 Inconsistent（保存時間與位置的結構問題仍是 Inconsistent）。
    // 原斷言（Status 為 Inconsistent）的第一次失敗：Public 收據 20261013-103826650-632ded11dde247d0b97d4e5ea6dd63bf。
    // 預期的訊息常數與位置、名稱都寫死，整批照舊不動（Scenarios 為 null）。
    [Theory]
    [InlineData("[]")]
    [InlineData("{\"logicVersion\":\"filter-2026-09-18-v15\",\"populationScope\":7,\"groups\":[]}")]
    [InlineData("not json")]
    public void UnreadableDefinition_IsListedAsThatScenariosProblem(string definition)
    {
        var result = Evaluate(Saved(1, Definition()), Saved(2, definition));

        Assert.Equal(FilterScenarioUpgradeStatus.NeedsEdit, result.Status);
        Assert.Null(result.Scenarios);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(2, problem.Position);
        Assert.Equal("情境2", problem.Name);
        // 使用者 2026-10-13 勾選頁確認（文字批次 C）：B3 改為「條件內容無法讀取」。第一次失敗：收據 20261013-141745059-73ff4afda2d1464bbfbe918459a13666。
        Assert.Equal(["條件內容無法讀取"], problem.Messages);
    }

    // ── 一次只存、刪或重新檢查一個情境時寫回的整批（2026-10-13）──

    [Fact]
    public void PlanWrite_AllValid_RenumbersStampsEveryRowAndUsesOneSavedTime()
    {
        var batch = new[]
        {
            Saved(3, Definition()),
            Saved(7, Definition(RuleLogicVersions.Filter), SavedUtc.AddSeconds(5))
        };

        var write = FilterScenarioRuleUpgrade.PlanWrite(
            batch, null, FilterScenarioWriteMode.KeepOthersVerbatim, Context, moneyScale: 100, NowUtc);

        Assert.True(write.AllValid);
        Assert.Empty(write.Problems);
        Assert.Equal([1, 2], write.Rows.Select(row => row.Position));
        Assert.All(write.Rows, row => Assert.Equal(NowUtc, row.SavedUtc));
        Assert.All(write.Rows, row => Assert.True(RuleLogicVersions.IsCurrent(row)));
        Assert.Equal(["情境3", "情境7"], write.Rows.Select(row => row.Name));
        Assert.Equal(1, write.UpgradedOthers);
    }

    [Fact]
    public void PlanWrite_KeepOthersVerbatim_LeavesEveryOtherDefinitionUntouchedWhenOneStillNeedsEdits()
    {
        var invalid = Definition(rule: UnknownRdeRule);
        var olderValid = Definition();
        var edited = Definition(RuleLogicVersions.Filter);
        var batch = new[] { Saved(1, invalid), Saved(2, edited), Saved(3, olderValid) };

        var write = FilterScenarioRuleUpgrade.PlanWrite(
            batch, 2, FilterScenarioWriteMode.KeepOthersVerbatim, Context, moneyScale: 100, NowUtc);

        Assert.False(write.AllValid);
        Assert.Equal([invalid, edited, olderValid], write.Rows.Select(row => row.DefinitionJson));
        Assert.All(write.Rows, row => Assert.Equal(NowUtc, row.SavedUtc));
        var problem = Assert.Single(write.Problems);
        Assert.Equal(1, problem.Position);
        Assert.Equal(0, write.UpgradedOthers);
    }

    [Fact]
    public void PlanWrite_UpgradeApplicable_StampsTheValidOnesAndKeepsTheInvalidOnesVerbatim()
    {
        var invalid = Definition(rule: UnknownRdeRule);
        var batch = new[] { Saved(1, invalid), Saved(2, Definition()), Saved(3, "not json") };

        var write = FilterScenarioRuleUpgrade.PlanWrite(
            batch, null, FilterScenarioWriteMode.UpgradeApplicable, Context, moneyScale: 100, NowUtc);

        Assert.False(write.AllValid);
        Assert.Equal(invalid, write.Rows[0].DefinitionJson);
        Assert.True(RuleLogicVersions.IsCurrent(write.Rows[1]));
        Assert.Equal("not json", write.Rows[2].DefinitionJson);
        Assert.Equal([1, 3], write.Problems.Select(problem => problem.Position));
        // 使用者 2026-10-13 勾選頁確認（文字批次 C）：B3 改為「條件內容無法讀取」。第一次失敗：收據 20261013-141745059-73ff4afda2d1464bbfbe918459a13666。
        Assert.Equal(["條件內容無法讀取"], write.Problems[1].Messages);
        Assert.Equal(1, write.UpgradedOthers);
    }

    [Fact]
    public void PlanWrite_ChecksCurrentVersionsToo()
    {
        var write = FilterScenarioRuleUpgrade.PlanWrite(
            [Saved(1, Definition(RuleLogicVersions.Filter, rule: UnknownRdeRule))],
            null, FilterScenarioWriteMode.UpgradeApplicable, Context, moneyScale: 100, NowUtc);

        Assert.False(write.AllValid);
        Assert.Single(write.Problems);
    }

    [Fact]
    public void PlanWrite_EmptyBatch_IsAllValid()
    {
        var write = FilterScenarioRuleUpgrade.PlanWrite(
            [], null, FilterScenarioWriteMode.KeepOthersVerbatim, Context, moneyScale: 100, NowUtc);

        Assert.True(write.AllValid);
        Assert.Empty(write.Rows);
    }
}
