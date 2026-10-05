using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>Stage 5 wire／Domain／renderer 契約；所有內容皆為合成值。</summary>
public sealed class AdvancedFilterAstContractTests
{
    private static readonly FilterValidationContext Ready =
        new(HasLastPeriodStart: true, HasAccountMapping: false, HasAuthorizedPreparers: false);

    [Fact]
    public void ParseAndValidate_SameVoucherTextSet_PreservesClosedWireShape()
    {
        var scenario = Parse(
            """
            {"name":"synthetic","rationale":"contract","groups":[{"join":"AND","matchScope":"sameVoucher","rules":[
              {"join":"AND","type":"textSet","field":"description","values":["anchor"],"mode":"exact","normalization":"preserve"},
              {"join":"AND","type":"textSet","field":"accName","values":["evi dence"],"mode":"contains","normalization":"removeAsciiSpaces"}
            ]}]}
            """);

        var group = Assert.Single(scenario.Groups);
        Assert.Equal(FilterGroupMatchScope.SameVoucher, group.MatchScope);
        Assert.Null(group.UnknownMatchScope);
        Assert.Equal(FilterRuleType.TextSet, group.Rules[0].Type);
        Assert.Equal(["anchor"], group.Rules[0].Values);
        Assert.Equal(TextSetNormalization.Preserve, group.Rules[0].Normalization);
        Assert.Equal(TextSetNormalization.RemoveAsciiSpaces, group.Rules[1].Normalization);
        Assert.Empty(FilterScenarioValidator.Validate(scenario, Ready));
    }

