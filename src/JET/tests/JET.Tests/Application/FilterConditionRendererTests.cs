using System.Text.Json;
using JET.Application;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 條件 AST → 中文布林式渲染器（<see cref="FilterConditionRenderer"/>）。
///
/// oracle 策略（規格 oracle）：預期值以前端 <c>filter-step-rules.js</c> 的 <c>readBackHtml</c> ＋
/// <c>ruleSummaryLabel(rule, 0)</c> 為準，逐一手算成字面斷言——渲染器必須與前端藍色 read-back
/// 的 <c>.scenario-readback__expr</c> 文字內容同構。標籤（週末過帳、假日過帳…）來自 Domain
/// FilterConditionLabels（前端鏡像，另有 FilterConditionLabelMirrorTests 守衛），此處不重測標籤內容，
/// 只鎖布林式的結構：組內組合器、組間運算子、內層括號、非營業日括號的讀回、空組過濾。
///
/// 設計技術：決策表（Decision Table）——以「條件組數 × 組內規則數 × 有無非營業日」為條件維度，
/// 每列一個代表情境。join 一律用大寫 AND/OR，和前端送出的值一致，讀回的比對分大小寫。
/// </summary>
public sealed class FilterConditionRendererTests
{
    // 省略借貸方向時，讀回只寫科目分類，不寫成借方。
    [Fact]
    public void ClassificationWithoutDirection_DoesNotDescribeItAsDebit()
    {
        var scenario = JsonElement.Parse("""
            {"groups":[{"conditions":[{"type":"accountSide","categoryMode":"is","categoryIds":["builtin.cash"],"categorySelection":"subtree"}]}]}
            """);
        var text = FilterConditionRenderer.Render(scenario);
        Assert.Contains("科目屬於「", text);
        Assert.DoesNotContain("借方", text);
        Assert.DoesNotContain("貸方", text);
    }

