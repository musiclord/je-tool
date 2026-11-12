using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 已存篩選情境的條件 AST（前端送出的 wire JSON）→ 中文布林式，與前端藍色 read-back
/// （filter-step-rules.js <c>readBackHtml</c> 的 <c>.scenario-readback__expr</c> 文字內容）同構。
/// CriteriaSelection summary 的「條件內容」以本渲染器輸出為值（由
/// <see cref="ExportCriteriaSelectionReportHandler"/> 計算，經
/// <see cref="CriteriaSelectionReportContext.ScenarioConditionLogic"/> 傳入 writer）。
///
/// 為什麼吃「原始 JSON」而非 <see cref="FilterScenarioSpec"/>：read-back 顯示的是使用者輸入的**顯示字串**
/// （numRange 的 from/to 原文），而解析成 spec 後金額已 scaled，顯示原文遺失；要真正同構就必須讀原始 wire。
/// 文字條件的關鍵字照前端的寫法拆開、去掉重複再接回，見 <c>TextKeywords</c>。層級：JSON 形狀處理與 <see cref="FilterScenarioPayloadParser"/>
/// 同屬 Application；中文標籤的唯一正本在 Domain <see cref="FilterConditionLabels"/>（前端鏡像、測試守衛）。
///
/// 非營業日卡片保留子條件組，內含排除補班日的日期條件。舊版「週末過帳 或 假日過帳」
/// 仍按原樹描述，不改成新版定義，也不搬到其他位置；本類只影響顯示文字。
///
/// 同一段文字有畫面與文件兩種寫法：底稿與報告（<c>forDocument</c>）把欄位名稱用【】標示，值照舊用「」；
/// 畫面上的條件讀回、滑鼠提示與情境的自動名稱不加。認不得的欄位寫的是替代句，不是欄位名稱，不加【】。
/// </summary>
public static class FilterConditionRenderer
{
    public static string Render(JsonElement scenario) => Render(scenario, null);

    /// <summary>
    /// <paramref name="categoryLabels"/> 是目前專案科目分類的 categoryId → 顯示 label 對照，
    /// 供科目配對多選讀回。省略時只還原內建分類的預設 label，未知身分提示重新選擇；正式報表一律
    /// 傳入專案的科目分類，才會反映使用者改過的顯示名稱。
    /// <paramref name="rdeFieldLabels"/> 是目前 committed RDE 欄位的 fieldId → 顯示 label 對照，
    /// 供 typed 條件讀回（renderer 用目前 metadata，僅 label 改名時顯示新名稱）；
    /// 省略或欄位已不存在時寫「已不在欄位配對中的攸關資料元素欄位」，不印 fieldId。
    /// <paramref name="accountCategories"/> 帶完整分類用途與階層，才能列出實際納入分類。省略時不猜分類範圍。
    /// <paramref name="preparationDate"/> 是目前案件的期末財報準備日，只供讀回說明，不寫入條件 AST。
    /// <paramref name="zerosThreshold"/> 是「分錄金額中有連續 0 的尾數」由 GL 算出的 0 的個數，條件寫成「金額尾數連續 N 個 0」；
    /// 省略時寫規則名稱。上游修改會清掉已存情境，所以已存情境讀回時用目前的個數就是儲存時的個數。
    /// <paramref name="forDocument"/> 為真時是底稿與報告用的寫法，欄位名稱加【】。
    /// </summary>
    public static string Render(
        JsonElement scenario,
        IReadOnlyDictionary<string, string>? categoryLabels,
        IReadOnlyDictionary<string, string>? rdeFieldLabels = null,
        IReadOnlyList<AccountCategory>? accountCategories = null,
        string? preparationDate = null, string? periodStart = null, string? periodEnd = null, bool compact = false,
        bool forDocument = false, int? zerosThreshold = null)
    {
        var editable = ArrayItems(scenario, "groups");

        // read-back 的 `ne`：只看「有規則」的可編輯組（wire 已濾空組，此處再守一次亦無害）。
        var ne = editable.Where(g => ArrayItems(g, "conditions").Count > 0).ToList();

        var expr = string.Empty;
        if (ne.Count == 1)
        {
            expr = GroupExpression(ne[0], categoryLabels, rdeFieldLabels, accountCategories, preparationDate, periodStart, periodEnd, compact, forDocument, zerosThreshold).Text;
        }
        else if (ne.Count >= 2)
        {
            var sop = ScenarioJoin(ne); // Empty editor groups do not contribute an operand or a join to the wire.
            var parts = ne.Select(g =>
            {
                var rendered = GroupExpression(g, categoryLabels, rdeFieldLabels, accountCategories, preparationDate, periodStart, periodEnd, compact, forDocument, zerosThreshold);
                return rendered.AtomCount > 1 && !rendered.IsExactLeftFold
                    ? "（" + rendered.Text + "）"
                    : rendered.Text; // mixed 已逐邊累積精確括號；uniform 多原子組維持既有單層括號
            }).ToArray();
            if (HasMixedEffectiveConditionJoins(ne))
            {
                expr = parts[0];
                for (var index = 1; index < parts.Length; index++)
                    expr = "（" + expr + " " + JoinLabel(EffectiveConditionJoin(ne[index])) + " " + parts[index] + "）";
            }
            else expr = string.Join(" " + JoinLabel(sop) + " ", parts);
        }

        return expr;
    }

