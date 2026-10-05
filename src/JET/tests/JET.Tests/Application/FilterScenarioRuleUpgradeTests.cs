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
    public void CurrentBatch_IsCurrentWithoutRevalidation()
    {
        // 已是目前版本的整批維持改版前的行為：開案時不重新驗證，問題留給補算時回報。
        var result = Evaluate(Saved(1, Definition(RuleLogicVersions.Filter, rule: UnknownRdeRule)));

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

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"logicVersion\":\"filter-2026-09-18-v15\",\"populationScope\":7,\"groups\":[]}")]
    [InlineData("not json")]
    public void UnreadableDefinition_IsInconsistent(string definition)
    {
        var result = Evaluate(Saved(1, definition));

        Assert.Equal(FilterScenarioUpgradeStatus.Inconsistent, result.Status);
        Assert.Null(result.Scenarios);
    }
}