    [Theory]
    [InlineData("mystery", 2, "AND", "matchScope")]
    [InlineData("sameVoucher", 1, "AND", "至少需要兩條")]
    [InlineData("sameVoucher", 2, "OR", "只允許 AND")]
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
                join = "AND", type = "textSet", field = "description",
                values = new[] { "anchor" }, mode = "contains", normalization = "preserve"
            }
        };
        if (ruleCount > 1)
        {
            rules.Add(new
            {
                join = secondJoin, type = "textSet", field = "accName",
                values = new[] { "evidence" }, mode = "exact", normalization = "preserve"
            });
        }

        var scenario = Parse(JsonSerializer.Serialize(new
        {
            name = "synthetic",
            rationale = "invalid contract",
            groups = new[] { new { join = "AND", matchScope, rules } }
        }));

        Assert.Contains(
            FilterScenarioValidator.Validate(scenario, Ready),
            error => error.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("notContains", "preserve", "value", "contains 或 exact")]
    [InlineData("contains", "unknown", "value", "normalization")]
    [InlineData("contains", "removeAsciiSpaces", "   ", "正規化後不可為空")]
    public void Validate_InvalidTextSetContract_FailsLoudly(
        string mode,
        string normalization,
        string value,
        string expected)
    {
        var scenario = Parse(JsonSerializer.Serialize(new
        {
            name = "synthetic",
            rationale = "invalid text set",
            groups = new[]
            {
                new
                {
                    join = "AND",
                    matchScope = "row",
                    rules = new[]
                    {
                        new
                        {
                            join = "AND", type = "textSet", field = "description",
                            values = new[] { value }, mode, normalization
                        }
                    }
                }
            }
        }));

        Assert.Contains(
            FilterScenarioValidator.Validate(scenario, Ready),
            error => error.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("null")]
    [InlineData("[\"valid\", 2]")]
    public void Parse_TextSetValuesThatAreNotAStringArray_ThrowsInvalidScenario(string valuesJson)
    {
        var json = """
            {"name":"synthetic","rationale":"shape","groups":[{"rules":[
              {"type":"textSet","field":"description","mode":"contains","values":__VALUES__}
            ]}]}
            """.Replace("__VALUES__", valuesJson, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(json);
        var exception = Assert.Throws<JetActionException>(
            () => FilterScenarioPayloadParser.Parse(document.RootElement, moneyScale: 100));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
    }

    [Fact]
    public void Validate_TextSetValueLimit_IsBackendAuthoritative()
    {
        var scenario = Parse(
            """
            {"name":"synthetic","rationale":"bounded values","groups":[{"rules":[
              {"type":"textSet","field":"description","mode":"contains","normalization":"preserve","values":["seed"]}
            ]}]}
            """);
        var group = Assert.Single(scenario.Groups);
        var rule = Assert.Single(group.Rules) with
        {
            Values = Enumerable.Range(0, FilterScenarioLimits.MaxTextSetValuesPerRule + 1)
                .Select(index => $"synthetic_{index}")
                .ToArray()
        };
        var oversized = scenario with
        {
            Groups = [group with { Rules = [rule] }]
        };

        Assert.Contains(
            FilterScenarioValidator.Validate(oversized, Ready),
            error => error.Contains(
                FilterScenarioLimits.MaxTextSetValuesPerRule.ToString(),
                StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_TextSetValueLimitPlusOne_ThrowsInvalidScenario()
    {
        var json = JsonSerializer.Serialize(new
        {
            name = "synthetic",
            rationale = "bounded parser",
            groups = new[]
            {
                new
                {
                    rules = new[]
                    {
                        new
                        {
                            type = "textSet",
                            field = "description",
                            mode = "contains",
                            normalization = "preserve",
                            values = Enumerable.Range(0, FilterScenarioLimits.MaxTextSetValuesPerRule + 1)
                                .Select(index => $"synthetic_{index}")
                                .ToArray()
                        }
                    }
                }
            }
        });

        using var document = JsonDocument.Parse(json);
        var exception = Assert.Throws<JetActionException>(
            () => FilterScenarioPayloadParser.Parse(document.RootElement, moneyScale: 100));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
        Assert.Contains(
            FilterScenarioLimits.MaxTextSetValuesPerRule.ToString(),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("matchScope")]
    [InlineData("type")]
    [InlineData("mode")]
    [InlineData("normalization")]
    public void ParseAndValidate_PaddedClosedToken_FailsBeforePersistenceOrReadBack(string property)
    {
        const string canonical =
            """
            {"name":"synthetic","rationale":"closed tokens","groups":[{"matchScope":"sameVoucher","rules":[
              {"join":"AND","type":"textSet","field":"description","values":["anchor"],"mode":"exact","normalization":"preserve"},
              {"join":"AND","type":"textSet","field":"accName","values":["evidence"],"mode":"contains","normalization":"preserve"}
            ]}]}
            """;
        var padded = property switch
        {
            "matchScope" => canonical.Replace(
                "\"matchScope\":\"sameVoucher\"",
                "\"matchScope\":\" sameVoucher \"",
                StringComparison.Ordinal),
            "type" => canonical.Replace(
                "\"type\":\"textSet\"",
                "\"type\":\" textSet \"",
                StringComparison.Ordinal),
            "mode" => canonical.Replace(
                "\"mode\":\"exact\"",
                "\"mode\":\" exact \"",
                StringComparison.Ordinal),
            "normalization" => canonical.Replace(
                "\"normalization\":\"preserve\"",
                "\"normalization\":\" preserve \"",
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
            {"name":"synthetic","rationale":"legacy row","groups":[{"join":"OR","rules":[
              {"join":"AND","type":"text","field":"description","keywords":"alpha","mode":"contains"},
              {"join":"OR","type":"manualAuto","isManual":true}
            ]}]}
            """);

        var group = Assert.Single(scenario.Groups);
        Assert.Equal(FilterGroupMatchScope.Row, group.MatchScope);
        Assert.Equal(FilterJoin.Or, group.Rules[1].Join);
        Assert.Empty(FilterScenarioValidator.Validate(scenario, Ready));
    }

    [Fact]
    public void Renderer_TextSetAndSameVoucher_UsesBackendAuthorityPhrases()
    {
        using var document = JsonDocument.Parse(
            """
            {"groups":[{"matchScope":"sameVoucher","rules":[
              {"type":"textSet","field":"description","values":["anchor"],"mode":"contains","normalization":"preserve"},
              {"type":"textSet","field":"accName","values":["evidence"],"mode":"exact","normalization":"removeAsciiSpaces"}
            ]}]}
            """);

        var rendered = FilterConditionRenderer.Render(document.RootElement);

        Assert.Contains("主要條件（決定符合條件的分錄）", rendered, StringComparison.Ordinal);
        Assert.Contains("後續條件可由同一傳票的其他分錄列符合", rendered, StringComparison.Ordinal);
        Assert.Contains("包含任一值", rendered, StringComparison.Ordinal);
        Assert.Contains("完全符合任一值", rendered, StringComparison.Ordinal);
        Assert.Contains("保留 ASCII 空白", rendered, StringComparison.Ordinal);
        Assert.Contains("移除 ASCII 空白", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("filter-2026-08-14-v7")]
    [InlineData("filter-2026-08-14-v8")]
    [InlineData("filter-2026-08-14-v9")]
    [InlineData("filter-2026-09-08-v13")]
    [InlineData("filter-2026-09-17-v14")]
    [InlineData("filter-2026-09-18-v15")]
    [InlineData("filter-2026-09-18-v16")]
    public void FilterLogicVersion_IsV17AndV16OrEarlierDefinitionsAreStale(string savedVersion)
    {
        // 2026-10-04 R1、R2 改變空白傳票號碼的命中結果，現行版本推進為 v17。
        // v16 與更早保存的情境不是目前版本，不得沿用舊 resultRef 或直接惰性補算。
        // 2026-10-02 起開案時若整批仍符合目前規則，會由 FilterScenarioRuleUpgrade 改成目前版本後再重算；
        // 不符合時整批維持舊版本，查詢與匯出仍會擋下。
        Assert.Equal("filter-2026-10-04-v17", RuleLogicVersions.Filter);
        var saved = new SavedFilterScenario(
            1,
            "synthetic",
            "stale replay",
            $$"""
            {"populationScope":"auditPeriod","logicVersion":"{{savedVersion}}","groups":[]}
            """,
            DateTimeOffset.UtcNow);

        Assert.False(RuleLogicVersions.IsCurrent(saved));
    }

    /* ---- 借貸組合雙側多選的 wire 形狀 ------------------------------------- */

    [Fact]
    public void Parse_PairCategoryIdArrays_AreAuthoritativeAndSuppressLegacyScalars()
    {
        var scenario = Parse(
            """
            {"name":"synthetic","rationale":"contract","groups":[{"join":"AND","rules":[
              {"join":"AND","type":"accountPair","pairMode":"exact",
               "debitCategory":"Cash","creditCategory":"Others",
               "debitCategoryIds":["builtin.receivables","builtin.cash"],
               "creditCategoryIds":["builtin.revenue"]}
            ]}]}
            """);

        var rule = scenario.Groups[0].Rules[0];
        Assert.Equal(["builtin.receivables", "builtin.cash"], rule.DebitCategoryIds);
        Assert.Equal(["builtin.revenue"], rule.CreditCategoryIds);
        // 2026-10-02 起規則不再有單選分類欄位，原本「scalar 為 null」的兩行斷言隨欄位刪除；
        // 單選欄位只是未知欄位，判定只看陣列。
        Assert.Equal(["builtin.cash", "builtin.receivables"], rule.EffectiveDebitCategoryIds);
    }

    [Fact]
    public void Parse_PairWithoutArrays_IgnoresSingleCategoryFields()
    {
        var scenario = Parse(
            """
            {"name":"synthetic","rationale":"contract","groups":[{"join":"AND","rules":[
              {"join":"AND","type":"specialAccountCategoryPair","pairMode":"drAndCr",
               "debitCategory":"Revenue","creditCategory":"Cash"}
            ]}]}
            """);

        // 只認分類身分陣列；單選欄位照未知欄位處理，不再換算成內建分類。
        var rule = scenario.Groups[0].Rules[0];
        Assert.Empty(rule.EffectiveDebitCategoryIds);
        Assert.Empty(rule.EffectiveCreditCategoryIds);
    }

    [Fact]
    public void Parse_ExplicitEmptyPairArray_DoesNotFallBackToScalar()
    {
        var scenario = Parse(
            """
            {"name":"synthetic","rationale":"contract","groups":[{"join":"AND","rules":[
              {"join":"AND","type":"accountPair","pairMode":"exact",
               "debitCategory":"Cash","debitCategoryIds":[],
               "creditCategoryIds":["builtin.revenue"]}
            ]}]}
            """);

        var rule = scenario.Groups[0].Rules[0];
        Assert.Empty(rule.EffectiveDebitCategoryIds);
        Assert.Contains(
            FilterScenarioValidator.Validate(scenario, Ready with
            {
                HasAccountMapping = true,
                HasAnyAccountCategory = true
            }),
            error => error.Contains("借方分類至少需選擇一項", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"join":"AND","type":"accountPair","pairMode":"exact","debitCategoryIds":"builtin.cash"}""")]
    [InlineData("""{"join":"AND","type":"accountPair","pairMode":"exact","creditCategoryIds":[1]}""")]
    public void Parse_MalformedPairArray_ThrowsInvalidScenario(string ruleJson)
    {
        var json = $$"""{"name":"synthetic","rationale":"contract","groups":[{"join":"AND","rules":[{{ruleJson}}]}]}""";
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
            {"name":"synthetic","rationale":"contract","groups":[{"join":"AND","rules":[
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
