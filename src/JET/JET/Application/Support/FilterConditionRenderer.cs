using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 已存篩選情境的條件 AST（前端送出的 wire JSON）→ 中文布林式，與前端藍色 read-back
/// （filter-step.js <c>readBackHtml</c> 的 <c>.scenario-readback__expr</c> 文字內容）同構。
/// CriteriaSelection summary 的「條件內容」以本渲染器輸出為值（由
/// <see cref="ExportCriteriaSelectionReportHandler"/> 計算，經
/// <see cref="CriteriaSelectionReportContext.ScenarioConditionLogic"/> 傳入 writer）。
///
/// 為什麼吃「原始 JSON」而非 <see cref="FilterScenarioSpec"/>：read-back 顯示的是使用者輸入的**顯示字串**
/// （numRange 的 from/to、text/customKeywords 的 keywords 原文），而解析成 spec 後金額已 scaled、關鍵字已切分，
/// 顯示原文遺失；要真正同構就必須讀原始 wire。層級：JSON 形狀處理與 <see cref="FilterScenarioPayloadParser"/>
/// 同屬 Application；中文標籤的唯一正本在 Domain <see cref="FilterConditionLabels"/>（前端鏡像、測試守衛）。
///
/// 非營業日 KCT I 保留條件括號，內含排除補班日的日期條件。舊版「週末過帳 或 假日過帳」
/// 仍按原樹描述，不改成新版定義，也不搬到其他位置；本類只影響顯示文字。
/// </summary>
public static class FilterConditionRenderer
{
    private const string ScenarioAndOp = " 且 ";

    public static string Render(JsonElement scenario) => Render(scenario, null);

    /// <summary>
    /// <paramref name="categoryLabels"/> 是目前專案 taxonomy 的 categoryId → 顯示 label 對照，
    /// 供科目配對多選讀回。省略時只還原內建分類的預設 label，未知身分提示重新選擇；正式報表一律
    /// 傳入專案 taxonomy，才會反映使用者改過的顯示名稱。
    /// <paramref name="rdeFieldLabels"/> 是目前 committed RDE 欄位的 fieldId → 顯示 label 對照，
    /// 供 typed 條件讀回（凍結裁決：renderer 用目前 metadata，僅 label 改名時顯示新名稱）；
    /// 省略或欄位已不存在時退回 fieldId 原字串。
    /// <paramref name="taxonomyCategories"/> 帶完整分類用途與階層，才能列出實際納入分類。省略時不猜分類範圍。
    /// <paramref name="preparationDate"/> 是目前案件的期末財報準備日，只供讀回說明，不寫入條件 AST。
    /// </summary>
    public static string Render(
        JsonElement scenario,
        IReadOnlyDictionary<string, string>? categoryLabels,
        IReadOnlyDictionary<string, string>? rdeFieldLabels = null,
        IReadOnlyList<AccountTaxonomyCategory>? taxonomyCategories = null,
        string? preparationDate = null, string? periodStart = null, string? periodEnd = null)
    {
        var editable = ArrayItems(scenario, "groups");

        // read-back 的 `ne`：只看「有規則」的可編輯組（wire 已濾空組，此處再守一次亦無害）。
        var ne = editable.Where(g => ArrayItems(g, "rules").Count > 0).ToList();

        var expr = string.Empty;
        if (ne.Count == 1)
        {
            expr = GroupExpression(ne[0], categoryLabels, rdeFieldLabels, taxonomyCategories, preparationDate, periodStart, periodEnd).Text;
        }
        else if (ne.Count >= 2)
        {
            var sop = ScenarioJoin(ne); // Empty editor groups do not contribute an operand or a join to the wire.
            var parts = ne.Select(g =>
            {
                var rendered = GroupExpression(g, categoryLabels, rdeFieldLabels, taxonomyCategories, preparationDate, periodStart, periodEnd);
                return rendered.AtomCount > 1 && !rendered.IsExactLeftFold
                    ? "（" + rendered.Text + "）"
                    : rendered.Text; // mixed 已逐邊累積精確括號；uniform 多原子組維持既有單層括號
            }).ToArray();
            if (HasMixedEffectiveRuleJoins(ne))
            {
                expr = parts[0];
                for (var index = 1; index < parts.Length; index++)
                    expr = "（" + expr + " " + JoinLabel(EffectiveRuleJoin(ne[index])) + " " + parts[index] + "）";
            }
            else expr = string.Join(" " + JoinLabel(sop) + " ", parts);
        }

        return expr;
    }