    /// <summary>
    /// 單一條件的讀回文字，等同前端 <c>ruleSummaryLabel(rule, 0, compact)</c>。自動名稱逐條取條件說明時用，只有畫面寫法。
    /// </summary>
    internal static string RenderCondition(
        JsonElement rule,
        IReadOnlyDictionary<string, string>? categoryLabels,
        IReadOnlyDictionary<string, string>? rdeFieldLabels,
        IReadOnlyList<AccountCategory>? accountCategories,
        string? preparationDate, string? periodStart, string? periodEnd, bool compact, int? zerosThreshold = null) =>
        ConditionAtom(rule, categoryLabels, rdeFieldLabels, accountCategories, preparationDate, periodStart, periodEnd, compact,
            forDocument: false, zerosThreshold: zerosThreshold);

    /// <summary>欄位值條件選的欄位名稱，等同前端 <c>FilterValues.field(rule).label</c>；欄位不在可選清單時是 null。</summary>
    internal static string? FieldValueFieldLabel(JsonElement rule, IReadOnlyDictionary<string, string>? rdeFieldLabels)
    {
        var fieldId = Str(rule, "fieldId");
        if (fieldId.Length > 0)
        {
            return rdeFieldLabels is not null && rdeFieldLabels.TryGetValue(fieldId, out var label) ? label : null;
        }

        var glKey = Str(rule, "field");
        return glKey == "amount" ? "金額"
            : FilterConditionLabels.GlFields.ContainsKey(glKey) ? FilterConditionLabels.GlFieldReadback(glKey)
            : null;
    }

    // ---- 群組層 ----

    /// <summary>只讀第一條之後的有效結合，大小寫規則與後端相同。</summary>
    private static string GroupCombinator(JsonElement group) =>
        ArrayItems(group, "conditions").Skip(1).Any(r => EffectiveConditionJoin(r) == "OR") ? "OR" : "AND";

    /// <summary>
    /// 既有主前端會把組內 join 收斂為單一運算子，所以 uniform 組仍沿用原來的
    /// <see cref="GroupCombinator"/> 呈現。只有已落地 AST 的有效邊（第一條規則之後）
    /// 同時含 AND 與 OR 時，才依 SQL 相同的左折疊逐邊加全形括號。
    /// </summary>
    private static GroupRendering GroupExpression(
        JsonElement group,
        IReadOnlyDictionary<string, string>? categoryLabels,
        IReadOnlyDictionary<string, string>? rdeFieldLabels,
        IReadOnlyList<AccountCategory>? accountCategories,
        string? preparationDate, string? periodStart, string? periodEnd, bool compact, bool forDocument,
        int? zerosThreshold)
    {
        var rules = ArrayItems(group, "conditions");
        var atoms = rules.Select(rule => ConditionAtom(rule, categoryLabels, rdeFieldLabels, accountCategories, preparationDate, periodStart, periodEnd, compact, forDocument, zerosThreshold)).ToList();
        if (!HasMixedEffectiveConditionJoins(rules))
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
                + JoinLabel(EffectiveConditionJoin(rules[index]))
                + " "
                + atoms[index]
                + "）";
        }

