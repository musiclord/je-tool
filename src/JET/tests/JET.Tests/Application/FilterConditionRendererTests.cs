using System.Text.Json;
using JET.Application;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 條件 AST → 中文布林式渲染器（<see cref="FilterConditionRenderer"/>）。
///
/// oracle 策略（規格 oracle）：預期值以前端 <c>filter-step.js</c> 的 <c>readBackHtml</c> ＋
/// <c>ruleSummaryLabel(rule, 0)</c> 為權威，逐一手算成字面斷言——渲染器必須與前端藍色 read-back
/// 的 <c>.scenario-readback__expr</c> 文字內容同構。標籤（回溯過帳/週末過帳…）來自 Domain
/// FilterConditionLabels（前端鏡像，另有 FilterConditionLabelMirrorTests 守衛），此處不重測標籤內容，
/// 只鎖布林式的結構：組內組合器、組間運算子、內層括號、非營業日排最後與其括號消歧、空組過濾。
///
/// 設計技術：決策表（Decision Table）——以「可編輯組數 × 組內規則數 × 有無非營業日」為條件維度，
/// 每列一個代表情境。join 一律用大寫 AND/OR（對齊前端 comboSegment 值與 read-back 的大小寫敏感比對）。
/// </summary>
public sealed class FilterConditionRendererTests
{
    [Fact]
    public void ValueConditions_ReadNaturallyWithoutParenthesizedAnnotations()
    {
        using var document = JsonDocument.Parse("""
            {"groups":[{"rules":[
              {"type":"fieldValue","field":"postDate","operator":"dayOfMonthIn","values":["28","31"]},
              {"join":"AND","type":"fieldValue","field":"amount","operator":"between","from":"10","to":"20","amountBasis":"absolute"},
              {"join":"AND","type":"drCrOnly","drCr":"debit"},
              {"join":"AND","type":"fieldValue","field":"description","operator":"contains","values":["合成"]}
            ]}]}
            """);
        var text = FilterConditionRenderer.Render(document.RootElement);
        Assert.Contains("每月幾日屬於「28、31」", text, StringComparison.Ordinal);
        Assert.Contains("金額絕對值 介於「10」至「20」", text, StringComparison.Ordinal);
        Assert.Contains("且 僅借方 且", text, StringComparison.Ordinal);
        Assert.Contains("空白不列入", text, StringComparison.Ordinal);
        Assert.DoesNotContain("含兩端", text, StringComparison.Ordinal);
        Assert.DoesNotContain("(且)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("（金額", text, StringComparison.Ordinal);
    }

    private static string Render(object scenario)
    {
        var json = JsonSerializer.Serialize(scenario);
        using var document = JsonDocument.Parse(json);
        return FilterConditionRenderer.Render(document.RootElement);
    }

    // ---- 單組（ne=1）----

    [Fact]
    public void SingleGroup_SinglePrescreen_RendersAtomWithoutOperators()
    {
        // 回溯過帳前端接上後的代表原子；單組單規則＝無組合器、無括號。
        var result = Render(new
        {
            groups = new[] { new { join = "AND", rules = new object[]
            {
                new { join = "AND", type = "prescreen", prescreenKey = "backdatedPosting" }
            } } }
        });

        Assert.Equal("預篩選：回溯過帳", result);
    }

    [Fact]
    public void SingleGroup_TwoRulesOrCombinator_JoinsWithOr()
    {
        // 組內任一規則 join=OR → 整組 OR（groupCombinator）；單組不加外層括號。
        var result = Render(new
        {
            groups = new[] { new { join = "AND", rules = new object[]
            {
                new { join = "AND", type = "prescreen", prescreenKey = "suspiciousKeywords" },
                new { join = "OR", type = "prescreen", prescreenKey = "holidayPosting" }
            } } }
        });

        Assert.Equal("預篩選：摘要特定描述 或 預篩選：假日過帳", result);
    }

    [Fact]
    public void SingleGroup_MixedEffectiveRuleJoins_RendersExactLeftFold()
    {
        var result = Render(new
        {
            groups = new[] { new { join = "AND", rules = new object[]
            {
                new { join = "AND", type = "prescreen", prescreenKey = "suspiciousKeywords" },
                new { join = "OR", type = "prescreen", prescreenKey = "holidayPosting" },
                new { join = "AND", type = "drCrOnly", drCr = "debit" },
                new { join = "OR", type = "manualAuto", isManual = "true" }
            } } }
        });

        Assert.Equal(
            "（（（預篩選：摘要特定描述 或 預篩選：假日過帳） 且 僅借方） 或 人工分錄）",
            result);
    }

    [Theory]
    [InlineData("AND", "預篩選：摘要特定描述 且 預篩選：假日過帳 且 僅借方")]
    [InlineData("OR", "預篩選：摘要特定描述 或 預篩選：假日過帳 或 僅借方")]
    public void SingleGroup_UniformRuleJoins_PreservesExistingFlatOutput(
        string join,
        string expected)
    {
        var result = Render(new
        {
            groups = new[] { new { join = "AND", rules = new object[]
            {
                new { join, type = "prescreen", prescreenKey = "suspiciousKeywords" },
                new { join, type = "prescreen", prescreenKey = "holidayPosting" },
                new { join, type = "drCrOnly", drCr = "debit" }
            } } }
        });

        Assert.Equal(expected, result);
    }

    [Fact]
    public void SingleGroup_AllLowercaseOrJoins_UsesActualOrMeaning()
    {
        var result = Render(new
        {
            groups = new[] { new { join = "and", rules = new object[]
            {
                new { join = "or", type = "prescreen", prescreenKey = "suspiciousKeywords" },
                new { join = "or", type = "prescreen", prescreenKey = "holidayPosting" },
                new { join = "or", type = "drCrOnly", drCr = "debit" }
            } } }
        });

        Assert.Equal(
            "預篩選：摘要特定描述 或 預篩選：假日過帳 或 僅借方",
            result);
    }

    [Fact]
    public void SingleGroup_MixedCaseEffectiveJoins_RendersSemanticLeftFold()
    {
        var result = Render(new
        {
            groups = new[] { new { join = "AND", rules = new object[]
            {
                new { join = "or", type = "prescreen", prescreenKey = "suspiciousKeywords" },
                new { join = " or ", type = "prescreen", prescreenKey = "holidayPosting" },
                new { join = "AND", type = "drCrOnly", drCr = "debit" }
            } } }
        });

        Assert.Equal(
            "（（預篩選：摘要特定描述 或 預篩選：假日過帳） 且 僅借方）",
            result);
    }

    [Fact]
    public void SingleGroup_FirstRuleJoinDoesNotCreateMixedEffectiveEdges()
    {
        var result = Render(new
        {
            groups = new[] { new { join = "AND", rules = new object[]
            {
                new { join = "OR", type = "prescreen", prescreenKey = "suspiciousKeywords" },
                new { join = "AND", type = "prescreen", prescreenKey = "holidayPosting" },
                new { join = "AND", type = "drCrOnly", drCr = "debit" }
            } } }
        });

        Assert.Equal(
            "預篩選：摘要特定描述 且 預篩選：假日過帳 且 僅借方",
            result);
    }

    [Fact]
    public void SingleGroup_TextAndNumRangeBothBounds_RendersDisplayStrings()
    {
        // text 原子（欄+模式+關鍵字原文）與 numRange 雙邊界（≥…、≤…）；組內 AND。
        var result = Render(new
        {
            groups = new[] { new { join = "AND", rules = new object[]
            {
                new { join = "AND", type = "text", field = "description", mode = "contains", keywords = "調整" },
                new { join = "AND", type = "numRange", field = "amount", from = "1000", to = "5000" }
            } } }
        });

        Assert.Equal("傳票摘要 包含「調整」 且 金額（絕對值） ≥ 1000、≤ 5000", result);
    }

    [Fact]
    public void SingleGroup_DateRangeOneSided_UsesEllipsisForMissingBound()
    {
        // dateRange 缺 to → 以刪節號替代（前端 rule.to || '…'）。
        var result = Render(new
        {
            groups = new[] { new { join = "AND", rules = new object[]
            {
                new { join = "AND", type = "dateRange", field = "postDate", from = "2024-01-01" }
            } } }
        });

        Assert.Equal("總帳日期 2024-01-01～…", result);
    }

    // ---- 多組（ne≥2）：組間運算子 + 內層括號 ----

    [Fact]
    public void TwoGroups_ScenarioOr_SingleAtomGroups_NoInnerParens()
    {
        // 兩組各單原子 → 內層不包括號；組間運算子取第二可編輯組 join=OR。
        var result = Render(new
        {
            groups = new object[]
            {
                new { join = "OR", rules = new object[]
                {
                    new { join = "AND", type = "text", field = "description", mode = "contains", keywords = "調整" }
                } },
                new { join = "OR", rules = new object[]
                {
                    new { join = "AND", type = "drCrOnly", drCr = "debit" }
                } }
            }
        });

        Assert.Equal("傳票摘要 包含「調整」 或 僅借方", result);
    }

    [Fact]
    public void TwoGroups_ScenarioAnd_MultiAtomGroupWrappedInParens()
    {
        // 組間 AND（editable[1].join=AND）；含 ≥2 原子的組要包內層括號，單原子組不包。
        var result = Render(new
        {
            groups = new object[]
            {
                new { join = "AND", rules = new object[]
                {
                    new { join = "AND", type = "text", field = "description", mode = "contains", keywords = "調整" },
                    new { join = "AND", type = "numRange", field = "amount", from = "1000000" }
                } },
                new { join = "AND", rules = new object[]
                {
                    new { join = "AND", type = "drCrOnly", drCr = "debit" }
                } }
            }
        });

        Assert.Equal("（傳票摘要 包含「調整」 且 金額（絕對值） ≥ 1000000） 且 僅借方", result);
    }

    // ---- 非營業日(I)：偵測、排最後、括號消歧、空組過濾 ----

    [Fact]
    public void PresetOnly_WithLeadingEmptyGroup_RendersNonBusinessDayAtom()
    {
        // 空可編輯組被過濾（不留懸空運算子）；只剩非營業日預設 → 單一原子。
        var result = Render(new
        {
            groups = new object[]
            {
                new { join = "AND", rules = new object[0] },
                new { join = "AND", rules = new object[]
                {
                    new { join = "OR", type = "prescreen", prescreenKey = "weekendPosting" },
                    new { join = "OR", type = "prescreen", prescreenKey = "holidayPosting" }
                } }
            }
        });

        Assert.Equal("非營業日（週末或假日）", result);
    }

    [Fact]
    public void SingleAtomGroup_PlusPreset_NoDisambiguatingParens()
    {
        // 單組單規則接 I：不需括號消歧（needsParens=false）。
        var result = Render(new
        {
            groups = new object[]
            {
                new { join = "AND", rules = new object[]
                {
                    new { join = "AND", type = "drCrOnly", drCr = "debit" }
                } },
                new { join = "AND", rules = new object[]
                {
                    new { join = "OR", type = "prescreen", prescreenKey = "weekendPosting" },
                    new { join = "OR", type = "prescreen", prescreenKey = "holidayPosting" }
                } }
            }
        });

        Assert.Equal("僅借方 且 非營業日（週末或假日）", result);
    }

    [Fact]
    public void MultiRuleSingleGroup_PlusPreset_WrapsInDisambiguatingParens()
    {
        // 單組含 ≥2 規則接 I：補括號消歧——「a 或 b 且 非營業日」慣例讀作 a 或 (b 且 I)，
        // 實際語意 (a 或 b) 且 I，故整式包括號（2026-07-06 驗收行為）。
        var result = Render(new
        {
            groups = new object[]
            {
                new { join = "AND", rules = new object[]
                {
                    new { join = "AND", type = "text", field = "description", mode = "contains", keywords = "調整" },
                    new { join = "OR", type = "drCrOnly", drCr = "debit" }
                } },
                new { join = "AND", rules = new object[]
                {
                    new { join = "OR", type = "prescreen", prescreenKey = "weekendPosting" },
                    new { join = "OR", type = "prescreen", prescreenKey = "holidayPosting" }
                } }
            }
        });

        Assert.Equal("（傳票摘要 包含「調整」 或 僅借方） 且 非營業日（週末或假日）", result);
    }

    [Fact]
    public void EmptyScenario_RendersEmptyString()
    {
        // 無群組 → 空字串（無條件可述；step3 該列條件欄留空）。
        Assert.Equal(string.Empty, Render(new { groups = new object[0] }));
    }

    [Theory]
    [InlineData("exact", "借貸科目組合：借方是 A 且貸方是 B（借方 Receivables・貸方 Revenue）")]
    [InlineData("debitAnchor", "借貸科目組合：借方是 A，看它的對方科目（借方 Receivables）")]
    [InlineData("creditAnchor", "借貸科目組合：貸方是 B，看它的對方科目（貸方 Revenue）")]
    public void AccountPair_RenderingOnlyNamesCategoriesThatParticipate(
        string pairMode,
        string expected)
    {
        var result = Render(new
        {
            groups = new[] { new { join = "AND", rules = new[]
            {
                new
                {
                    join = "AND",
                    type = "accountPair",
                    pairMode,
                    debitCategory = "Receivables",
                    creditCategory = "Revenue"
                }
            } } }
        });

        Assert.Equal(expected, result);
    }

    // ---- 手動同形組 vs 非營業日預設：偵測收緊（同構錨點查證 2026-07-08）----
    // 錨點：F 欄同構對象是「保存當下的藍色 read-back」——唯一渲染 readBackHtml 的是帶 __kctPresetGroup
    // 標記的草稿（filter-step.js:756/1275）；已存情境重載後前端只渲染 pill 摘要（scenarioPillsHtml，
    // filter-step.js:1050），無任何預設偵測（filter-step.js:342 明注 wire 剝標後無從分辨）。因此渲染器以
    // KCT 預設在 wire 上的唯一簽章重建保存當下的 read-back：group join=="AND"（addKctToDraft 固定、
    // toWireDraft 不動預設組）且組合器 OR（兩規則 join 皆 'OR'）且鍵集恰為 {weekendPosting, holidayPosting}。
    // 手動建的同形組在 wire 上可分：單組情境經 toWireDraft 收斂為 join=="OR"（scenarioJoin 預設）、
    // 或 情境收斂為 "OR"——皆按普通組渲染（同構於保存當下 read-back 對可編輯組的渲染）。

    [Fact]
    public void ManualAndCombinator_SameShapeGroup_RendersAsNormalGroup()
    {
        // 使用者從自訂面板手動加兩條 prescreen（週末+假日）、組合器 AND：語意是「週末 且 假日」，
        // 絕不可誤標為「非營業日（週末或假日）」（語意相反）。保存當下 read-back 顯示的就是 且 式。
        var result = Render(new
        {
            groups = new[] { new { join = "OR", rules = new object[]
            {
                new { join = "AND", type = "prescreen", prescreenKey = "weekendPosting" },
                new { join = "AND", type = "prescreen", prescreenKey = "holidayPosting" }
            } } }
        });

        Assert.Equal("預篩選：週末過帳 且 預篩選：假日過帳", result);
    }

    [Fact]
    public void ManualOrCombinator_SingleSameShapeGroup_RendersAsNormalGroup()
    {
        // 手動 或 同形組（單組情境）：wire 上 group join 經 toWireDraft 收斂為 "OR"（scenarioJoin 預設），
        // 與 KCT 預設的固定 "AND" 可分。保存當下 read-back 把它當一般可編輯組渲染
        // （「預篩選：週末過帳 或 預篩選：假日過帳」，無預設區塊）——渲染器同構照渲。
        var result = Render(new
        {
            groups = new[] { new { join = "OR", rules = new object[]
            {
                new { join = "OR", type = "prescreen", prescreenKey = "weekendPosting" },
                new { join = "OR", type = "prescreen", prescreenKey = "holidayPosting" }
            } } }
        });

        Assert.Equal("預篩選：週末過帳 或 預篩選：假日過帳", result);
    }

    // ---- 原子全型別覆蓋（決策表補齊：其餘 13 種 atom 型別 + 變體）----
    // oracle：前端 ruleSummaryLabel(rule, 0) 手算（等價分割：每型別一代表值；
    // manualAuto 補一負向變體、revenueDebitNearQuarterEnd 補缺值刪節號變體）。

    [Theory]
    [InlineData("""{"join":"AND","type":"manualAuto","isManual":"true"}""", "人工分錄")]
    [InlineData("""{"join":"AND","type":"manualAuto","isManual":"false"}""", "自動分錄")]
    [InlineData("""{"join":"AND","type":"accountPair","pairMode":"exact","debitCategory":"Receivables","creditCategory":"Revenue"}""",
        "借貸科目組合：借方是 A 且貸方是 B（借方 Receivables・貸方 Revenue）")]
    [InlineData("""{"join":"AND","type":"specialAccountCategoryPair","pairMode":"drNotCr","debitCategory":"Receivables","creditCategory":"Revenue"}""",
        "借貸科目組合：借方是 A 且整張傳票沒有 B 貸方（借方 Receivables・貸方 Revenue）")]
    [InlineData("""{"join":"AND","type":"customKeywords","keywords":"迴轉,調整"}""", "自訂關鍵字「迴轉,調整」")]
    [InlineData("""{"join":"AND","type":"customTrailingZeros","digits":"6"}""", "尾數連續 6 個 0")]
    [InlineData("""{"join":"AND","type":"customPreparerEntryCount","maxEntries":"11"}""", "所選母體內編製人員張數 ≤ 11")]
    [InlineData("""{"join":"AND","type":"customAccountEntryCount","maxEntries":"11"}""", "所選母體內科目張數 ≤ 11")]
    [InlineData("""{"join":"AND","type":"revenueDebitNearQuarterEnd","windowDays":"5"}""", "季末前 5 天借記收入")]
    [InlineData("""{"join":"AND","type":"revenueDebitNearQuarterEnd","windowDays":""}""", "季末前 … 天借記收入")]
    [InlineData("""{"join":"AND","type":"revenueWithoutNormalCounterpart"}""", "貸收入・借方非應收/預收")]
    [InlineData("""{"join":"AND","type":"manualRevenueEntry"}""", "收入之人工分錄")]
    [InlineData("""{"join":"AND","type":"trailingDigits","keywords":"999999"}""", "主單位整數(捨小數)末 6 位 = 999999")]
    [InlineData("""{"join":"AND","type":"trailingDigits","keywords":"000000,99"}""", "主單位整數(捨小數)末 6 位 = 000000 或 末 2 位 = 99")]
    [InlineData("""{"join":"AND","type":"preparerEqualsApprover"}""", "編製＝核准同一人")]
    public void RuleAtom_AllTypes_MatchFrontendRuleSummaryLabel(string ruleJson, string expectedAtom)
    {
        var json = $$"""{"groups":[{"join":"AND","rules":[{{ruleJson}}]}]}""";
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expectedAtom, FilterConditionRenderer.Render(document.RootElement));
    }