    // ---- 群組層 ----

    /// <summary>只讀第一條之後的有效結合，大小寫規則與後端相同。</summary>
    private static string GroupCombinator(JsonElement group) =>
        ArrayItems(group, "rules").Skip(1).Any(r => EffectiveRuleJoin(r) == "OR") ? "OR" : "AND";

    /// <summary>
    /// 既有主前端會把組內 join 收斂為單一運算子，所以 uniform 組仍沿用原來的
    /// <see cref="GroupCombinator"/> 呈現。只有已落地 AST 的有效邊（第一條規則之後）
    /// 同時含 AND 與 OR 時，才依 SQL 相同的左折疊逐邊加全形括號。
    /// </summary>
    private static GroupRendering GroupExpression(
        JsonElement group,
        IReadOnlyDictionary<string, string>? categoryLabels,
        IReadOnlyDictionary<string, string>? rdeFieldLabels,
        IReadOnlyList<AccountTaxonomyCategory>? taxonomyCategories,
        string? preparationDate, string? periodStart, string? periodEnd)
    {
        var rules = ArrayItems(group, "rules");
        var atoms = rules.Select(rule => RuleAtom(rule, categoryLabels, rdeFieldLabels, taxonomyCategories, preparationDate, periodStart, periodEnd)).ToList();
        if (Str(group, "matchScope") == "sameVoucher")
        {
            return SameVoucherGroupExpression(atoms);
        }

        if (!HasMixedEffectiveRuleJoins(rules))
        {
            return new GroupRendering(
                JoinAtoms(atoms, GroupCombinator(group)),
                atoms.Count,
                IsExactLeftFold: false);
        }

        var expression = atoms[0];
        for (var index = 1; index < atoms.Count; index++)
        {
            expression = "（"
                + expression
                + " "
                + JoinLabel(EffectiveRuleJoin(rules[index]))
                + " "
                + atoms[index]
                + "）";
        }

        return new GroupRendering(expression, atoms.Count, IsExactLeftFold: true);
    }

    private static GroupRendering SameVoucherGroupExpression(IReadOnlyList<string> atoms)
    {
        if (atoms.Count == 0)
        {
            return new GroupRendering(string.Empty, 0, IsExactLeftFold: false);
        }

        var expression = FilterConditionLabels.SameVoucherOutputAnchor + "：" + atoms[0];
        if (atoms.Count > 1)
        {
            expression += ScenarioAndOp
                + FilterConditionLabels.SameVoucherEvidenceExplanation
                + "："
                + string.Join(ScenarioAndOp, atoms.Skip(1));
        }

        return new GroupRendering(expression, atoms.Count, IsExactLeftFold: false);
    }

    private static bool HasMixedEffectiveRuleJoins(IReadOnlyList<JsonElement> rules)
    {
        var hasAnd = false;
        var hasOr = false;
        for (var index = 1; index < rules.Count; index++)
        {
            if (EffectiveRuleJoin(rules[index]) == "OR")
            {
                hasOr = true;
            }
            else
            {
                hasAnd = true;
            }
        }

        return hasAnd && hasOr;
    }

    // Mixed 判定必須跟 typed AST 一樣忽略大小寫與前後空白；只有 mixed branch 使用此值。
    // Uniform fallback 仍走既有大小寫敏感 GroupCombinator，因此全小寫 uniform 輸出不變。
    private static string EffectiveRuleJoin(JsonElement rule) =>
        string.Equals(
            Str(rule, "join")?.Trim(),
            "OR",
            StringComparison.OrdinalIgnoreCase)
            ? "OR"
            : "AND";

    /// <summary>有效組間運算子：第二個非空組的 join；第一組 join 不參與左折疊語意。</summary>
    private static string ScenarioJoin(IReadOnlyList<JsonElement> editable) =>
        editable.Count >= 2 ? EffectiveRuleJoin(editable[1]) : "OR";

    private static string JoinAtoms(IReadOnlyList<string> atoms, string op) =>
        string.Join(" " + JoinLabel(op) + " ", atoms);

