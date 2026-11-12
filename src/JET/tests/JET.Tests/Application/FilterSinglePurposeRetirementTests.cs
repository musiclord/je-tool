using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class FilterSinglePurposeRetirementTests
{
    // 規則需求表讀可用欄位清單；不帶清單就不檢查欄位。
    private static readonly FilterValidationContext Context = new(true, true, true, HasAnyAccountCategory: true);

    // 借貸方向、借貸範圍、「這一側都不屬於」與核心金額的「含正負號」當成無法讀取的內容，
    // 五種停用的型別和其他認不得的類型一樣寫「此條件的類型已停用」。預覽與儲存都擋下，已存的原文不改，
    // 原因不寫重建方式。
    public static TheoryData<string> RetiredSettings => new()
    {
        """{"type":"fieldValue","field":"accountNumber","operator":"in","values":["A"],"drCr":"debit"}""",
        """{"type":"fieldValue","field":"accountName","operator":"in","values":["A"],"drCr":"credit"}""",
        """{"type":"accountSide","drCr":"debit","categoryMode":"is","categoryIds":["builtin.cash"]}""",
        """{"type":"accountSide","drCr":"any","categoryMode":"is","categoryIds":["builtin.cash"]}""",
        """{"type":"accountSide","categoryMode":"absent","categoryIds":["builtin.cash"]}""",
        """{"type":"voucher","side":"all","quantifier":"any","conditions":[{"type":"drCrOnly","drCr":"debit"}]}""",
        """{"type":"voucher","side":"credit","quantifier":"all","conditions":[{"type":"drCrOnly","drCr":"credit"}]}""",
        """{"type":"fieldValue","field":"amount","operator":"greaterThan","value":"10","amountBasis":"signed"}"""
    };

    public static TheoryData<string> RetiredTypes => new()
    {
        """{"type":"customKeywords","keywords":"調整"}""",
        """{"type":"customTrailingZeros","digits":1}""",
        """{"type":"trailingDigits","keywords":"001"}""",
        """{"type":"customPreparerEntryCount","maxEntries":11}""",
        """{"type":"customAccountEntryCount","maxEntries":11}"""
    };

    [Theory]
    [MemberData(nameof(RetiredSettings))]
    public void RetiredSyntax_CannotPreviewOrSave_AndSavedDefinitionsAreKeptVerbatim(string rule)
    {
        var definition = Definition($$"""{"conditions":[{"type":"group","conditions":[{{rule}}]}]}""");
        var spec = FilterScenarioPayloadParser.Parse(JsonElement.Parse(definition), 100);
        foreach (var forSave in new[] { false, true })
            Assert.Contains("第 1 組第 1 條 子條件 1：" + FilterScenarioCorruption.RuleMessage,
                FilterScenarioValidator.Validate(spec, Context, forSave));
        AssertListedAndKept(definition, FilterScenarioRuleUpgrade.UnreadableDefinitionMessage);
    }

    [Theory]
    [MemberData(nameof(RetiredTypes))]
    public void RetiredTypes_ReadAsDisabledTypes_AndSavedDefinitionsAreKeptVerbatim(string rule)
    {
        var definition = Definition($$"""{"conditions":[{"type":"group","conditions":[{{rule}}]}]}""");
        var exception = Assert.Throws<JetActionException>(() =>
            FilterScenarioPayloadParser.Parse(JsonElement.Parse(definition), 100));
        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
        Assert.Equal("第 1 組第 1 條 子條件 1：此條件的類型已停用，請刪除此條件", exception.Message);
        AssertListedAndKept(definition, exception.Message);
    }

    [Fact]
    public void SameVoucherScope_IsUnreadable_AndTheSavedDefinitionIsKeptVerbatim()
    {
        var definition = Definition("""
            {"matchScope":"sameVoucher","conditions":[
              {"type":"drCrOnly","drCr":"debit"},{"type":"fieldValue","field":"accountNumber","operator":"in","values":["A"]}]}
            """);
        var spec = FilterScenarioPayloadParser.Parse(JsonElement.Parse(definition), 100);
        foreach (var forSave in new[] { false, true })
            Assert.Contains("第 1 組：" + FilterScenarioCorruption.RuleMessage, FilterScenarioValidator.Validate(spec, Context, forSave));
        AssertListedAndKept(definition, FilterScenarioRuleUpgrade.UnreadableDefinitionMessage);
    }

    private static string Definition(string group) =>
        $$"""{"logicVersion":"filter-2026-10-08-v19","name":"合成舊情境","rationale":"不自動轉換","groups":[{{group}}]}""";

    // 開案檢查把情境列為待修正並寫出原因，重新寫回時原文不改。
    private static void AssertListedAndKept(string definition, string message)
    {
        var saved = new SavedFilterScenario(1, "合成舊情境", "不自動轉換", definition, DateTimeOffset.UnixEpoch);
        var review = FilterScenarioRuleUpgrade.Evaluate([saved], Context, 100, DateTimeOffset.UnixEpoch.AddDays(1));
        Assert.Equal(FilterScenarioUpgradeStatus.NeedsEdit, review.Status);
        Assert.Null(review.Scenarios);
        Assert.Contains(message, Assert.Single(review.Problems).Messages);
        var write = FilterScenarioRuleUpgrade.PlanWrite([saved], null, FilterScenarioWriteMode.KeepOthersVerbatim,
            Context, 100, DateTimeOffset.UnixEpoch.AddDays(1));
        Assert.False(write.AllValid);
        Assert.Equal(definition, Assert.Single(write.Rows).DefinitionJson);
    }

    [Fact]
    public void NewSinglePurposeRules_AreAcceptedAndUseANewLogicVersion()
    {
        var spec = FilterScenarioPayloadParser.Parse(JsonElement.Parse("""
            {"name":"合成新情境","rationale":"明確組合","groups":[{"conditions":[
              {"type":"drCrOnly","drCr":"debit"},
              {"type":"accountSide","categoryMode":"is","categoryIds":["builtin.cash"]},
              {"type":"fieldValue","field":"amount","operator":"greaterThan","value":"10","amountBasis":"absolute"},
              {"type":"voucher","quantifier":"none","conditions":[{"type":"drCrOnly","drCr":"credit"}]}]}]}
            """), 100);
        Assert.Empty(FilterScenarioValidator.Validate(spec, Context));
        Assert.Equal("filter-2026-10-14-v20", RuleLogicVersions.Filter);
    }
}
