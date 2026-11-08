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

    // 已儲存情境的「同一傳票」比對範圍已拿掉（裁定 2026-10-30-25：已儲存情境的舊寫法不再讀回），讀到時當成無法讀取的內容。
    // 這則只留文字值清單的形狀檢查，條件組改用同一分錄列。第一次失敗：Build 收據 20261030-080858642-d5c1719481744042bbfc70329c5fcceb（比對範圍的型別已不存在，無法編譯）。
    [Fact]
    public void ParseAndValidate_TextSet_PreservesClosedWireShape()
    {
        var scenario = Parse(
            """
            {"name":"synthetic","rationale":"contract","groups":[{"join":"AND","matchScope":"row","conditions":[
              {"join":"AND","type":"textSet","field":"description","values":["anchor"],"mode":"exact","normalization":"preserve"},
              {"join":"AND","type":"textSet","field":"accountName","values":["evi dence"],"mode":"contains","normalization":"removeAsciiSpaces"}
            ]}]}
            """);

        var group = Assert.Single(scenario.Groups);
        Assert.Null(group.UnknownMatchScope);
        Assert.Equal(FilterConditionType.TextSet, group.Conditions[0].Type);
        Assert.Equal(["anchor"], group.Conditions[0].Values);
        Assert.Equal(TextSetNormalization.Preserve, group.Conditions[0].Normalization);
        Assert.Equal(TextSetNormalization.RemoveAsciiSpaces, group.Conditions[1].Normalization);
        Assert.Empty(FilterScenarioValidator.Validate(scenario, Ready));
    }

    // 原本第四個參數是訊息片段（matchScope、至少需要兩條、只允許 AND）。這三種錯誤只會出自存檔損毀，審計員在畫面上無從修正，
    // 損毀的情境在畫面改寫通用句（裁定 2026-10-13-21）；參數改成程式寫死的原因代號，仍逐項區分。第一次失敗：收據 20261013-141745059-73ff4afda2d1464bbfbe918459a13666。
    [Theory]
    [InlineData("mystery", 2, "AND", "group_match_scope_invalid")]
    // 已儲存情境的「同一傳票」比對範圍已拿掉（裁定 2026-10-30-25：已儲存情境的舊寫法不再讀回），讀到時當成無法讀取的內容。
    // 原本 sameVoucher 的兩列檢查的是它自己的條數與連接詞，現在改成和其他未知值同一個原因。第一次失敗：Focused 收據 20261030-081546753-58fab64421d94d9a9973dfa09cda1b98（舊的比對範圍改成無法讀取）。
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
                join = "AND", type = "textSet", field = "description",
                values = new[] { "anchor" }, mode = "contains", normalization = "preserve"
            }
        };
        if (ruleCount > 1)
        {
            rules.Add(new
            {
                join = secondJoin, type = "textSet", field = "accountName",
                values = new[] { "evidence" }, mode = "exact", normalization = "preserve"
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

    // 原本第四個參數是訊息片段。這三種錯誤只會出自存檔損毀，審計員在畫面上無從修正，損毀的情境在畫面改寫通用句（裁定 2026-10-13-21）；
    // 參數改成程式寫死的原因代號，仍逐項區分。第一次失敗：收據 20261013-141745059-73ff4afda2d1464bbfbe918459a13666。
    [Theory]
    [InlineData("notContains", "preserve", "value", "text_set_mode_invalid")]
    [InlineData("contains", "unknown", "value", "text_set_normalization_invalid")]
    [InlineData("contains", "removeAsciiSpaces", "   ", "text_set_value_blank")]
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
                    conditions = new[]
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

        var errors = FilterScenarioValidator.Validate(scenario, Ready);
        Assert.Contains("第 1 組第 1 條：此條件內容有誤，無法儲存。請刪除此條件後重新設定。", errors);
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
            {"name":"synthetic","rationale":"bounded values","groups":[{"conditions":[
              {"type":"textSet","field":"description","mode":"contains","normalization":"preserve","values":["seed"]}
            ]}]}
            """);
        var group = Assert.Single(scenario.Groups);
        var rule = Assert.Single(group.Conditions) with
        {
            Values = Enumerable.Range(0, FilterScenarioLimits.MaxTextSetValuesPerRule + 1)
                .Select(index => $"synthetic_{index}")
                .ToArray()
        };
        var oversized = scenario with
        {
            Groups = [group with { Conditions = [rule] }]
        };

        // 損毀的情境在畫面只寫通用句（裁定 2026-10-13-21）；這類錯誤只會出自程式送錯資料或存檔損毀，審計員在畫面上無從修正，所以原句與英文代碼改記支援日誌；
        // 改鎖通用句與程式寫死的原因代號，檢查仍逐項區分。第一次失敗：收據 20261013-141745059-73ff4afda2d1464bbfbe918459a13666。
        var errors = FilterScenarioValidator.Validate(oversized, Ready);
        Assert.Contains("第 1 組第 1 條：此條件內容有誤，無法儲存。請刪除此條件後重新設定。", errors);
        Assert.Contains(FilterScenarioCorruption.ReasonsOf(errors), reason => reason.Code == "text_set_values_too_many");
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
                    conditions = new[]
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
        // 損毀的情境在畫面只寫通用句（裁定 2026-10-13-21）；這類錯誤只會出自程式送錯資料或存檔損毀，審計員在畫面上無從修正，所以原句與英文代碼改記支援日誌；
        // 改鎖通用句與程式寫死的原因代號，檢查仍逐項區分。第一次失敗：收據 20261013-141745059-73ff4afda2d1464bbfbe918459a13666。
        Assert.Equal("第 1 組第 1 條：此條件內容有誤，無法儲存。請刪除此條件後重新設定。", exception.Message);
        Assert.Contains(new FilterScenarioCorruptReason(1, 1, "text_set_values_too_many"), FilterScenarioCorruption.ReasonsOf(exception));
    }

    [Theory]
    [InlineData("matchScope")]
    [InlineData("type")]
    [InlineData("mode")]
    [InlineData("normalization")]
    public void ParseAndValidate_PaddedClosedToken_FailsBeforePersistenceOrReadBack(string property)
    {
        // 「同一傳票」比對範圍已拿掉（裁定 2026-10-30-25），合格的原文改用 row，前後加空白的那一份仍要被擋下。
        // 這次改的是測試資料，不是因為測試失敗。
        const string canonical =
            """
            {"name":"synthetic","rationale":"closed tokens","groups":[{"matchScope":"row","conditions":[
              {"join":"AND","type":"textSet","field":"description","values":["anchor"],"mode":"exact","normalization":"preserve"},
              {"join":"AND","type":"textSet","field":"accountName","values":["evidence"],"mode":"contains","normalization":"preserve"}
            ]}]}
            """;
        var padded = property switch
        {
            "matchScope" => canonical.Replace(
                "\"matchScope\":\"row\"",
                "\"matchScope\":\" row \"",
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
            {"name":"synthetic","rationale":"legacy row","groups":[{"join":"OR","conditions":[
              {"join":"AND","type":"text","field":"description","keywords":"alpha","mode":"contains"},
              {"join":"OR","type":"manualAuto","isManual":true}
            ]}]}
            """);

        var group = Assert.Single(scenario.Groups);
        // 比對範圍只剩同一分錄列，型別已拿掉，改看沒有未知值。第一次失敗：Build 收據 20261030-080858642-d5c1719481744042bbfc70329c5fcceb（比對範圍的型別已不存在，無法編譯）。
        Assert.Null(group.UnknownMatchScope);
        Assert.Equal(FilterJoin.Or, group.Conditions[1].Join);
        Assert.Empty(FilterScenarioValidator.Validate(scenario, Ready));
    }

    // 已儲存情境的「同一傳票」比對範圍已拿掉（裁定 2026-10-30-25：已儲存情境的舊寫法不再讀回），讀到時當成無法讀取的內容。
    // 讀回文字也不再寫「主要條件」與「同一傳票」的說明，這則只留文字值清單的片語。第一次失敗：Focused 收據 20261030-081546753-58fab64421d94d9a9973dfa09cda1b98（舊的比對範圍改成無法讀取）。
    [Fact]
    public void Renderer_TextSet_UsesBackendAuthorityPhrases()
    {
        using var document = JsonDocument.Parse(
            """
            {"groups":[{"matchScope":"row","conditions":[
              {"type":"textSet","field":"description","values":["anchor"],"mode":"contains","normalization":"preserve"},
              {"type":"textSet","field":"accountName","values":["evidence"],"mode":"exact","normalization":"removeAsciiSpaces"}
            ]}]}
            """);

        var rendered = FilterConditionRenderer.Render(document.RootElement);

        Assert.Contains("包含任一值", rendered, StringComparison.Ordinal);
        Assert.Contains("完全符合任一值", rendered, StringComparison.Ordinal);
        // 這句的用字依裁定 2026-10-14-01（第一次失敗：收據 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e）。
        Assert.Contains("保留半形空白", rendered, StringComparison.Ordinal);
        Assert.Contains("移除半形空白", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("filter-2026-08-14-v7")]
    [InlineData("filter-2026-08-14-v8")]
    [InlineData("filter-2026-08-14-v9")]
    [InlineData("filter-2026-09-08-v13")]
    [InlineData("filter-2026-09-17-v14")]
    [InlineData("filter-2026-09-18-v15")]
    [InlineData("filter-2026-09-18-v16")]
    [InlineData("filter-2026-10-04-v17")]
    [InlineData("filter-2026-10-06-v18")]
    [InlineData("filter-2026-10-08-v19")]
    public void FilterLogicVersion_IsV20AndV19OrEarlierDefinitionsAreStale(string savedVersion)
    {
        // 裁定 2026-10-08-03 移除傳票日期與回溯過帳，現行版本推進為 v19；第一次失敗收據 20261008-042049371-eba08c280cca4ff59170a6e88e87291a。
        // 2026-10-06 條件 A 納入查核期末視窗，現行版本推進為 v18。
        // v17 與更早保存的情境不是目前版本，不得沿用舊 resultRef 或直接惰性補算。
        // 2026-10-02 起開案時若整批仍符合目前規則，會由 FilterScenarioRuleUpgrade 改成目前版本後再重算；
        // 不符合時整批維持舊版本，查詢與匯出仍會擋下。
        // First failure: 20261014-135805134-9060396119bc4e8f8dae2eecd29f1776. Mixed-purpose conditions are retired.
        Assert.Equal("filter-2026-10-14-v20", RuleLogicVersions.Filter);
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
        // 2026-10-02 起規則不再有單選分類欄位，原本「scalar 為 null」的兩行斷言隨欄位刪除；
        // 單選欄位只是未知欄位，判定只看陣列。
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

        // 只認分類身分陣列；單選欄位照未知欄位處理，不再換算成內建分類。
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
            // 改鎖裁定 2026-10-13-28 的整句。第一次失敗：收據 20261013-141745059-73ff4afda2d1464bbfbe918459a13666。
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