    private static string JoinLabel(string op) => op == "OR" ? "或" : "且";

    private readonly record struct GroupRendering(
        string Text,
        int AtomCount,
        bool IsExactLeftFold);

    /// <summary>
    /// 舊週末或假日括號辨識：條件括號（type "group"）恰有兩條子條件，都是預篩選，鍵集合恰為
    /// {weekendPosting, holidayPosting}，且第二條的有效連接是「或」（第一條的 join 沒有左側條件，不算）。
    /// 符合時語意仍是「週末或假日」，不聲稱已排除補班日；
    /// 「週末 且 假日」語意不同，照一般括號讀回。前端 filter-step.js 的 isNonBusinessDayBracket 逐字鏡像。
    /// </summary>
    private static bool IsWeekendOrHolidayBracket(JsonElement rule)
    {
        if (Str(rule, "type") != "group")
        {
            return false;
        }

        var children = ArrayItems(rule, "rules");
        if (children.Count != 2 || EffectiveRuleJoin(children[1]) != "OR")
        {
            return false;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in children)
        {
            if (Str(child, "type") != "prescreen")
            {
                return false;
            }

            keys.Add(Str(child, "prescreenKey"));
        }

        return keys.SetEquals([PrescreenRuleKeys.WeekendPosting, PrescreenRuleKeys.HolidayPosting]);
    }

    private static bool IsNonBusinessDayBracket(JsonElement rule)
    {
        if (Str(rule, "type") != "group") return false;
        var children = ArrayItems(rule, "rules");
        if (children.Count != 1) return false;
        var child = children[0];
        return Str(child, "type") == "fieldValue" && Str(child, "field") == "postDate"
            && Str(child, "fieldId").Length == 0 && Str(child, "operator") == "isNonBusinessDay"
            && Str(child, "drCr").Length == 0 && Str(child, "includeBlank") != "true";
    }

    // ---- 規則原子（等同前端 ruleSummaryLabel(rule, 0)：index 0＝不加 (AND)/(OR) 前綴）----

