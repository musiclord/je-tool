using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>進階條件篩選的 wire、Domain 與 renderer 契約；所有內容皆為合成值。</summary>
public sealed class AdvancedFilterAstContractTests
{
    private static readonly FilterValidationContext Ready =
        new(HasPrepStartDate: true, HasAccountMapping: false, HasAuthorizedPreparers: false);

    [Theory]
    [InlineData("mystery", 2, "AND", "group_match_scope_invalid")]
    // 已儲存情境沒有「同一傳票」比對範圍，讀到 sameVoucher 時當成無法讀取的內容，原因和其他未知值相同。
    [InlineData("sameVoucher", 2, "AND", "group_match_scope_invalid")]
    public void Validate_InvalidSameVoucherGroup_FailsLoudly(
        string matchScope,
        int ruleCount,
        string secondJoin,
        string expected)
    {
        var rules = new List<object>
        {
            new
            {
                join = "AND", type = "fieldValue", field = "description",
                @operator = "in", values = new[] { "anchor" }
            }
        };
        if (ruleCount > 1)
        {
            rules.Add(new
            {
                join = secondJoin, type = "fieldValue", field = "accountName",
                @operator = "in", values = new[] { "evidence" }
            });
        }

        var scenario = Parse(JsonSerializer.Serialize(new
        {
            name = "synthetic",
            rationale = "invalid contract",
            groups = new[] { new { join = "AND", matchScope, conditions = rules } }
        }));

        var errors = FilterScenarioValidator.Validate(scenario, Ready);
        Assert.Contains("第 1 組：此條件內容有誤，無法儲存。請刪除此條件後重新設定。", errors);
        Assert.Contains(FilterScenarioCorruption.ReasonsOf(errors), reason => reason.Code == expected);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("null")]
    [InlineData("[\"valid\", 2]")]
    public void Parse_TextSetValuesThatAreNotAStringArray_ThrowsInvalidScenario(string valuesJson)
    {
        var json = """
            {"name":"synthetic","rationale":"shape","groups":[{"conditions":[
              {"type":"fieldValue","field":"description","operator":"in","values":__VALUES__}
            ]}]}
            """.Replace("__VALUES__", valuesJson, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(json);
        var exception = Assert.Throws<JetActionException>(
            () => FilterScenarioPayloadParser.Parse(document.RootElement, moneyScale: 100));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
    }

    [Theory]
    [InlineData("matchScope")]
    [InlineData("type")]
    [InlineData("operator")]
    public void ParseAndValidate_PaddedClosedToken_FailsBeforePersistenceOrReadBack(string property)
    {
        // 合格的原文用 row，前後加空白的那一份要被擋下。
        const string canonical =
            """
            {"name":"synthetic","rationale":"closed tokens","groups":[{"matchScope":"row","conditions":[
              {"join":"AND","type":"fieldValue","field":"description","operator":"in","values":["anchor"]},
              {"join":"AND","type":"fieldValue","field":"accountName","operator":"contains","values":["evidence"]}
            ]}]}
            """;
        var padded = property switch
        {
            "matchScope" => canonical.Replace(
                "\"matchScope\":\"row\"",
                "\"matchScope\":\" row \"",
                StringComparison.Ordinal),
            "type" => canonical.Replace(
                "\"type\":\"fieldValue\"",
                "\"type\":\" fieldValue \"",
                StringComparison.Ordinal),
            "operator" => canonical.Replace(
                "\"operator\":\"in\"",
                "\"operator\":\" in \"",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(property), property, null)
        };

        try
        {
            var scenario = Parse(padded);
            Assert.NotEmpty(FilterScenarioValidator.Validate(scenario, Ready));
        }
        catch (JetActionException exception)
        {
            Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
        }
    }

    [Fact]
    public void Parse_OmittedMatchScope_KeepsExistingRowSemantics()
    {
        var scenario = Parse(
            """
            {"name":"synthetic","rationale":"legacy row","groups":[{"join":"OR","conditions":[
              {"join":"AND","type":"fieldValue","field":"description","operator":"contains","values":["alpha"]},
              {"join":"OR","type":"manualAuto","isManual":true}
            ]}]}
            """);

        var group = Assert.Single(scenario.Groups);
        // 比對範圍只有同一分錄列，這裡看沒有未知值。
        Assert.Null(group.UnknownMatchScope);
        Assert.Equal(FilterJoin.Or, group.Conditions[1].Join);
        Assert.Empty(FilterScenarioValidator.Validate(scenario, Ready));
    }

    /* ---- 借貸組合雙側多選的 wire 形狀 ------------------------------------- */

    [Fact]
    public void Parse_PairCategoryIdArrays_AreAuthoritativeAndSuppressLegacyScalars()
    {
        var scenario = Parse(
            """
            {"name":"synthetic","rationale":"contract","groups":[{"join":"AND","conditions":[
              {"join":"AND","type":"accountPair","pairMode":"exact",
               "debitCategory":"Cash","creditCategory":"Others",
               "debitCategoryIds":["builtin.receivables","builtin.cash"],
               "creditCategoryIds":["builtin.revenue"]}
            ]}]}
            """);

        var rule = scenario.Groups[0].Conditions[0];
        Assert.Equal(["builtin.receivables", "builtin.cash"], rule.DebitCategoryIds);
        Assert.Equal(["builtin.revenue"], rule.CreditCategoryIds);
        // 規則沒有單選分類欄位；單選欄位只是未知欄位，判定只看陣列。
        Assert.Equal(["builtin.cash", "builtin.receivables"], rule.EffectiveDebitCategoryIds);
    }

    [Fact]
    public void Parse_PairWithoutArrays_IgnoresSingleCategoryFields()
    {
        var scenario = Parse(
            """
            {"name":"synthetic","rationale":"contract","groups":[{"join":"AND","conditions":[
              {"join":"AND","type":"specialAccountCategoryPair","pairMode":"drAndCr",
               "debitCategory":"Revenue","creditCategory":"Cash"}
            ]}]}
            """);

        // 只認分類身分陣列；單選欄位照未知欄位處理，不換算成內建分類。
        var rule = scenario.Groups[0].Conditions[0];
        Assert.Empty(rule.EffectiveDebitCategoryIds);
        Assert.Empty(rule.EffectiveCreditCategoryIds);
    }

    [Fact]
    public void Parse_ExplicitEmptyPairArray_DoesNotFallBackToScalar()
    {
        var scenario = Parse(
            """
            {"name":"synthetic","rationale":"contract","groups":[{"join":"AND","conditions":[
              {"join":"AND","type":"accountPair","pairMode":"exact",
               "debitCategory":"Cash","debitCategoryIds":[],
               "creditCategoryIds":["builtin.revenue"]}
            ]}]}
            """);

        var rule = scenario.Groups[0].Conditions[0];
        Assert.Empty(rule.EffectiveDebitCategoryIds);
        Assert.Contains(
            FilterScenarioValidator.Validate(scenario, Ready with
            {
                HasAccountMapping = true,
                HasAnyAccountCategory = true
            }),
            error => error == "第 1 組第 1 條：尚未選擇借方分類");
    }

    [Theory]
    [InlineData("""{"join":"AND","type":"accountPair","pairMode":"exact","debitCategoryIds":"builtin.cash"}""")]
    [InlineData("""{"join":"AND","type":"accountPair","pairMode":"exact","creditCategoryIds":[1]}""")]
    public void Parse_MalformedPairArray_ThrowsInvalidScenario(string ruleJson)
    {
        var json = $$"""{"name":"synthetic","rationale":"contract","groups":[{"join":"AND","conditions":[{{ruleJson}}]}]}""";
        using var document = JsonDocument.Parse(json);

        var exception = Assert.Throws<JetActionException>(
            () => FilterScenarioPayloadParser.Parse(document.RootElement, moneyScale: 100));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
    }

    [Fact]
    public void Parse_PairArrayBeyondPerSideLimit_ThrowsInvalidScenario()
    {
        var tooMany = string.Join(
            ",",
            Enumerable
                .Range(0, FilterScenarioLimits.MaxCategoryIdsPerSide + 1)
                .Select(static index => $"\"custom.{index:x32}\""));
        var json = $$"""
            {"name":"synthetic","rationale":"contract","groups":[{"join":"AND","conditions":[
              {"join":"AND","type":"accountPair","pairMode":"debitAnchor","debitCategoryIds":[{{tooMany}}]}
            ]}]}
            """;
        using var document = JsonDocument.Parse(json);

        var exception = Assert.Throws<JetActionException>(
            () => FilterScenarioPayloadParser.Parse(document.RootElement, moneyScale: 100));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
    }

    private static FilterScenarioSpec Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return FilterScenarioPayloadParser.Parse(document.RootElement, moneyScale: 100);
    }
}