        return new GroupRendering(expression, atoms.Count, IsExactLeftFold: true);
    }


    private static bool HasMixedEffectiveConditionJoins(IReadOnlyList<JsonElement> rules)
    {
        var hasAnd = false;
        var hasOr = false;
        for (var index = 1; index < rules.Count; index++)
        {
            if (EffectiveConditionJoin(rules[index]) == "OR")
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
    private static string EffectiveConditionJoin(JsonElement rule) =>
        string.Equals(
            Str(rule, "join")?.Trim(),
            "OR",
            StringComparison.OrdinalIgnoreCase)
            ? "OR"
            : "AND";

    /// <summary>有效組間運算子：第二個非空組的 join；第一組 join 不參與左折疊語意。</summary>
    private static string ScenarioJoin(IReadOnlyList<JsonElement> editable) =>
        editable.Count >= 2 ? EffectiveConditionJoin(editable[1]) : "OR";

    private static string JoinAtoms(IReadOnlyList<string> atoms, string op) =>
        string.Join(" " + JoinLabel(op) + " ", atoms);

    private static string JoinLabel(string op) => op == "OR" ? "或" : "且";

    private readonly record struct GroupRendering(
        string Text,
        int AtomCount,
        bool IsExactLeftFold);

    /// <summary>
    /// 舊週末或假日括號辨識：子條件組（type "group"）恰有兩條子條件，都是預篩選，鍵集合恰為
    /// {weekendPosting, holidayPosting}，且第二條的有效連接是「或」（第一條的 join 沒有左側條件，不算）。
    /// 符合時語意仍是「週末或假日」，不聲稱已排除補班日；
    /// 「週末 且 假日」語意不同，照一般括號讀回。前端 filter-step-rules.js 的 isNonBusinessDayBracket 逐字鏡像。
    /// </summary>
    private static bool IsWeekendOrHolidayBracket(JsonElement rule)
    {
        if (Str(rule, "type") != "group")
        {
            return false;
        }

        var children = ArrayItems(rule, "conditions");
        if (children.Count != 2 || EffectiveConditionJoin(children[1]) != "OR")
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

    // 舊情境裡由非營業日卡片加入的子條件組，只有一條排除補班日的日期條件。卡片現在直接加入那一條條件，
    // 已儲存情境裡的這種子條件組照舊讀回「非營業日」。
    private static bool IsNonBusinessDayBracket(JsonElement rule)
    {
        if (Str(rule, "type") != "group") return false;
        var children = ArrayItems(rule, "conditions");
        if (children.Count != 1) return false;
        var child = children[0];
        return Str(child, "type") == "fieldValue" && Str(child, "field") == "glDate"
            && Str(child, "fieldId").Length == 0 && Str(child, "operator") == "isNonBusinessDay"
            && Str(child, "drCr").Length == 0 && Str(child, "includeBlank") != "true";
    }

    // ---- 規則原子（等同前端 ruleSummaryLabel(rule, 0)：index 0＝不加 (AND)/(OR) 前綴）----

    private static string ConditionAtom(
        JsonElement r,
        IReadOnlyDictionary<string, string>? categoryLabels,
        IReadOnlyDictionary<string, string>? rdeFieldLabels,
        IReadOnlyList<AccountCategory>? accountCategories,
        string? preparationDate, string? periodStart, string? periodEnd, bool compact, bool forDocument,
        int? zerosThreshold)
    {
        var type = Str(r, "type");
        var text = type switch
        {
            "group" when IsNonBusinessDayBracket(r) => FilterConditionLabels.NonBusinessDayAtom,
            "group" when IsWeekendOrHolidayBracket(r) => "（"
                + FilterConditionLabels.PrescreenLabel(PrescreenRuleKeys.WeekendPosting) + " 或 "
                + FilterConditionLabels.PrescreenLabel(PrescreenRuleKeys.HolidayPosting) + "）",
            "group" => NestedAtom(r, categoryLabels, rdeFieldLabels, accountCategories, preparationDate, periodStart, periodEnd, compact, forDocument, zerosThreshold),
            "voucher" => NestedAtom(r, categoryLabels, rdeFieldLabels, accountCategories, preparationDate, periodStart, periodEnd, compact, forDocument, zerosThreshold),
            "prescreen" => PrescreenAtom(Str(r, "prescreenKey"), preparationDate, zerosThreshold),
            "text" => GlFieldName(Str(r, "field"), forDocument) + " "
                + FilterConditionLabels.TextModeLabel(Str(r, "mode")) + "「" + TextKeywords(r) + "」",
            "textSet" => TextSetAtom(r, forDocument),
            "dateRange" => GlFieldName(Str(r, "field"), forDocument) + " "
                + Ellipsis(Str(r, "from")) + "～" + Ellipsis(Str(r, "to")),
            "numRange" => NumRangeAtom(r, forDocument),
            "drCrOnly" => Str(r, "drCr") == "credit" ? "僅貸方" : "僅借方",
            "manualAuto" => Str(r, "isManual") == "false" ? "自動分錄" : "人工分錄",
            "accountPair" => AccountPairAtom(r, categoryLabels),
            "specialAccountCategoryPair" => FilterConditionLabels.AccountCombinationPrefix
                + FilterConditionLabels.SpecialPairModeLabel(Str(r, "pairMode")) + "（借方 "
                + CategorySelection(r, "debitCategoryIds", categoryLabels) + "，貸方 "
                + CategorySelection(r, "creditCategoryIds", categoryLabels) + "）",
            "entityFrequency" => "GL 中" + QuotedFieldName(JetFieldCatalog.GlSemanticFieldMappingLabel(Str(r, "field")), forDocument) +
                (Str(r, "countUnit") == "vouchers" ? "傳票張數（同號只算一張）" : "分錄筆數") + " " +
                (EntityFrequencyConditions.Operators.TryGetValue(Str(r, "countOperator"), out var countLabel) ? countLabel : "") + " " +
                Str(r, "countFrom") + (Str(r, "countOperator") == "between" ? "～" + Str(r, "countTo") + "（含端點）" : ""),
            "revenueDebitNearQuarterEnd" => QuarterEndAtom(r, periodStart, periodEnd),
            "revenueWithoutNormalCounterpart" => "貸方為收入但借方非一般對方科目",
            "manualRevenueEntry" => "收入之人工分錄",
            "preparerEqualsApprover" => "編製人員與核准人員相同",
            "typed" => TypedFieldAtom(r, rdeFieldLabels, forDocument),
            "fieldValue" => FieldValueAtom(r, rdeFieldLabels, compact, forDocument),
            "accountSide" => AccountSideAtom(r, categoryLabels, accountCategories),
            _ => FilterConditionLabels.RetiredCondition,
        };
        if (type is not ("accountPair" or "specialAccountCategoryPair")) return text;
        string[] sides = type == "accountPair" && Str(r, "pairMode") == AccountPairModes.CreditAnchor ? ["creditCategoryIds"]
            : type == "accountPair" && Str(r, "pairMode") == AccountPairModes.DebitAnchor ? ["debitCategoryIds"]
            : ["debitCategoryIds", "creditCategoryIds"];
        if (!CategorySelectionMatters(r, accountCategories, sides)) return text;
        text = CategorySelectionModeLabel(r) + "：" + text;
        if (accountCategories is null) return text;
        if (type != "accountPair" || Str(r, "pairMode") != AccountPairModes.CreditAnchor)
            text += "；借方實際納入分類：" + ExpandedCategoryLabels(r, "debitCategoryIds", accountCategories);
        if (type != "accountPair" || Str(r, "pairMode") != AccountPairModes.DebitAnchor)
            text += "；貸方實際納入分類：" + ExpandedCategoryLabels(r, "creditCategoryIds", accountCategories);
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

    private static string PrescreenAtom(string key, string? preparationDate, int? zerosThreshold)
    {
        var text = "預篩選：" + FilterConditionLabels.PrescreenLabel(key, zerosThreshold);
        return key == PrescreenRuleKeys.PrepPeriodApproval && !string.IsNullOrWhiteSpace(preparationDate)
            ? text + "（" + preparationDate + " 起，含當日）"
            : text;
    }

    private static string AccountSideAtom(JsonElement rule, IReadOnlyDictionary<string, string>? labels,
        IReadOnlyList<AccountCategory>? accountCategories)
    {
        var categories = CategorySelection(rule, "categoryIds", labels);
        var text = Str(rule, "categoryMode") switch
        {
            "is" => "科目屬於「" + categories + "」",
            "isNot" => "科目不屬於「" + categories + "」",
            _ => "尚未選擇分類條件"
        };
        if (!CategorySelectionMatters(rule, accountCategories, "categoryIds")) return text;
        text += "（" + CategorySelectionModeLabel(rule) + "）";
        return accountCategories is null ? text
            : text + "；實際納入分類：" + ExpandedCategoryLabels(rule, "categoryIds", accountCategories);
    }

    private static string NestedAtom(JsonElement rule, IReadOnlyDictionary<string, string>? categories,
        IReadOnlyDictionary<string, string>? fields, IReadOnlyList<AccountCategory>? accountCategories,
        string? preparationDate, string? periodStart, string? periodEnd, bool compact, bool forDocument,
        int? zerosThreshold)
    {
        var children = ArrayItems(rule, "conditions");
        var text = string.Empty;
        foreach (var child in children)
        {
            var atom = ConditionAtom(child, categories, fields, accountCategories, preparationDate, periodStart, periodEnd, compact, forDocument, zerosThreshold);
            text = text.Length == 0 ? atom : "（" + text + " " + JoinLabel(EffectiveConditionJoin(child)) + " " + atom + "）";
        }
        if (Str(rule, "type") == "group")
        {
            var scope = children.Count > 0 && children.All(IsExplicitVoucherCondition) ? "同張傳票"
                : children.Any(ContainsExplicitVoucherCondition) ? "條件組合" : "同一分錄";
            return scope + "（" + text + "）";
        }
        var quantifier = Str(rule, "quantifier") switch { "all" => "全部符合（至少有一筆）", "none" => "不存在符合", _ => "至少一筆符合" };
        return "整張傳票" + quantifier + "：" + (children.Count == 1 ? text : "同一分錄（" + text + "）");
    }

    private static bool IsExplicitVoucherCondition(JsonElement rule) => Str(rule, "type") == "voucher"
        || Str(rule, "type") == "group" && ArrayItems(rule, "conditions") is { Count: > 0 } children && children.All(IsExplicitVoucherCondition);

    private static bool ContainsExplicitVoucherCondition(JsonElement rule) => Str(rule, "type") == "voucher"
        || ArrayItems(rule, "conditions").Any(ContainsExplicitVoucherCondition);

    private static string FieldValueAtom(JsonElement rule, IReadOnlyDictionary<string, string>? labels, bool compact, bool forDocument)
    {
        var fieldId = Str(rule, "fieldId");
        var glKey = Str(rule, "field");
        // 欄位已不存在時寫畫面名稱、不接金額比較基準；前端 FilterValues.summary 同構。
        var fieldKnown = fieldId.Length == 0
            ? glKey == "amount" || FilterConditionLabels.GlFields.ContainsKey(glKey)
            : labels is not null && labels.ContainsKey(fieldId);
        var field = fieldId.Length > 0
            ? (fieldKnown ? FieldName(labels![fieldId], forDocument) : FilterConditionLabels.MissingRdeField)
            // Normalized filter amount has no text/date mapping label in GlFields.
            : glKey == "amount" ? FieldName("金額", forDocument) : GlFieldName(glKey, forDocument);
        var op = Str(rule, "operator");
        var opLabel = FieldValueConditions.Labels.TryGetValue(op, out var knownOp) ? knownOp
            : op.Length == 0 ? string.Empty : FilterConditionLabels.RetiredCondition;
        var listValues = StringArrayItems(rule, "values");
        var operand = op is "isBlank" or "isNotBlank" || FieldValueConditions.IsCalendar(op) ? "" : op is "between" or "notBetween"
            ? "「" + Str(rule, "from") + "」至「" + Str(rule, "to") + "」"
            : op is "in" or "notIn" or FieldValueConditions.DayOfMonthIn or FieldValueConditions.DayOfMonthNotIn
                ? "「" + string.Join("、", listValues) + "」"
            : (FieldValueConditions.IsContains(op) || FieldValueConditions.IsAffix(op)) && listValues.Count > 0 ? "「" + string.Join("、", listValues) + "」"
            : "「" + Str(rule, "value") + "」";
        // 尾數比對、尾數連續多個 0 與整數部分包含都忽略正負號，不接金額比較基準。
        if (fieldKnown && op is not "isBlank" and not "isNotBlank" && !FieldValueConditions.IsTail(op) && !FieldValueConditions.IsZeroRun(op)
            && !FieldValueConditions.IsDigitContains(op))
            field += Str(rule, "amountBasis") switch { "absolute" => "絕對值", "signed" => "含正負號", _ => "" };
        // 空白預設不列入，與 GlRulePredicates.FieldValue 的預設一致。
        var includeBlank = rule.TryGetProperty("includeBlank", out var blank) && blank.ValueKind == JsonValueKind.True;
        // 精簡讀回（自動名稱與動機用）只寫「空白也符合」，不寫預設的「空白不列入」，和前端 FilterValues.summary 的 compact 相同。
        var blankText = op is "isBlank" or "isNotBlank" ? "" : includeBlank ? "；空白也符合" : compact ? "" : "；空白不列入";
        var unit = FieldValueConditions.IsMonthWindow(op) ? " 天" : FieldValueConditions.IsZeroRun(op) ? "個 0" : "";
        return field + " " + opLabel + operand + unit + blankText;
    }

    /// <summary>
    /// typed 條件讀回：RDE 欄位以目前 metadata 的顯示 label 呈現（僅 label 改名時自動跟進）；
    /// 欄位已不存在或未提供對照時寫「已不在欄位配對中的攸關資料元素欄位」，不接比較基準；
    /// operand 依 carrier 呈現原始 wire 字串；amount 條件附 amountBasis 標示。標籤正本在 Domain <see cref="FilterConditionLabels.TypedOperators"/>。
    /// </summary>
    private static string TypedFieldAtom(
        JsonElement r,
        IReadOnlyDictionary<string, string>? rdeFieldLabels,
        bool forDocument)
    {
        var fieldId = Str(r, "fieldId");
        var fieldKnown = rdeFieldLabels is not null && rdeFieldLabels.ContainsKey(fieldId);
        var fieldLabel = fieldKnown ? FieldName(rdeFieldLabels![fieldId], forDocument) : FilterConditionLabels.MissingRdeField;
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

        var basis = !fieldKnown ? string.Empty : Str(r, "amountBasis") switch
        {
            TypedAmountBasisNames.Signed => "（" + FilterConditionLabels.TypedSignedAmountBasis + "）",
            TypedAmountBasisNames.Absolute => "（" + FilterConditionLabels.TypedAbsoluteAmountBasis + "）",
            _ => string.Empty
        };

        return fieldLabel + " " + opLabel + operand + basis;
    }

    /// <summary>
    /// 文字條件的關鍵字用比對時的同一套分隔字元拆開、去掉重複，再用半形逗號接回，
    /// 和前端讀回與送出的寫法相同，自動名稱與動機、報告與底稿的條件說明因此和畫面一致。
    /// </summary>
    private static string TextKeywords(JsonElement r) =>
        string.Join(",", FieldValueConditions.SplitInputList(Str(r, "keywords")).Distinct(StringComparer.Ordinal));

    private static string NumRangeAtom(JsonElement r, bool forDocument)
    {
        var from = Str(r, "from");
        var to = Str(r, "to");
        return FieldName("金額", forDocument) + "（絕對值） "
            + (from.Length > 0 ? "≥ " + from : string.Empty)
            + (from.Length > 0 && to.Length > 0 ? "、" : string.Empty)
            + (to.Length > 0 ? "≤ " + to : string.Empty);
    }

    private static string TextSetAtom(JsonElement r, bool forDocument)
    {
        var modeLabel = Str(r, "mode") == "exact"
            ? FilterConditionLabels.TextSetExactAny
            : FilterConditionLabels.TextSetContainsAny;
        var normalizationLabel = Str(r, "normalization") == "removeAsciiSpaces"
            ? FilterConditionLabels.TextSetRemoveAsciiSpaces
            : FilterConditionLabels.TextSetPreserveAsciiSpaces;
        return GlFieldName(Str(r, "field"), forDocument)
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
    /// 單側分類的讀回：帶 ID 陣列時逐一還原成科目分類的顯示 label，並依使用者選取順序串接
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

        var builtIn = AccountCategoryBuiltIns.All.SingleOrDefault(
            item => string.Equals(item.CategoryId, categoryId, StringComparison.Ordinal));
        return builtIn?.Label ?? "已刪除的分類，請重新選擇";
    }

    // 選的分類都沒有下層時，「包含下層分類」與「僅分類本身」選到的分類相同，讀回不寫選取依據與實際納入分類。新案件的分類都是
    // 平的一層，條件說明就和舊版一樣只寫分類；「相同分類用途」會納入同用途的其他分類，照舊寫出。
    // 沒有分類清單時當成沒有下層，和前端沒有分類設定時用內建分類一樣。前端 ui-core.js 的 categorySelectionMatters 是同一個規則。
    private static bool CategorySelectionMatters(JsonElement rule, IReadOnlyList<AccountCategory>? categories,
        params string[] idsProperties)
    {
        if (Str(rule, "categorySelection") is not ("subtree" or "node")) return true;
        if (categories is null) return false;
        var selected = idsProperties.SelectMany(property => StringArrayItems(rule, property)).ToHashSet(StringComparer.Ordinal);
        return categories.Any(category => category.ParentCategoryId is not null && selected.Contains(category.ParentCategoryId));
    }

    private static string CategorySelectionModeLabel(JsonElement rule) => Str(rule, "categorySelection") switch
    {
        "node" => "僅分類本身", "subtree" => "包含下層分類", _ => "相同分類用途"
    };

    private static string ExpandedCategoryLabels(JsonElement rule, string idsProperty,
        IReadOnlyList<AccountCategory> categories)
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

    /// <summary>空值以刪節號替代（同前端 <c>rule.from || '…'</c>）。</summary>
    private static string Ellipsis(string value) => value.Length > 0 ? value : "…";

    /// <summary>欄位名稱：底稿與報告用【】標示，畫面照原樣。</summary>
    private static string FieldName(string name, bool forDocument) => forDocument ? "【" + name + "】" : name;

    /// <summary>畫面上用「」包住的欄位名稱，在底稿與報告裡改成【】。</summary>
    private static string QuotedFieldName(string name, bool forDocument) =>
        forDocument ? "【" + name + "】" : "「" + name + "」";

    /// <summary>GL 欄位的名稱：認得的欄位照 <see cref="FieldName"/>；認不得的寫 <see cref="FilterConditionLabels.MissingGlField"/>，那不是欄位名稱，不加【】。</summary>
    private static string GlFieldName(string key, bool forDocument) =>
        FilterConditionLabels.GlFields.ContainsKey(key)
            ? FieldName(FilterConditionLabels.GlFieldReadback(key), forDocument)
            : FilterConditionLabels.MissingGlField;

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
