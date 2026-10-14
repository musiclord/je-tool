using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class FilterSinglePurposeRetirementTests
{
    private static readonly FilterValidationContext Context = new(true, true, true, HasAnyAccountCategory: true)
    {
        HasCreatedBy = true, HasDescription = true
    };

    public static TheoryData<string, string> RetiredRules => new()
    {
        { """{"type":"fieldValue","field":"accNum","operator":"in","values":["A"],"drCr":"debit"}""", "科目編號條件裡的分錄方向已停用" },
        { """{"type":"fieldValue","field":"accName","operator":"in","values":["A"],"drCr":"credit"}""", "科目名稱條件裡的分錄方向已停用" },
        { """{"type":"accountSide","drCr":"debit","categoryMode":"is","categoryIds":["builtin.cash"]}""", "科目分類條件裡的借貸方向已停用" },
        { """{"type":"accountSide","drCr":"any","categoryMode":"is","categoryIds":["builtin.cash"]}""", "科目分類條件裡的借貸方向已停用" },
        { """{"type":"accountSide","categoryMode":"absent","categoryIds":["builtin.cash"]}""", "科目分類條件的「這一側都不屬於」已停用" },
        { """{"type":"voucher","side":"all","quantifier":"any","rules":[{"type":"drCrOnly","drCr":"debit"}]}""", "整張傳票條件的借貸範圍已停用" },
        { """{"type":"voucher","side":"credit","quantifier":"all","rules":[{"type":"drCrOnly","drCr":"credit"}]}""", "整張傳票條件的借貸範圍已停用" },
        { """{"type":"fieldValue","field":"amount","operator":"greaterThan","value":"10","amountBasis":"signed"}""", "金額條件的「含正負號」已停用" },
        { """{"type":"customKeywords","keywords":"調整"}""", "舊式摘要關鍵字條件已停用" },
        { """{"type":"customTrailingZeros","digits":1}""", "舊式連續零尾數條件已停用" },
        { """{"type":"trailingDigits","keywords":"001"}""", "舊式金額尾數條件已停用" }
    };

    [Theory]
    [MemberData(nameof(RetiredRules))]
    public void RetiredSyntax_CannotPreviewOrSave_AndSavedDefinitionsAreKeptVerbatim(string rule, string reason)
    {
        var definition = $$"""{"logicVersion":"filter-2026-10-08-v19","name":"合成舊情境","rationale":"不自動轉換","groups":[{"rules":[{"type":"group","rules":[{{rule}}]}]}]}""";
        AssertRetired(definition, reason);
    }

    [Fact]
    public void SameVoucherScope_IsRetiredWithInstructionsToKeepTheFirstRule()
    {
        const string definition = """
            {"logicVersion":"filter-2026-10-08-v19","name":"合成舊情境","rationale":"不自動轉換","groups":[{"matchScope":"sameVoucher","rules":[
              {"type":"drCrOnly","drCr":"debit"},{"type":"fieldValue","field":"accNum","operator":"in","values":["A"]}]}]}
            """;
        AssertRetired(definition, "條件組的「同一傳票」比對範圍已停用");
    }

    private static void AssertRetired(string definition, string reason)
    {
        var spec = FilterScenarioPayloadParser.Parse(JsonElement.Parse(definition), 100);
        foreach (var forSave in new[] { false, true })
        {
            var errors = FilterScenarioValidator.Validate(spec, Context, forSave);
            Assert.Contains(errors, text => text.Contains(reason, StringComparison.Ordinal) && text.Contains("請", StringComparison.Ordinal));
        }
        var saved = new SavedFilterScenario(1, "合成舊情境", "不自動轉換", definition, DateTimeOffset.UnixEpoch);
        var review = FilterScenarioRuleUpgrade.Evaluate([saved], Context, 100, DateTimeOffset.UnixEpoch.AddDays(1));
        Assert.Equal(FilterScenarioUpgradeStatus.NeedsEdit, review.Status);
        Assert.Null(review.Scenarios);
        Assert.Contains(Assert.Single(review.Problems).Messages, text => text.Contains(reason, StringComparison.Ordinal));
        var write = FilterScenarioRuleUpgrade.PlanWrite([saved], null, FilterScenarioWriteMode.KeepOthersVerbatim,
            Context, 100, DateTimeOffset.UnixEpoch.AddDays(1));
        Assert.False(write.AllValid);
        Assert.Equal(definition, Assert.Single(write.Rows).DefinitionJson);
    }

    [Fact]
    public void NewSinglePurposeRules_AreAcceptedAndUseANewLogicVersion()
    {
        var spec = FilterScenarioPayloadParser.Parse(JsonElement.Parse("""
            {"name":"合成新情境","rationale":"明確組合","groups":[{"rules":[
              {"type":"drCrOnly","drCr":"debit"},
              {"type":"accountSide","categoryMode":"is","categoryIds":["builtin.cash"]},
              {"type":"fieldValue","field":"amount","operator":"greaterThan","value":"10","amountBasis":"absolute"},
              {"type":"voucher","quantifier":"none","rules":[{"type":"drCrOnly","drCr":"credit"}]}]}]}
            """), 100);
        Assert.Empty(FilterScenarioValidator.Validate(spec, Context));
        Assert.Equal("filter-2026-10-14-v20", RuleLogicVersions.Filter);
    }
}