    private static string RuleAtom(
        JsonElement r,
        IReadOnlyDictionary<string, string>? categoryLabels,
        IReadOnlyDictionary<string, string>? rdeFieldLabels,
        IReadOnlyList<AccountTaxonomyCategory>? taxonomyCategories,
        string? preparationDate, string? periodStart, string? periodEnd)
    {
        var type = Str(r, "type");
        var text = type switch
        {
            "group" when IsNonBusinessDayBracket(r) => FilterConditionLabels.NonBusinessDayAtom,
            "group" when IsWeekendOrHolidayBracket(r) => "（"
                + FilterConditionLabels.PrescreenLabel(PrescreenRuleKeys.WeekendPosting) + " 或 "
                + FilterConditionLabels.PrescreenLabel(PrescreenRuleKeys.HolidayPosting) + "）",
            "group" => NestedAtom(r, categoryLabels, rdeFieldLabels, taxonomyCategories, preparationDate, periodStart, periodEnd),
            "voucher" => NestedAtom(r, categoryLabels, rdeFieldLabels, taxonomyCategories, preparationDate, periodStart, periodEnd),
            "prescreen" => PrescreenAtom(Str(r, "prescreenKey"), preparationDate),
            "text" => FilterConditionLabels.GlFieldLabel(Str(r, "field")) + " "
                + FilterConditionLabels.TextModeLabel(Str(r, "mode")) + "「" + Str(r, "keywords") + "」",
            "textSet" => TextSetAtom(r),
            "dateRange" => FilterConditionLabels.GlFieldLabel(Str(r, "field")) + " "
                + Ellipsis(Str(r, "from")) + "～" + Ellipsis(Str(r, "to")),
            "numRange" => NumRangeAtom(r),
            "drCrOnly" => Str(r, "drCr") == "credit" ? "僅貸方" : "僅借方",
            "manualAuto" => Str(r, "isManual") == "false" ? "自動分錄" : "人工分錄",
            "accountPair" => AccountPairAtom(r, categoryLabels),
            "specialAccountCategoryPair" => FilterConditionLabels.AccountCombinationPrefix
                + FilterConditionLabels.SpecialPairModeLabel(Str(r, "pairMode")) + "（借方 "
                + CategorySelection(r, "debitCategoryIds", categoryLabels) + "，貸方 "
                + CategorySelection(r, "creditCategoryIds", categoryLabels) + "）",
            "customKeywords" => "自訂關鍵字「" + Str(r, "keywords") + "」",
            "customTrailingZeros" => "金額尾數連續 " + Str(r, "digits") + " 個 0",
            "customPreparerEntryCount" => "分錄測試範圍內編製人員分錄筆數 ≤ " + Str(r, "maxEntries"),
            "customAccountEntryCount" => "分錄測試範圍內科目分錄筆數 ≤ " + Str(r, "maxEntries"),
            "entityFrequency" => "分錄測試範圍內「" + JetFieldCatalog.GlSemanticFieldMappingLabel(Str(r, "field")) + "」" +
                (Str(r, "countUnit") == "vouchers" ? "傳票張數（同號只算一張）" : "分錄筆數") + " " +
                (EntityFrequencyConditions.Operators.TryGetValue(Str(r, "countOperator"), out var countLabel) ? countLabel : "") + " " +
                Str(r, "countFrom") + (Str(r, "countOperator") == "between" ? "～" + Str(r, "countTo") + "（含端點）" : ""),
            "revenueDebitNearQuarterEnd" => QuarterEndAtom(r, periodStart, periodEnd),
            "revenueWithoutNormalCounterpart" => "貸方為收入，借方非應收或預收",
            "manualRevenueEntry" => "收入之人工分錄",
            "trailingDigits" => TrailingDigitsAtom(Str(r, "keywords")),
            "preparerEqualsApprover" => "編製＝核准同一人",
            "typed" => TypedFieldAtom(r, rdeFieldLabels),
            "fieldValue" => FieldValueAtom(r, rdeFieldLabels),
            "accountSide" => AccountSideAtom(r, categoryLabels, taxonomyCategories),
            _ => type,
        };
        if (type is not ("accountPair" or "specialAccountCategoryPair")) return text;
        text = CategorySelectionModeLabel(r) + "：" + text;
        if (taxonomyCategories is null) return text;
        if (type != "accountPair" || Str(r, "pairMode") != AccountPairModes.CreditAnchor)
            text += "；借方實際納入分類：" + ExpandedCategoryLabels(r, "debitCategoryIds", taxonomyCategories);
        if (type != "accountPair" || Str(r, "pairMode") != AccountPairModes.DebitAnchor)
            text += "；貸方實際納入分類：" + ExpandedCategoryLabels(r, "creditCategoryIds", taxonomyCategories);
        return text;
    }

    private static string QuarterEndAtom(JsonElement rule, string? periodStart, string? periodEnd)
    {
        var text = "季底或查核期末前 " + Ellipsis(Str(rule, "windowDays")) + " 天借記收入";
        if (int.TryParse(Str(rule, "windowDays"), out var days) && periodStart is not null && periodEnd is not null)
        {
            var windows = QuarterEndWindows.Compute(periodStart, periodEnd, days);
            text += "；日期區間：" + string.Join("、", windows.Select(w => w.FromIso + "～" + w.ToIso));
        }
        return text;
    }

    private static string PrescreenAtom(string key, string? preparationDate)
    {
        var text = "預篩選：" + FilterConditionLabels.PrescreenLabel(key);
        return key == PrescreenRuleKeys.PostPeriodApproval && !string.IsNullOrWhiteSpace(preparationDate)
            ? text + "（" + preparationDate + " 起，含當日）"
            : text;
    }

    private static string AccountSideAtom(JsonElement rule, IReadOnlyDictionary<string, string>? labels,
        IReadOnlyList<AccountTaxonomyCategory>? taxonomyCategories)
    {
        var side = Str(rule, "drCr") switch { "credit" => "貸方", "any" => "不限借貸", _ => "借方" };
        var categories = CategorySelection(rule, "categoryIds", labels);
        var text = Str(rule, "categoryMode") switch
        {
            "is" => side + "科目屬於「" + categories + "」",
            "isNot" => side + "科目不屬於「" + categories + "」",
            "absent" => side == "不限借貸" ? "整張傳票都沒有屬於「" + categories + "」的科目"
                : "整張傳票的" + side + "都不屬於「" + categories + "」",
            _ => side + "尚未選擇分類條件"
        };
        text += "（" + CategorySelectionModeLabel(rule) + "）";
        return taxonomyCategories is null ? text
            : text + "；實際納入分類：" + ExpandedCategoryLabels(rule, "categoryIds", taxonomyCategories);
    }