    // 省略 categorySelection 的舊條件仍比對相同分類用途，讀回明說這個範圍。
    // 十項固定預期帶此模式前綴，用完整等值比較。
    [Fact]
    public void ValueConditions_ReadNaturallyWithoutParenthesizedAnnotations()
    {
        using var document = JsonDocument.Parse("""
            {"groups":[{"conditions":[
              {"type":"fieldValue","field":"glDate","operator":"dayOfMonthIn","values":["28","31"]},
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
    public void RevenueDebitNearQuarterEnd_ReadbackIncludesActualMergedPeriodEndWindows()
    {
        using var doc = JsonDocument.Parse("""{"groups":[{"conditions":[{"type":"revenueDebitNearQuarterEnd","windowDays":5}]}]}""");
        Assert.Equal("季底或查核期末前 5 天借記收入；日期區間：2025-11-26～2025-11-30",
            FilterConditionRenderer.Render(doc.RootElement, null, periodStart: "2025-11-01", periodEnd: "2025-11-30"));
    }

    // ---- 單組（ne=1）----

    [Fact]
    public void SingleGroup_SinglePrescreen_RendersAtomWithoutOperators()
    {
        // 單組單規則＝無組合器、無括號。代表原子用週末過帳。
        var result = Render(new
        {
            groups = new[] { new { join = "AND", conditions = new object[]
            {
                new { join = "AND", type = "prescreen", prescreenKey = "weekendPosting" }
            } } }
        });

        Assert.Equal("預篩選：週末過帳", result);
    }

    [Fact]
    public void SingleGroup_TwoRulesOrCombinator_JoinsWithOr()
    {
        // 組內任一規則 join=OR → 整組 OR（groupCombinator）；單組不加外層括號。
        var result = Render(new
        {
            groups = new[] { new { join = "AND", conditions = new object[]
            {
                new { join = "AND", type = "prescreen", prescreenKey = "suspiciousKeywords" },
                new { join = "OR", type = "prescreen", prescreenKey = "holidayPosting" }
            } } }
        });

        Assert.Equal("預篩選：摘要特定描述 或 預篩選：假日過帳", result);
    }

    [Fact]
    public void SingleGroup_MixedEffectiveConditionJoins_RendersExactLeftFold()
    {
        var result = Render(new
        {
            groups = new[] { new { join = "AND", conditions = new object[]
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
            groups = new[] { new { join = "AND", conditions = new object[]
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
            groups = new[] { new { join = "and", conditions = new object[]
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
            groups = new[] { new { join = "AND", conditions = new object[]
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
            groups = new[] { new { join = "AND", conditions = new object[]
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

    // ---- 多組（ne≥2）：組間運算子 + 內層括號 ----

    [Fact]
    public void TwoGroups_ScenarioOr_SingleAtomGroups_NoInnerParens()
    {
        // 兩組各單原子 → 內層不包括號；組間運算子取第二可編輯組 join=OR。
        var result = Render(new
        {
            groups = new object[]
            {
                new { join = "OR", conditions = new object[]
                {
                    new { join = "AND", type = "fieldValue", field = "description", @operator = "contains", values = new[] { "調整" } }
                } },
                new { join = "OR", conditions = new object[]
                {
                    new { join = "AND", type = "drCrOnly", drCr = "debit" }
                } }
            }
        });

        Assert.Equal("傳票摘要 包含任一文字「調整」；空白不列入 或 僅借方", result);
    }

    [Fact]
    public void TwoGroups_ScenarioAnd_MultiAtomGroupWrappedInParens()
    {
        // 組間 AND（editable[1].join=AND）；含 ≥2 原子的組要包內層括號，單原子組不包。
        var result = Render(new
        {
            groups = new object[]
            {
                new { join = "AND", conditions = new object[]
                {
                    new { join = "AND", type = "fieldValue", field = "description", @operator = "contains", values = new[] { "調整" } },
                    new { join = "AND", type = "fieldValue", field = "amount", @operator = "greaterThanOrEqual", value = "1000000", amountBasis = "absolute" }
                } },
                new { join = "AND", conditions = new object[]
                {
                    new { join = "AND", type = "drCrOnly", drCr = "debit" }
                } }
            }
        });

        Assert.Equal("（傳票摘要 包含任一文字「調整」；空白不列入 且 金額絕對值 大於或等於「1000000」；空白不列入） 且 僅借方", result);
    }

    // ---- 已存情境裡兩條預篩選自成一組的非營業日條件(I)：照一般條件組讀回 ----
    // I 是組內的子條件組，讀回在括號辨識「週末過帳 或 假日過帳」。
    // 已保存的情境不轉換，舊形狀照條件樹讀回，與重開後畫面顯示的兩條預篩選一致。

    [Fact]
    public void OldPresetOnlyGroup_WithLeadingEmptyGroup_RendersOrdinaryOrGroup()
    {
        // 空組被過濾（不留懸空運算子）；只剩兩條預篩選的非營業日組 → 一般「或」條件組。
        var result = Render(new
        {
            groups = new object[]
            {
                new { join = "AND", conditions = new object[0] },
                new { join = "AND", conditions = new object[]
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
        // 單組單規則接兩條預篩選的非營業日組：照兩組「且」讀回，多條件的第二組加括號。
        var result = Render(new
        {
            groups = new object[]
            {
                new { join = "AND", conditions = new object[]
                {
                    new { join = "AND", type = "drCrOnly", drCr = "debit" }
                } },
                new { join = "AND", conditions = new object[]
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
        // 單組含 ≥2 規則接兩條預篩選的非營業日組：兩組各自加括號，語意 (a 或 b) 且 (週末 或 假日) 不會被誤讀。
        var result = Render(new
        {
            groups = new object[]
            {
                new { join = "AND", conditions = new object[]
                {
                    new { join = "AND", type = "fieldValue", field = "description", @operator = "contains", values = new[] { "調整" } },
                    new { join = "OR", type = "drCrOnly", drCr = "debit" }
                } },
                new { join = "AND", conditions = new object[]
                {
                    new { join = "OR", type = "prescreen", prescreenKey = "weekendPosting" },
                    new { join = "OR", type = "prescreen", prescreenKey = "holidayPosting" }
                } }
            }
        });

        Assert.Equal("（傳票摘要 包含任一文字「調整」；空白不列入 或 僅借方） 且 （預篩選：週末過帳 或 預篩選：假日過帳）", result);
    }

    // ---- 非營業日(I) 子條件組：在原位置讀回「非營業日（週末或假日）」----

    private static object NonBusinessDayBracket(string join = "AND", string secondJoin = "OR", bool holidayFirst = false) => new
    {
        join,
        type = "group",
        conditions = new object[]
        {
            new { join = "AND", type = "prescreen", prescreenKey = holidayFirst ? "holidayPosting" : "weekendPosting" },
            new { join = secondJoin, type = "prescreen", prescreenKey = holidayFirst ? "weekendPosting" : "holidayPosting" }
        }
    };

    [Fact]
    public void NonBusinessDayBracket_InsideGroup_RendersAtomInPlace()
    {
        // 非營業日卡片加入目前條件組，成為第 2 條：讀回留在原位置，不搬到最後、不另加「情境」字樣。
        var result = Render(new
        {
            groups = new[] { new { join = "AND", conditions = new object[]
            {
                new { join = "AND", type = "prescreen", prescreenKey = "blankDescription" },
                NonBusinessDayBracket(),
                new { join = "AND", type = "drCrOnly", drCr = "debit" }
            } } }
        });

        // 非營業日也排除補班日；舊 AST 保留週末或假日語意，不冒稱已排除補班日，位置與命中不變。
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
                new { join = "OR", conditions = new object[]
                {
                    new { join = "OR", type = "drCrOnly", drCr = "debit" },
                    NonBusinessDayBracket("OR")
                } },
                new { join = "OR", conditions = new object[]
                {
                    NonBusinessDayBracket(holidayFirst: true)
                } }
            }
        });

        // 非營業日也排除補班日，舊樹的讀回名稱只寫週末或假日；核對完整組內與組間連接的固定句。
        Assert.Equal("（僅借方 或 （週末過帳 或 假日過帳）） 或 （週末過帳 或 假日過帳）", result);
    }

    [Fact]
    public void WeekendAndHolidayBracket_IsNotMistakenForNonBusinessDay()
    {
        // 「週末 且 假日」語意不同，照一般括號讀回；只有一條或多一條的括號也不算。
        var andBracket = Render(new { groups = new[] { new { conditions = new[] { NonBusinessDayBracket(secondJoin: "AND") } } } });
        var extraChild = Render(new { groups = new[] { new { conditions = new object[] { new
        {
            type = "group",
            conditions = new object[]
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
        var single = Render(new { groups = new[] { new { conditions = new[] { NonBusinessDayBracket() } } } });

        // 非營業日也排除補班日，舊樹只描述週末或假日；讀回在一般條件位置，不含情境字樣。
        Assert.Equal("（週末過帳 或 假日過帳）", single);
        Assert.DoesNotContain("情境", single, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyScenario_RendersEmptyString()
    {
        // 無群組 → 空字串（無條件可述；step3 該列條件欄留空）。
        Assert.Equal(string.Empty, Render(new { groups = new object[0] }));
    }

    // 讀回的用語和畫面一致：借方與貸方用逗號分開。
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
            groups = new[] { new { join = "AND", conditions = new[]
            {
                new
                {
                    join = "AND",
                    type = "accountPair",
                    pairMode,
                    // 讀回只認分類身分陣列，分類用陣列帶入。
                    debitCategoryIds = new[] { "builtin.receivables" },
                    creditCategoryIds = new[] { "builtin.revenue" }
                }
            } } }
        });

        Assert.Equal(expected, result);
    }

    // ---- 條件組層級的週末、假日預篩選：一律照一般條件組讀回 ----
    // 非營業日只在子條件組辨識（見上方 NonBusinessDayBracket 測試），條件組本身不做任何辨識。

    [Fact]
    public void ManualAndCombinator_SameShapeGroup_RendersAsNormalGroup()
    {
        // 使用者從自訂面板手動加兩條 prescreen（週末+假日）、組合器 AND：語意是「週末 且 假日」，
        // 絕不可誤標為「非營業日（週末或假日）」（語意相反）。保存當下 read-back 顯示的就是 且 式。
        var result = Render(new
        {
            groups = new[] { new { join = "OR", conditions = new object[]
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
        // 手動 或 同形組（單組情境）：wire 上的 group join 經 toWireDraft 收斂為 "OR"（scenarioJoin 預設）。
        // 保存當下的讀回把它當一般可編輯組，寫成「預篩選：週末過帳 或 預篩選：假日過帳」，沒有預設區塊；渲染器照同樣的方式讀回。
        var result = Render(new
        {
            groups = new[] { new { join = "OR", conditions = new object[]
            {
                new { join = "OR", type = "prescreen", prescreenKey = "weekendPosting" },
                new { join = "OR", type = "prescreen", prescreenKey = "holidayPosting" }
            } } }
        });

        Assert.Equal("預篩選：週末過帳 或 預篩選：假日過帳", result);
    }

    // ---- 其餘原子型別與變體的讀回（決策表：每型別一代表值）----
    // oracle：前端 ruleSummaryLabel(rule, 0) 手算（等價分割：每型別一代表值；
    // manualAuto 補一負向變體、revenueDebitNearQuarterEnd 補缺值刪節號變體）。

    [Theory]
    [InlineData("""{"join":"AND","type":"manualAuto","isManual":"true"}""", "人工分錄")]
    [InlineData("""{"join":"AND","type":"manualAuto","isManual":"false"}""", "自動分錄")]
    // 讀回只認分類身分陣列；下面兩列用陣列帶分類。
    [InlineData("""{"join":"AND","type":"accountPair","pairMode":"exact","debitCategoryIds":["builtin.receivables"],"creditCategoryIds":["builtin.revenue"]}""",
        "相同分類用途：借貸科目組合：借方是 A 且貸方是 B（借方 Receivables，貸方 Revenue）")]
    [InlineData("""{"join":"AND","type":"specialAccountCategoryPair","pairMode":"drNotCr","debitCategoryIds":["builtin.receivables"],"creditCategoryIds":["builtin.revenue"]}""",
        "相同分類用途：借貸科目組合：借方是 A 且整張傳票沒有 B 貸方（借方 Receivables，貸方 Revenue）")]
    [InlineData("""{"join":"AND","type":"revenueDebitNearQuarterEnd","windowDays":"5"}""", "季底或查核期末前 5 天借記收入")]
    [InlineData("""{"join":"AND","type":"revenueDebitNearQuarterEnd","windowDays":""}""", "季底或查核期末前 … 天借記收入")]
    // 固定條件的讀回和卡片名稱統一。
    [InlineData("""{"join":"AND","type":"revenueWithoutNormalCounterpart"}""", "貸方為收入但借方非一般對方科目")]
    [InlineData("""{"join":"AND","type":"manualRevenueEntry"}""", "收入之人工分錄")]
    [InlineData("""{"join":"AND","type":"preparerEqualsApprover"}""", "編製人員與核准人員相同")]
    public void ConditionAtom_AllTypes_MatchFrontendRuleSummaryLabel(string ruleJson, string expectedAtom)
    {
        var json = $$"""{"groups":[{"join":"AND","conditions":[{{ruleJson}}]}]}""";
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expectedAtom, FilterConditionRenderer.Render(document.RootElement));
    }

    /* ---- 借貸組合多選讀回 ------------------------------------------------- */

    // 多選讀回把每個分類身分還原成科目分類的顯示 label，依使用者選取順序、以 Domain 正本
    // 的分隔字元串接；帶陣列時 scalar 不參與呈現（與判定端一致）。
    // 讀回的用語和畫面一致：借方與貸方用逗號分開。
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
    public void PairAtom_CategoryIdArrays_RenderCategoryLabelsInSelectionOrder(
        string ruleJson,
        string expectedAtom)
    {
        var json = $$"""{"groups":[{"join":"AND","conditions":[{{ruleJson}}]}]}""";
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expectedAtom, FilterConditionRenderer.Render(document.RootElement));
    }

    [Fact]
    public void PairAtom_UsesProjectCategoryLabels_IncludingRenamedBuiltInsAndCustomCategories()
    {
        const string customId = "custom.0123456789abcdef0123456789abcdef";
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["builtin.cash"] = "現金及約當現金",
            [customId] = "零用金"
        };
        var json = $$"""
            {"groups":[{"join":"AND","conditions":[{"join":"AND","type":"accountPair","pairMode":"debitAnchor",
            "debitCategoryIds":["builtin.cash","{{customId}}"]}]}]}
            """;
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            "相同分類用途：借貸科目組合：借方是 A，看它的對方科目（借方 現金及約當現金、零用金）",
            FilterConditionRenderer.Render(document.RootElement, labels));
    }

    // 底稿與報告的條件說明，欄位名稱照原樣寫，不加任何符號，值用「」包住；畫面的讀回也不在欄位名稱加符號。
    // 兩種寫法只差在統計條件：畫面用「」包住欄位名稱，文件裡欄位名稱前後各留一個半形空白。
    // 認不得的欄位寫的是替代句，兩種寫法相同。
    [Fact]
    public void ForDocument_WritesFieldNamesWithoutSymbols_AndOnlyEntityFrequencyDiffersFromScreen()
    {
        const string rdeField = "rde.dddd0000dddd0000dddd0000dddd0000";
        var rdeLabels = new Dictionary<string, string>(StringComparer.Ordinal) { [rdeField] = "成本中心" };
        var json = $$"""
            {"groups":[{"join":"AND","conditions":[
              {"join":"AND","type":"fieldValue","field":"description","operator":"contains","values":["調整"]},
              {"join":"AND","type":"fieldValue","field":"amount","operator":"greaterThanOrEqual","value":"1000","amountBasis":"absolute"},
              {"join":"AND","type":"fieldValue","field":"glDate","operator":"onOrAfter","value":"2024-01-01"},
              {"join":"AND","type":"fieldValue","field":"description","operator":"in","values":["1101"]},
              {"join":"AND","type":"fieldValue","field":"amount","operator":"between","from":"10","to":"20","amountBasis":"absolute"},
              {"join":"AND","type":"fieldValue","fieldId":"{{rdeField}}","operator":"equals","value":"X"},
              {"join":"AND","type":"entityFrequency","field":"createdBy","countUnit":"entries","countOperator":"lessThan","countFrom":"3"},
              {"join":"AND","type":"fieldValue","field":"nope","operator":"contains","values":["x"]},
              {"join":"AND","type":"fieldValue","fieldId":"rde.gone","operator":"isBlank"}
            ]}]}
            """;
        using var document = JsonDocument.Parse(json);

        var screen = FilterConditionRenderer.Render(document.RootElement, null, rdeLabels);
        var paper = FilterConditionRenderer.Render(document.RootElement, null, rdeLabels, forDocument: true);

        Assert.DoesNotContain("【", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("【", paper, StringComparison.Ordinal);
        Assert.DoesNotContain("】", paper, StringComparison.Ordinal);
        Assert.Contains("傳票摘要 包含任一文字「調整」", paper, StringComparison.Ordinal);
        Assert.Contains("金額絕對值 大於或等於「1000」", paper, StringComparison.Ordinal);
        Assert.Contains("總帳日期 當日或以後「2024-01-01」", paper, StringComparison.Ordinal);
        Assert.Contains("傳票摘要 符合清單任一值「1101」", paper, StringComparison.Ordinal);
        Assert.Contains("金額絕對值 介於「10」至「20」", paper, StringComparison.Ordinal);
        Assert.Contains("成本中心 等於「X」", paper, StringComparison.Ordinal);
        Assert.Contains("GL 中「傳票建立人員」分錄筆數", screen, StringComparison.Ordinal);
        Assert.Contains("GL 中 傳票建立人員 分錄筆數", paper, StringComparison.Ordinal);
        Assert.Contains("已不存在的欄位 包含任一文字「x」", paper, StringComparison.Ordinal);
        Assert.Contains("已不在欄位配對中的攸關資料元素欄位 空白", paper, StringComparison.Ordinal);
        Assert.Equal(
            screen.Replace("GL 中「傳票建立人員」分錄筆數", "GL 中 傳票建立人員 分錄筆數", StringComparison.Ordinal),
            paper);
    }
}
