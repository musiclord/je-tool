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
/// 只鎖布林式的結構：組內組合器、組間運算子、內層括號、非營業日括號的讀回、空組過濾。
///
/// 設計技術：決策表（Decision Table）——以「條件組數 × 組內規則數 × 有無非營業日」為條件維度，
/// 每列一個代表情境。join 一律用大寫 AND/OR（對齊前端 comboSegment 值與 read-back 的大小寫敏感比對）。
/// </summary>
public sealed class FilterConditionRendererTests
{
    // 2026-10-04 C4：省略 categorySelection 的舊條件仍比對相同分類用途，讀回現在明說這個範圍。
    // 十項固定預期只補此模式前綴；保留完整等值比較。第一次失敗收據：20261004-075513821-fc05ce3f66ae4f50bc2291ee57350ac9。
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

    [Fact]
    public void K1_ReadbackIncludesActualMergedPeriodEndWindows()
    {
        using var doc = JsonDocument.Parse("""{"groups":[{"rules":[{"type":"revenueDebitNearQuarterEnd","windowDays":5}]}]}""");
        Assert.Equal("季底或查核期末前 5 天借記收入；日期區間：2025-11-26～2025-11-30",
            FilterConditionRenderer.Render(doc.RootElement, null, periodStart: "2025-11-01", periodEnd: "2025-11-30"));
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

        Assert.Equal("總帳入帳日 2024-01-01～…", result);
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

    // ---- 舊版非營業日(I) 獨立條件組：照一般條件組讀回 ----
    // 2026-10-02 使用者裁定移除「情境層級」：I 改成組內的條件括號，讀回改在括號辨識「週末過帳 或 假日過帳」。
    // 下面三個測試原本斷言舊版獨立組會被讀成「非營業日（週末或假日）」並搬到最後
    //（原名 PresetOnly_WithLeadingEmptyGroup_RendersNonBusinessDayAtom、SingleAtomGroup_PlusPreset_NoDisambiguatingParens、
    // MultiRuleSingleGroup_PlusPreset_WrapsInDisambiguatingParens）。使用者裁定不轉換舊情境，舊形狀改照條件樹讀回，
    // 與重開後畫面顯示的兩條預篩選一致。第一次失敗：收據 20261003-031912333-532a31ca0cd1416d94dc1d3077d6f652。

    [Fact]
    public void OldPresetOnlyGroup_WithLeadingEmptyGroup_RendersOrdinaryOrGroup()
    {
        // 空組被過濾（不留懸空運算子）；只剩舊版非營業日組 → 一般「或」條件組。
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

        Assert.Equal("預篩選：週末過帳 或 預篩選：假日過帳", result);
    }

    [Fact]
    public void SingleAtomGroup_PlusOldPresetGroup_RendersAsSecondGroup()
    {
        // 單組單規則接舊版非營業日組：照兩組「且」讀回，多條件的第二組加括號。
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

        Assert.Equal("僅借方 且 （預篩選：週末過帳 或 預篩選：假日過帳）", result);
    }

    [Fact]
    public void MultiRuleSingleGroup_PlusOldPresetGroup_KeepsBothGroupsParenthesized()
    {
        // 單組含 ≥2 規則接舊版非營業日組：兩組各自加括號，語意 (a 或 b) 且 (週末 或 假日) 不會被誤讀。
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

        Assert.Equal("（傳票摘要 包含「調整」 或 僅借方） 且 （預篩選：週末過帳 或 預篩選：假日過帳）", result);
    }

    // ---- 非營業日(I) 條件括號：在原位置讀回「非營業日（週末或假日）」（2026-10-02 新增）----

    private static object NonBusinessDayBracket(string join = "AND", string secondJoin = "OR", bool holidayFirst = false) => new
    {
        join,
        type = "group",
        rules = new object[]
        {
            new { join = "AND", type = "prescreen", prescreenKey = holidayFirst ? "holidayPosting" : "weekendPosting" },
            new { join = secondJoin, type = "prescreen", prescreenKey = holidayFirst ? "weekendPosting" : "holidayPosting" }
        }
    };

    [Fact]
    public void NonBusinessDayBracket_InsideGroup_RendersAtomInPlace()
    {
        // KCT I 加入目前條件組，成為第 2 條：讀回留在原位置，不搬到最後、不另加「情境」字樣。
        var result = Render(new
        {
            groups = new[] { new { join = "AND", rules = new object[]
            {
                new { join = "AND", type = "prescreen", prescreenKey = "blankDescription" },
                NonBusinessDayBracket(),
                new { join = "AND", type = "drCrOnly", drCr = "debit" }
            } } }
        });

        // 2026-10-04 第 7 批 R5：舊 AST 保留週末或假日語意，不冒稱已排除補班日；位置與命中不變。
        // 第一次失敗：20261004-085118314-9c43f4a2a69c4f718bab4f9f3d6aaa11。
        Assert.Equal("預篩選：空白摘要 且 （週末過帳 或 假日過帳） 且 僅借方", result);
    }

    [Fact]
    public void NonBusinessDayBracket_FollowsGroupCombinatorAndGroupJoins()
    {
        // I 和同組其他條件的連接方式依該組設定；跨組仍照組間運算子。
        var result = Render(new
        {
            groups = new object[]
            {
                new { join = "OR", rules = new object[]
                {
                    new { join = "OR", type = "drCrOnly", drCr = "debit" },
                    NonBusinessDayBracket("OR")
                } },
                new { join = "OR", rules = new object[]
                {
                    NonBusinessDayBracket(holidayFirst: true)
                } }
            }
        });

        // 2026-10-04 第 7 批 R5：只更新舊樹讀回名稱，保留完整組內與組間連接的固定句。
        // 第一次失敗：20261004-085118314-9c43f4a2a69c4f718bab4f9f3d6aaa11。
        Assert.Equal("（僅借方 或 （週末過帳 或 假日過帳）） 或 （週末過帳 或 假日過帳）", result);
    }

    [Fact]
    public void WeekendAndHolidayBracket_IsNotMistakenForNonBusinessDay()
    {
        // 「週末 且 假日」語意不同，照一般括號讀回；只有一條或多一條的括號也不算。
        var andBracket = Render(new { groups = new[] { new { rules = new[] { NonBusinessDayBracket(secondJoin: "AND") } } } });
        var extraChild = Render(new { groups = new[] { new { rules = new object[] { new
        {
            type = "group",
            rules = new object[]
            {
                new { type = "prescreen", prescreenKey = "weekendPosting" },
                new { join = "OR", type = "prescreen", prescreenKey = "holidayPosting" },
                new { join = "OR", type = "prescreen", prescreenKey = "weekendApproval" }
            }
        } } } } });

        Assert.Equal("同一分錄（（預篩選：週末過帳 且 預篩選：假日過帳））", andBracket);
        Assert.Equal("同一分錄（（（預篩選：週末過帳 或 預篩選：假日過帳） 或 預篩選：週末核准））", extraChild);
    }

    [Fact]
    public void NonBusinessDayBracket_MatchReasonUsesOrdinaryConditionPosition()
    {
        // 符合原因由 QueryFilterVoucherHandlers 把單一條件包成一組再讀回，前面加「第 N 組條件 M：」。
        var single = Render(new { groups = new[] { new { rules = new[] { NonBusinessDayBracket() } } } });

        // 2026-10-04 第 7 批 R5：舊樹只描述週末或假日，保留一般條件位置與不含情境字樣的斷言。
        // 第一次失敗：20261004-085118314-9c43f4a2a69c4f718bab4f9f3d6aaa11。
        Assert.Equal("（週末過帳 或 假日過帳）", single);
        Assert.DoesNotContain("情境", single, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyScenario_RendersEmptyString()
    {
        // 無群組 → 空字串（無條件可述；step3 該列條件欄留空）。
        Assert.Equal(string.Empty, Render(new { groups = new object[0] }));
    }

    // 2026-10-03 用語統一：讀回跟著畫面改，借方與貸方改用逗號分開（S9），「所選母體」改為「分錄測試範圍」（T1），
    // 「主單位整數(捨小數)」改為「金額整數部分（不含小數）」（T6）。第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a。
    [Theory]
    [InlineData("exact", "相同分類用途：借貸科目組合：借方是 A 且貸方是 B（借方 Receivables，貸方 Revenue）")]
    [InlineData("debitAnchor", "相同分類用途：借貸科目組合：借方是 A，看它的對方科目（借方 Receivables）")]
    [InlineData("creditAnchor", "相同分類用途：借貸科目組合：貸方是 B，看它的對方科目（貸方 Revenue）")]
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
                    // 2026-10-02 起讀回只認分類身分陣列，單選分類欄位改成同一分類的陣列。
                    debitCategoryIds = new[] { "builtin.receivables" },
                    creditCategoryIds = new[] { "builtin.revenue" }
                }
            } } }
        });

        Assert.Equal(expected, result);
    }

    // ---- 條件組層級的週末、假日預篩選：一律照一般條件組讀回 ----
    // 2026-10-02 起非營業日只在條件括號辨識（見上方 NonBusinessDayBracket 測試），條件組本身不再做任何辨識；
    // 這兩個測試原本就斷言一般讀回，條件與預期值不變。

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

    // 2026-10-03 用語統一：讀回跟著畫面改，借方與貸方改用逗號分開（S9），「所選母體」改為「分錄測試範圍」（T1），
    // 「主單位整數(捨小數)」改為「金額整數部分（不含小數）」（T6）。第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a。
    [Theory]
    [InlineData("""{"join":"AND","type":"manualAuto","isManual":"true"}""", "人工分錄")]
    [InlineData("""{"join":"AND","type":"manualAuto","isManual":"false"}""", "自動分錄")]
    // 2026-10-02 起讀回只認分類身分陣列；下面兩列的單選分類欄位改成同一分類的陣列。
    [InlineData("""{"join":"AND","type":"accountPair","pairMode":"exact","debitCategoryIds":["builtin.receivables"],"creditCategoryIds":["builtin.revenue"]}""",
        "相同分類用途：借貸科目組合：借方是 A 且貸方是 B（借方 Receivables，貸方 Revenue）")]
    [InlineData("""{"join":"AND","type":"specialAccountCategoryPair","pairMode":"drNotCr","debitCategoryIds":["builtin.receivables"],"creditCategoryIds":["builtin.revenue"]}""",
        "相同分類用途：借貸科目組合：借方是 A 且整張傳票沒有 B 貸方（借方 Receivables，貸方 Revenue）")]
    [InlineData("""{"join":"AND","type":"customKeywords","keywords":"迴轉,調整"}""", "自訂關鍵字「迴轉,調整」")]
    // 2026-10-04 第 8 批 L59：自訂尾數讀回補「金額」，原 AST、6 位參數與全文等值斷言保留。
    // 第一次失敗：20261004-092023464-13b0a6928d5e400492fbe8a6a24eb69d。
    [InlineData("""{"join":"AND","type":"customTrailingZeros","digits":"6"}""", "金額尾數連續 6 個 0")]
    [InlineData("""{"join":"AND","type":"customPreparerEntryCount","maxEntries":"11"}""", "分錄測試範圍內編製人員分錄筆數 ≤ 11")]
    [InlineData("""{"join":"AND","type":"customAccountEntryCount","maxEntries":"11"}""", "分錄測試範圍內科目分錄筆數 ≤ 11")]
    [InlineData("""{"join":"AND","type":"revenueDebitNearQuarterEnd","windowDays":"5"}""", "季底或查核期末前 5 天借記收入")]
    [InlineData("""{"join":"AND","type":"revenueDebitNearQuarterEnd","windowDays":""}""", "季底或查核期末前 … 天借記收入")]
    [InlineData("""{"join":"AND","type":"revenueWithoutNormalCounterpart"}""", "貸方為收入，借方非應收或預收")]
    [InlineData("""{"join":"AND","type":"manualRevenueEntry"}""", "收入之人工分錄")]
    [InlineData("""{"join":"AND","type":"trailingDigits","keywords":"999999"}""", "金額整數部分（不含小數）末 6 位 = 999999")]
    [InlineData("""{"join":"AND","type":"trailingDigits","keywords":"000000,99"}""", "金額整數部分（不含小數）末 6 位 = 000000 或 末 2 位 = 99")]
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
    // 2026-10-03 用語統一：讀回跟著畫面改，借方與貸方改用逗號分開（S9），「所選母體」改為「分錄測試範圍」（T1），
    // 「主單位整數(捨小數)」改為「金額整數部分（不含小數）」（T6）。第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a。
    [Theory]
    [InlineData(
        """{"join":"AND","type":"accountPair","pairMode":"exact","debitCategoryIds":["builtin.receivables","builtin.cash"],"creditCategoryIds":["builtin.revenue"]}""",
        "相同分類用途：借貸科目組合：借方是 A 且貸方是 B（借方 Receivables、Cash，貸方 Revenue）")]
    [InlineData(
        """{"join":"AND","type":"accountPair","pairMode":"debitAnchor","debitCategoryIds":["builtin.cash"],"creditCategoryIds":["builtin.revenue"]}""",
        "相同分類用途：借貸科目組合：借方是 A，看它的對方科目（借方 Cash）")]
    [InlineData(
        """{"join":"AND","type":"specialAccountCategoryPair","pairMode":"drNotCr","debitCategoryIds":["builtin.receivables","builtin.cash"],"creditCategoryIds":["builtin.revenue"]}""",
        "相同分類用途：借貸科目組合：借方是 A 且整張傳票沒有 B 貸方（借方 Receivables、Cash，貸方 Revenue）")]
    [InlineData(
        """{"join":"AND","type":"accountPair","pairMode":"exact","debitCategory":"Cash","creditCategory":"Others","debitCategoryIds":["builtin.receivables"],"creditCategoryIds":["builtin.revenue"]}""",
        "相同分類用途：借貸科目組合：借方是 A 且貸方是 B（借方 Receivables，貸方 Revenue）")]
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
            "相同分類用途：借貸科目組合：借方是 A，看它的對方科目（借方 現金及約當現金、零用金）",
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
