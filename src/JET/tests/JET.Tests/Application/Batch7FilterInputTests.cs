using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch7FilterInputTests
{
    private const string DateFieldId = "rde.b7000000000000000000000000000001";

    [Theory]
    [InlineData("2025/6/11", true)]
    [InlineData("2025.6.11", true)]
    [InlineData("20250611", true)]
    [InlineData("114/6/11", true)]
    [InlineData("1140611", true)]
    [InlineData("45292", true)]
    [InlineData("2025/6/11", false)]
    public void CoreAndRdeDateOperands_UseTheProjectsGlDateFormats(string value, bool rocEnabled)
    {
        foreach (var rule in new[]
        {
            $$"""{"type":"fieldValue","field":"postDate","operator":"on","value":"{{value}}"}""",
            $$"""{"type":"fieldValue","fieldId":"{{DateFieldId}}","operator":"on","value":"{{value}}"}""",
            $$"""{"type":"typed","fieldId":"{{DateFieldId}}","operator":"on","value":"{{value}}"}"""
        }) Assert.Empty(FilterScenarioValidator.Validate(Parse(rule), Context(rocEnabled)));
    }

    [Theory]
    [InlineData("114/6/11", false)]
    [InlineData("114.6.11", false)]
    [InlineData("11/05/06", true)]
    [InlineData("2025-13-01", true)]
    [InlineData("", true)]
    public void DateOperands_StillRejectDisabledRocAmbiguousInvalidAndBlank(string value, bool rocEnabled)
    {
        var rule = $$"""{"type":"fieldValue","field":"postDate","operator":"on","value":"{{value}}"}""";
        Assert.NotEmpty(FilterScenarioValidator.Validate(Parse(rule), Context(rocEnabled)));
    }

    [Fact]
    public void DateRangesAndLists_NormalizeBeforeComparingOrDeduplicating()
    {
        Assert.Empty(FilterScenarioValidator.Validate(Parse(
            """{"type":"fieldValue","field":"postDate","operator":"between","from":"114/6/1","to":"2025.6.11"}"""), Context(true)));
        Assert.Empty(FilterScenarioValidator.Validate(Parse(
            """{"type":"fieldValue","field":"postDate","operator":"in","values":["114/6/11","20250611"]}"""), Context(true)));
        var reversed = FilterScenarioValidator.Validate(Parse(
            """{"type":"fieldValue","field":"postDate","operator":"between","from":"2025/6/12","to":"114/6/11"}"""), Context(true));
        Assert.Equal(["第 1 組第 1 條：區間的起日不得晚於迄日，請調整日期後再儲存。"], reversed);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("customKeywords")]
    [InlineData("trailingDigits")]
    public void LegacyStringLists_AcceptAllAgreedSeparators(string type)
    {
        var rule = JsonSerializer.Serialize(new { type, field = "description", keywords = " 00，11、22\t33\r\n44,55 " });
        Assert.Equal(["00", "11", "22", "33", "44", "55"], Parse(rule).Groups[0].Rules[0].Keywords);
    }

    [Fact]
    public void TailStringList_AcceptsFullWidthCommaIdeographicCommaAndTab()
    {
        var scenario = Parse(JsonSerializer.Serialize(new
        {
            type = "fieldValue", field = "amount", @operator = "endsWithDigits",
            value = " 00，11、22\t33\r\n44,55 ", amountBasis = "absolute"
        }));
        Assert.Equal(["00", "11", "22", "33", "44", "55"], FieldValueConditions.TailPatterns(scenario.Groups[0].Rules[0]));
        Assert.Empty(FilterScenarioValidator.Validate(scenario, Context(true)));
    }

    [Fact]
    public void StructuredTextValues_PreserveEmbeddedCommasAndTheRawHundredValueLimit()
    {
        var scenario = Parse("""{"type":"fieldValue","field":"description","operator":"in","values":["Alpha,Beta","Gamma、Delta"]}""");
        Assert.Equal(["Alpha,Beta", "Gamma、Delta"], scenario.Groups[0].Rules[0].TypedValues);
        var tooMany = scenario.Groups[0].Rules[0] with
        {
            TypedOperator = "contains", TypedValues = Enumerable.Repeat("duplicate", 101).ToArray()
        };
        var errors = FilterScenarioValidator.Validate(scenario with
        {
            Groups = [new FilterGroupSpec(FilterJoin.And, [tooMany])]
        }, Context(true));
        Assert.Contains(errors, error => error.Contains("100", StringComparison.Ordinal));
    }

    [Fact]
    public void MoneyListErrors_UsePlainLanguageAndPreserveTheExactRulePosition()
    {
        var errors = FilterScenarioValidator.Validate(Parse(
            """{"type":"fieldValue","field":"amount","operator":"in","amountBasis":"signed","values":["10","abc"]}"""), Context(true));
        Assert.Equal(["第 1 組第 1 條：清單第 2 個值：金額「abc」格式無效，請輸入數字。"], errors);
        var detail = Assert.Single(FilterScenarioErrorDetails.Parse(errors));
        Assert.Equal(1, detail.Group);
        Assert.Equal(1, detail.Rule);
        Assert.Equal("清單第 2 個值：金額「abc」格式無效，請輸入數字。", detail.Message);
    }

    [Theory]
    [InlineData(null, "統計區間上限必須是大於或等於 0 的整數。")]
    [InlineData(-1, "統計區間上限必須是大於或等於 0 的整數。")]
    [InlineData(2, "統計區間上限不得小於下限；區間包含兩個端點。")]
    public void FrequencyErrors_DistinguishMissingOrInvalidUpperBoundFromReversedRange(int? to, string expected)
    {
        var errors = FilterScenarioValidator.Validate(Parse(JsonSerializer.Serialize(new
        {
            type = "entityFrequency", field = "accNum", countUnit = "entries", countOperator = "between", countFrom = 3, countTo = to
        })), Context(true));
        Assert.Equal([$"第 1 組第 1 條：{expected}"], errors);
    }

    [Fact]
    public void PositionedErrors_ReadNewPrefixAndKeepNestedChildPosition()
    {
        var details = FilterScenarioErrorDetails.Parse([
            "第 2 組第 3 條 子條件 1：請輸入數字。", "第 4 組：沒有任何規則。", "情境名稱必填。"]);
        Assert.Equal(new JetErrorDetail(2, 3, "子條件 1：請輸入數字。"), details[0]);
        Assert.Equal(new JetErrorDetail(4, null, "沒有任何規則。"), details[1]);
        Assert.Equal(new JetErrorDetail(null, null, "情境名稱必填。"), details[2]);
    }

    private static FilterScenarioSpec Parse(string rule)
    {
        using var doc = JsonDocument.Parse($$"""{"name":"合成輸入","rationale":"固定答案","groups":[{"rules":[{{rule}}]}]}""");
        return FilterScenarioPayloadParser.Parse(doc.RootElement, 100);
    }

    private static FilterValidationContext Context(bool rocEnabled)
    {
        var project = new ProjectDocument("batch7", "", "", "test", "2025-01-01", "2025-12-31", null,
            100, "AwayFromZero", DateTimeOffset.UnixEpoch, 5, ProjectDocument.CurrentSchemaVersion, RocDateEnabled: rocEnabled);
        var mapping = new CommittedMapping(DatasetKind.Gl, new Dictionary<string, string>
        {
            ["postDate"] = "Date", ["accNum"] = "Account", ["amount"] = "Amount", ["description"] = "Description"
        }, "signed", "batch7", DateTimeOffset.UnixEpoch,
            GlOptions: new GlMappingOptions("unmapped", null, new GlManualAutoPolicy([], []),
                [new GlRdeFieldMetadata(DateFieldId, "Review", "合成審核日", "date")]));
        return FilterValidationContextFactory.Create(project, mapping, null, false, GlPopulationScope.AuditPeriod);
    }
}