    private static string NestedAtom(JsonElement rule, IReadOnlyDictionary<string, string>? categories,
        IReadOnlyDictionary<string, string>? fields, IReadOnlyList<AccountTaxonomyCategory>? taxonomyCategories,
        string? preparationDate, string? periodStart, string? periodEnd)
    {
        var children = ArrayItems(rule, "rules");
        var text = string.Empty;
        foreach (var child in children)
        {
            var atom = RuleAtom(child, categories, fields, taxonomyCategories, preparationDate, periodStart, periodEnd);
            text = text.Length == 0 ? atom : "（" + text + " " + JoinLabel(EffectiveRuleJoin(child)) + " " + atom + "）";
        }
        if (Str(rule, "type") == "group")
        {
            var scope = children.Count > 0 && children.All(IsExplicitVoucherCondition) ? "同張傳票"
                : children.Any(ContainsExplicitVoucherCondition) ? "條件組合" : "同一分錄";
            return scope + "（" + text + "）";
        }
        var side = Str(rule, "side") switch { "debit" => "借方", "credit" => "貸方", _ => "整張傳票" };
        var quantifier = Str(rule, "quantifier") switch { "all" => "全部符合（至少有一筆）", "none" => "不存在符合", _ => "至少一筆符合" };
        return side + quantifier + "：" + (children.Count == 1 ? text : "同一分錄（" + text + "）");
    }

    private static bool IsExplicitVoucherCondition(JsonElement rule) => Str(rule, "type") == "voucher"
        || Str(rule, "type") == "group" && ArrayItems(rule, "rules") is { Count: > 0 } children && children.All(IsExplicitVoucherCondition);

    private static bool ContainsExplicitVoucherCondition(JsonElement rule) => Str(rule, "type") == "voucher"
        || ArrayItems(rule, "rules").Any(ContainsExplicitVoucherCondition);

    private static string FieldValueAtom(JsonElement rule, IReadOnlyDictionary<string, string>? labels)
    {
        var fieldId = Str(rule, "fieldId");
        var field = fieldId.Length == 0 ? FilterConditionLabels.GlFieldLabel(Str(rule, "field"))
            : labels?.GetValueOrDefault(fieldId) ?? fieldId;
        // Normalized filter amount has no text/date mapping label in GlFields.
        if (fieldId.Length == 0 && Str(rule, "field") == "amount") field = "金額";
        var op = Str(rule, "operator");
        var opLabel = FieldValueConditions.Labels.GetValueOrDefault(op, op);
        var listValues = StringArrayItems(rule, "values");
        var operand = op is "isBlank" or "isNotBlank" || FieldValueConditions.IsCalendar(op) ? "" : op is "between" or "notBetween"
            ? "「" + Str(rule, "from") + "」至「" + Str(rule, "to") + "」"
            : op is "in" or "notIn" or FieldValueConditions.DayOfMonthIn or FieldValueConditions.DayOfMonthNotIn
                ? "「" + string.Join("、", listValues) + "」"
            : FieldValueConditions.IsContains(op) && listValues.Count > 0 ? "「" + string.Join("、", listValues) + "」"
            : "「" + Str(rule, "value") + "」";
        if (op is not "isBlank" and not "isNotBlank" && !FieldValueConditions.IsTail(op))
            field += Str(rule, "amountBasis") switch { "absolute" => "絕對值", "signed" => "含正負號", _ => "" };
        // 空白預設不列入（2026-09-04 裁定），與 GlRulePredicates.FieldValue 的預設一致。
        var includeBlank = rule.TryGetProperty("includeBlank", out var blank) && blank.ValueKind == JsonValueKind.True;
        var blankText = op is "isBlank" or "isNotBlank" ? "" : includeBlank ? "；空白也符合" : "；空白不列入";
        var side = Str(rule, "drCr") switch { "debit" => "借方分錄：", "credit" => "貸方分錄：", _ => "" };
        return side + field + " " + opLabel + operand + (FieldValueConditions.IsMonthWindow(op) ? " 天" : "") + blankText;
    }