    /* ---- 借貸組合多選讀回 ------------------------------------------------- */

    // 多選讀回把每個分類身分還原成 taxonomy 顯示 label，依使用者選取順序、以 Domain 正本
    // 的分隔字元串接；帶陣列時 scalar 不參與呈現（與判定端一致）。
    [Theory]
    [InlineData(
        """{"join":"AND","type":"accountPair","pairMode":"exact","debitCategoryIds":["builtin.receivables","builtin.cash"],"creditCategoryIds":["builtin.revenue"]}""",
        "借貸科目組合：借方是 A 且貸方是 B（借方 Receivables、Cash・貸方 Revenue）")]
    [InlineData(
        """{"join":"AND","type":"accountPair","pairMode":"debitAnchor","debitCategoryIds":["builtin.cash"],"creditCategoryIds":["builtin.revenue"]}""",
        "借貸科目組合：借方是 A，看它的對方科目（借方 Cash）")]
    [InlineData(
        """{"join":"AND","type":"specialAccountCategoryPair","pairMode":"drNotCr","debitCategoryIds":["builtin.receivables","builtin.cash"],"creditCategoryIds":["builtin.revenue"]}""",
        "借貸科目組合：借方是 A 且整張傳票沒有 B 貸方（借方 Receivables、Cash・貸方 Revenue）")]
    [InlineData(
        """{"join":"AND","type":"accountPair","pairMode":"exact","debitCategory":"Cash","creditCategory":"Others","debitCategoryIds":["builtin.receivables"],"creditCategoryIds":["builtin.revenue"]}""",
        "借貸科目組合：借方是 A 且貸方是 B（借方 Receivables・貸方 Revenue）")]
    public void PairAtom_CategoryIdArrays_RenderTaxonomyLabelsInSelectionOrder(
        string ruleJson,
        string expectedAtom)
    {
        var json = $$"""{"groups":[{"join":"AND","rules":[{{ruleJson}}]}]}""";
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expectedAtom, FilterConditionRenderer.Render(document.RootElement));
    }

    [Fact]
    public void PairAtom_UsesProjectTaxonomyLabels_IncludingRenamedBuiltInsAndCustomCategories()
    {
        const string customId = "custom.0123456789abcdef0123456789abcdef";
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["builtin.cash"] = "現金及約當現金",
            [customId] = "零用金"
        };
        var json = $$"""
            {"groups":[{"join":"AND","rules":[{"join":"AND","type":"accountPair","pairMode":"debitAnchor",
            "debitCategoryIds":["builtin.cash","{{customId}}"]}]}]}
            """;
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            "借貸科目組合：借方是 A，看它的對方科目（借方 現金及約當現金、零用金）",
            FilterConditionRenderer.Render(document.RootElement, labels));
    }

    // ---- typed 條件（type:"typed"，2026-08-14 凍結）----

    [Fact]
    public void TypedAtom_UsesCurrentRdeLabel_AndRendersRawOperandStrings()
    {
        const string fieldId = "rde.aaaa0000aaaa0000aaaa0000aaaa0000";
        var rdeLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [fieldId] = "稅務備註"
        };
        var json = $$"""
            {"groups":[{"join":"AND","rules":[
              {"join":"AND","type":"typed","fieldId":"{{fieldId}}","operator":"equals","value":"  Alpha "}
            ]}]}
            """;
        using var document = JsonDocument.Parse(json);

        // label 改名（type／identity 不變）時情境仍合法、renderer 用目前 metadata；
        // operand 顯示原始 wire 字串（含空白），不顯示比較用的正規化值。
        Assert.Equal(
            "稅務備註 等於「  Alpha 」",
            FilterConditionRenderer.Render(document.RootElement, null, rdeLabels));
    }

    [Fact]
    public void TypedAtom_UnknownField_FallsBackToFieldId()
    {
        const string fieldId = "rde.dddd0000dddd0000dddd0000dddd0000";
        var json = $$"""
            {"groups":[{"join":"AND","rules":[
              {"join":"AND","type":"typed","fieldId":"{{fieldId}}","operator":"isBlank"}
            ]}]}
            """;
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            fieldId + " 為空白",
            FilterConditionRenderer.Render(document.RootElement));
    }

    [Fact]
    public void TypedAtom_BetweenInValuesAndMoneyBasis_RenderPerCarrier()
    {
        const string dateField = "rde.bbbb0000bbbb0000bbbb0000bbbb0000";
        const string moneyField = "rde.cccc0000cccc0000cccc0000cccc0000";
        const string textField = "rde.aaaa0000aaaa0000aaaa0000aaaa0000";
        var rdeLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [dateField] = "審核日",
            [moneyField] = "稅額",
            [textField] = "備註"
        };
        var json = $$"""
            {"groups":[{"join":"AND","rules":[
              {"join":"AND","type":"typed","fieldId":"{{dateField}}","operator":"between","from":"2025-01-01","to":"2025-06-30"},
              {"join":"AND","type":"typed","fieldId":"{{moneyField}}","operator":"greaterThan","value":"100.50","amountBasis":"absolute"},
              {"join":"AND","type":"typed","fieldId":"{{textField}}","operator":"notIn","values":["A","B"]}
            ]}]}
            """;
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            "審核日 介於「2025-01-01」～「2025-06-30」 且 稅額 大於「100.50」（金額絕對值） 且 備註 不屬於任何值（排除）「A、B」",
            FilterConditionRenderer.Render(document.RootElement, null, rdeLabels));
    }
}