    /// <summary>
    /// typed 條件讀回：RDE 欄位以目前 metadata 的顯示 label 呈現（僅 label 改名時自動跟進；
    /// 欄位已不存在或未提供對照時退回 fieldId 原字串）；operand 依 carrier 呈現原始 wire 字串；
    /// money 條件附 amountBasis 標示。標籤正本在 Domain <see cref="FilterConditionLabels.TypedOperators"/>。
    /// </summary>
    private static string TypedFieldAtom(
        JsonElement r,
        IReadOnlyDictionary<string, string>? rdeFieldLabels)
    {
        var fieldId = Str(r, "fieldId");
        var fieldLabel = rdeFieldLabels is not null
            && rdeFieldLabels.TryGetValue(fieldId, out var label)
            ? label
            : fieldId;
        var op = Str(r, "operator");
        var opLabel = FilterConditionLabels.TypedOperatorLabel(op);

        string operand;
        if (op is TypedFieldOperatorSets.IsBlank or TypedFieldOperatorSets.IsNotBlank)
        {
            operand = string.Empty;
        }
        else if (op is TypedFieldOperatorSets.Between)
        {
            operand = "「" + Ellipsis(Str(r, "from")) + "」～「" + Ellipsis(Str(r, "to")) + "」";
        }
        else if (op is TypedFieldOperatorSets.In or TypedFieldOperatorSets.NotIn)
        {
            operand = "「" + string.Join(FilterConditionLabels.CategoryListSeparator,
                StringArrayItems(r, "values")) + "」";
        }
        else
        {
            operand = "「" + Str(r, "value") + "」";
        }

        var basis = Str(r, "amountBasis") switch
        {
            TypedAmountBasisNames.Signed => "（" + FilterConditionLabels.TypedSignedAmountBasis + "）",
            TypedAmountBasisNames.Absolute => "（" + FilterConditionLabels.TypedAbsoluteAmountBasis + "）",
            _ => string.Empty
        };

        return fieldLabel + " " + opLabel + operand + basis;
    }

    private static string NumRangeAtom(JsonElement r)
    {
        var from = Str(r, "from");
        var to = Str(r, "to");
        return "金額（絕對值） "
            + (from.Length > 0 ? "≥ " + from : string.Empty)
            + (from.Length > 0 && to.Length > 0 ? "、" : string.Empty)
            + (to.Length > 0 ? "≤ " + to : string.Empty);
    }

    private static string TextSetAtom(JsonElement r)
    {
        var modeLabel = Str(r, "mode") == "exact"
            ? FilterConditionLabels.TextSetExactAny
            : FilterConditionLabels.TextSetContainsAny;
        var normalizationLabel = Str(r, "normalization") == "removeAsciiSpaces"
            ? FilterConditionLabels.TextSetRemoveAsciiSpaces
            : FilterConditionLabels.TextSetPreserveAsciiSpaces;
        return FilterConditionLabels.GlFieldLabel(Str(r, "field"))
            + " "
            + modeLabel
            + "「"
            + string.Join("、", StringArrayItems(r, "values"))
            + "」（"
            + normalizationLabel
            + "）";
    }

    private static string AccountPairAtom(
        JsonElement r,
        IReadOnlyDictionary<string, string>? categoryLabels)
    {
        var mode = Str(r, "pairMode");
        var debit = CategorySelection(r, "debitCategoryIds", categoryLabels);
        var credit = CategorySelection(r, "creditCategoryIds", categoryLabels);
        var detail = mode switch
        {
            AccountPairModes.DebitAnchor => "借方 " + debit,
            AccountPairModes.CreditAnchor => "貸方 " + credit,
            _ => "借方 " + debit + "，貸方 " + credit
        };
        return FilterConditionLabels.AccountCombinationPrefix + FilterConditionLabels.AccountPairModeLabel(mode) + "（" + detail + "）";
    }

    /// <summary>
    /// 單側分類的讀回：帶 ID 陣列時逐一還原成 taxonomy 顯示 label，並依使用者選取順序串接
    /// （分隔字元的正本在 Domain <see cref="FilterConditionLabels.CategoryListSeparator"/>）；
    /// 沒有陣列或陣列為空時讀回空字串。
    /// </summary>
    private static string CategorySelection(
        JsonElement r,
        string idsProperty,
        IReadOnlyDictionary<string, string>? categoryLabels)
    {
        if (r.ValueKind != JsonValueKind.Object
            || !r.TryGetProperty(idsProperty, out var ids)
            || ids.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var labels = ids.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(item => CategoryLabel(item.GetString() ?? string.Empty, categoryLabels))
            .Where(static label => label.Length > 0)
            .ToArray();
        return string.Join(FilterConditionLabels.CategoryListSeparator, labels);
    }

    private static string CategoryLabel(
        string categoryId,
        IReadOnlyDictionary<string, string>? categoryLabels)
    {
        if (categoryLabels is not null && categoryLabels.TryGetValue(categoryId, out var label))
        {
            return label;
        }

        var builtIn = AccountTaxonomyBuiltIns.All.SingleOrDefault(
            item => string.Equals(item.CategoryId, categoryId, StringComparison.Ordinal));
        return builtIn?.Label ?? "已刪除的分類，請重新選擇";
    }

    private static string CategorySelectionModeLabel(JsonElement rule) => Str(rule, "categorySelection") switch
    {
        "node" => "僅分類本身", "subtree" => "包含下層分類", _ => "相同分類用途"
    };

    private static string ExpandedCategoryLabels(JsonElement rule, string idsProperty,
        IReadOnlyList<AccountTaxonomyCategory> categories)
    {
        var selected = StringArrayItems(rule, idsProperty).ToHashSet(StringComparer.Ordinal);
        var mode = Str(rule, "categorySelection");
        if (mode == "subtree")
        {
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var category in categories)
                    if (category.ParentCategoryId is not null && selected.Contains(category.ParentCategoryId))
                        changed |= selected.Add(category.CategoryId);
            }
        }
        else if (mode != "node")
        {
            var roles = categories.Where(category => selected.Contains(category.CategoryId))
                .Select(category => category.SemanticRole).ToHashSet(StringComparer.Ordinal);
            selected = categories.Where(category => roles.Contains(category.SemanticRole))
                .Select(category => category.CategoryId).ToHashSet(StringComparer.Ordinal);
        }
        var labels = categories.Where(category => selected.Contains(category.CategoryId))
            .OrderBy(category => category.Ordinal).ThenBy(category => category.CategoryId, StringComparer.Ordinal)
            .Select(category => category.Label).ToArray();
        return labels.Length == 0 ? "無" : string.Join(FilterConditionLabels.CategoryListSeparator, labels);
    }

    /// <summary>
    /// 尾數比對讀回：金額整數部分（不含小數）末 k 位 = 樣態,k 為樣態字元數;多樣態以「或」串接。
    /// 講清楚「先捨小數取整數、再比末 k 位」,不是比對顯示金額或小數。前端 ruleSummary 的
    /// trailingDigits 分支需產生逐字相同字串（讀回與 CriteriaSelection summary 同構）。
    /// </summary>
    private static string TrailingDigitsAtom(string keywords)
    {
        var atoms = new List<string>();
        foreach (var raw in FieldValueConditions.SplitInputList(keywords))
        {
            var p = raw.Trim();
            if (p.Length > 0)
            {
                atoms.Add($"末 {p.Length} 位 = {p}");
            }
        }

        return "金額整數部分（不含小數）" + string.Join(" 或 ", atoms);
    }

    /// <summary>空值以刪節號替代（同前端 <c>rule.from || '…'</c>）。</summary>
    private static string Ellipsis(string value) => value.Length > 0 ? value : "…";

    // ---- 原始 JSON 讀取 ----

    private static List<JsonElement> ArrayItems(JsonElement element, string name)
    {
        var items = new List<JsonElement>();
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var array)
            && array.ValueKind == JsonValueKind.Array)
        {
            items.AddRange(array.EnumerateArray());
        }

        return items;
    }

    private static IReadOnlyList<string> StringArrayItems(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString() ?? string.Empty)
            .ToArray();
    }

    /// <summary>
    /// 讀屬性為顯示字串（前端 draft 值一律為字串，故字串直取；數字/布林容錯轉字面，
    /// 缺欄回空字串——空字串在此扮演前端「falsy」角色，numRange/dateRange 據此判定是否有值）。
    /// </summary>
    private static string Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
    }
}
