using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 把已儲存篩選情境的條件樹寫成中文的條件說明，例如「A 且（B 或 C）」。輸入是前端送出的原始條件 JSON，
/// 輸出的文字和前端藍色的條件讀回區相同（filter-step-rules.js <c>readBackHtml</c> 的 <c>.scenario-readback__expr</c> 文字內容）。
/// 條件篩選報告摘要的「條件內容」就是這裡的輸出，由 <see cref="ExportCriteriaSelectionReportHandler"/> 計算，
/// 經 <see cref="CriteriaSelectionReportContext.ScenarioConditionLogic"/> 傳給寫報告的程式。
///
/// 這裡讀原始 JSON，不讀 <see cref="FilterScenarioSpec"/>。讀回要顯示使用者輸入的原文，例如 numRange 的 from 與 to；
/// 解析成 <see cref="FilterScenarioSpec"/> 之後金額已經換算成整數，原文就不在了，所以只有讀原始 JSON 才能和前端寫得一樣。
/// 文字條件的關鍵字照前端的寫法拆開、去掉重複再接回，見 <c>TextKeywords</c>。讀 JSON 的程式和 <see cref="FilterScenarioPayloadParser"/>
/// 一樣放在 Application；中文標籤只定義在 Domain 的 <see cref="FilterConditionLabels"/>，前端另有一份相同的文字，由測試核對兩邊一致。
///
/// 非營業日卡片保留子條件組，內含排除補班日的日期條件。已存情境裡用兩條預篩選組成的「週末過帳 或 假日過帳」
/// 照條件樹原樣描述，不換成非營業日卡片的寫法，也不搬到其他位置；本類只影響顯示文字。
///
/// 欄位名稱照原樣寫，不加任何符號，值用「」。畫面與文件（底稿與報告，<c>forDocument</c>）的寫法只差在統計條件：
/// 畫面上的條件讀回、滑鼠提示與情境的自動名稱用「」包住欄位名稱，底稿與報告不加符號，欄位名稱前後各留一個半形空白。
/// </summary>
public static class FilterConditionRenderer
{
    public static string Render(JsonElement scenario) => Render(scenario, null);

    /// <summary>
    /// <paramref name="categoryLabels"/> 是目前案件科目分類的 categoryId 與顯示名稱的對照，
    /// 科目配對條件讀回所選的分類時使用。省略時只認得內建分類的預設名稱，認不得的分類寫「已刪除的分類，請重新選擇」；
    /// 正式報表一律傳入案件的科目分類，才會反映使用者改過的顯示名稱。
    /// <paramref name="rdeFieldLabels"/> 是已確認配對裡攸關資料元素欄位的 fieldId 與顯示名稱的對照，
    /// <c>typed</c> 條件與欄位值條件讀回時使用。用的是目前的名稱，所以欄位只改了名稱時會顯示新名稱；
    /// 省略或欄位已不存在時寫「已不在欄位配對中的攸關資料元素欄位」，不印 fieldId。
    /// <paramref name="accountCategories"/> 帶完整分類用途與階層，才能列出實際納入分類。省略時不猜分類範圍。
    /// <paramref name="preparationDate"/> 是目前案件的期末財報準備日，只用在讀回的說明文字，不存進條件。
    /// <paramref name="zerosThreshold"/> 是「分錄金額中有連續 0 的尾數」由 GL 算出的 0 的個數，條件寫成「金額尾數連續 N 個 0」；
    /// 省略時寫規則名稱。上游修改會清掉已存情境，所以已存情境讀回時用目前的個數就是儲存時的個數。
    /// <paramref name="forDocument"/> 為真時是底稿與報告用的寫法：統計條件的欄位名稱不用「」包住，前後各留一個半形空白。
    /// <paramref name="neverBlankGlFields"/> 是 GL 中每一筆都有值的欄位；這些欄位的欄位值條件不寫空白怎麼處理。
    /// 省略時不知道案件的配對，每一條都照條件裡存的設定寫。
    /// </summary>
    public static string Render(
        JsonElement scenario,
        IReadOnlyDictionary<string, string>? categoryLabels,
        IReadOnlyDictionary<string, string>? rdeFieldLabels = null,
        IReadOnlyList<AccountCategory>? accountCategories = null,
        string? preparationDate = null, string? periodStart = null, string? periodEnd = null, bool compact = false,
        bool forDocument = false, int? zerosThreshold = null, IReadOnlyCollection<string>? neverBlankGlFields = null)
    {
        var editable = ArrayItems(scenario, "groups");

        // 只看裡面有條件的條件組。前端送出時已經去掉空的條件組，這裡再濾一次。
        var ne = editable.Where(g => ArrayItems(g, "conditions").Count > 0).ToList();

        var expr = string.Empty;
        if (ne.Count == 1)
        {
            expr = GroupExpression(ne[0], categoryLabels, rdeFieldLabels, accountCategories, preparationDate, periodStart, periodEnd, compact, forDocument, zerosThreshold, neverBlankGlFields).Text;
        }
        else if (ne.Count >= 2)
        {
            var sop = ScenarioJoin(ne); // 空的條件組不會送出，所以不算一個運算元，也不帶連接詞。
            var parts = ne.Select(g =>
            {
                var rendered = GroupExpression(g, categoryLabels, rdeFieldLabels, accountCategories, preparationDate, periodStart, periodEnd, compact, forDocument, zerosThreshold, neverBlankGlFields);
                return rendered.AtomCount > 1 && !rendered.IsExactLeftFold
                    ? "（" + rendered.Text + "）"
                    : rendered.Text; // 組內同時有「且」和「或」時，文字已經逐條加好括號；只有一種連接詞而且不只一條時，外面加一層括號
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

    /// <summary>已確認的 GL 配對下，GL 中每一筆都有值的欄位；還沒有配對時是 null，讀回照條件裡存的設定寫。</summary>
    internal static IReadOnlyList<string>? NeverBlankGlFields(CommittedMapping? glMapping) =>
        glMapping is null ? null : JetFieldCatalog.NeverBlankGlFields(glMapping.Mapping, glMapping.GlOptions);

    /// <summary>
    /// 單一條件的讀回文字，等同前端 <c>ruleSummaryLabel(rule, 0, compact)</c>。自動名稱逐條取條件說明時用，只有畫面寫法。
    /// </summary>
    internal static string RenderCondition(
        JsonElement rule,
        IReadOnlyDictionary<string, string>? categoryLabels,
        IReadOnlyDictionary<string, string>? rdeFieldLabels,
        IReadOnlyList<AccountCategory>? accountCategories,
        string? preparationDate, string? periodStart, string? periodEnd, bool compact, int? zerosThreshold = null,
        IReadOnlyCollection<string>? neverBlankGlFields = null) =>
        ConditionAtom(rule, categoryLabels, rdeFieldLabels, accountCategories, preparationDate, periodStart, periodEnd, compact,
            forDocument: false, zerosThreshold: zerosThreshold, neverBlankGlFields: neverBlankGlFields);

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
    /// 一個條件組的文字。前端會把同一組裡的連接詞整理成同一種，所以組內只有一種連接詞時，
    /// 照 <see cref="GroupCombinator"/> 用那一種連接詞把各條串起來。已儲存的條件裡，第一條之後的連接詞
    /// 同時有 AND 與 OR 時，才和 SQL 一樣由左到右逐條結合，每結合一條加一層全形括號。
    /// </summary>
    private static GroupRendering GroupExpression(
        JsonElement group,
        IReadOnlyDictionary<string, string>? categoryLabels,
        IReadOnlyDictionary<string, string>? rdeFieldLabels,
        IReadOnlyList<AccountCategory>? accountCategories,
        string? preparationDate, string? periodStart, string? periodEnd, bool compact, bool forDocument,
        int? zerosThreshold, IReadOnlyCollection<string>? neverBlankGlFields)
    {
        var rules = ArrayItems(group, "conditions");
        var atoms = rules.Select(rule => ConditionAtom(rule, categoryLabels, rdeFieldLabels, accountCategories, preparationDate, periodStart, periodEnd, compact, forDocument, zerosThreshold, neverBlankGlFields)).ToList();
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

    // 一條條件實際的連接詞：join 去掉前後空白後是 OR（不分大小寫）就是「或」，其他一律是「且」。
    // 後端解析條件時也不分大小寫。組內、條件組之間與子條件組的連接詞都用這個判斷。
    private static string EffectiveConditionJoin(JsonElement rule) =>
        string.Equals(
            Str(rule, "join")?.Trim(),
            "OR",
            StringComparison.OrdinalIgnoreCase)
            ? "OR"
            : "AND";

    /// <summary>條件組之間的連接詞：取第二個非空條件組的 join。第一組左邊沒有東西可以連接，它的 join 不用。</summary>
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
    /// 辨認舊情境裡「週末或假日」的寫法：子條件組（type 為 "group"）剛好有兩條子條件，兩條都是預篩選，
    /// 鍵是 weekendPosting 與 holidayPosting 各一，而且第二條的連接詞是「或」。第一條左邊沒有條件，它的 join 不看。
    /// 符合時讀回「（週末過帳 或 假日過帳）」，意思仍是週末或假日，不表示已排除補班日。
    /// 「週末 且 假日」的意思不同，照一般括號讀回。前端 filter-step-rules.js 的 isLegacyWeekendHolidayBracket 是同一個判斷。
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

    // ---- 單一條件的文字（等同前端 ruleSummaryLabel(rule, 0)；index 傳 0 表示前面不加 (AND) 或 (OR)）----

    private static string ConditionAtom(
        JsonElement r,
        IReadOnlyDictionary<string, string>? categoryLabels,
        IReadOnlyDictionary<string, string>? rdeFieldLabels,
        IReadOnlyList<AccountCategory>? accountCategories,
        string? preparationDate, string? periodStart, string? periodEnd, bool compact, bool forDocument,
        int? zerosThreshold, IReadOnlyCollection<string>? neverBlankGlFields)
    {
        var type = Str(r, "type");
        var text = type switch
        {
            "group" when IsNonBusinessDayBracket(r) => FilterConditionLabels.NonBusinessDayAtom,
            "group" when IsWeekendOrHolidayBracket(r) => "（"
                + FilterConditionLabels.PrescreenLabel(PrescreenRuleKeys.WeekendPosting) + " 或 "
                + FilterConditionLabels.PrescreenLabel(PrescreenRuleKeys.HolidayPosting) + "）",
            "group" => NestedAtom(r, categoryLabels, rdeFieldLabels, accountCategories, preparationDate, periodStart, periodEnd, compact, forDocument, zerosThreshold, neverBlankGlFields),
            "voucher" => NestedAtom(r, categoryLabels, rdeFieldLabels, accountCategories, preparationDate, periodStart, periodEnd, compact, forDocument, zerosThreshold, neverBlankGlFields),
            "prescreen" => PrescreenAtom(Str(r, "prescreenKey"), preparationDate, zerosThreshold),
            "text" => GlFieldName(Str(r, "field")) + " "
                + FilterConditionLabels.TextModeLabel(Str(r, "mode")) + "「" + TextKeywords(r) + "」",
            "textSet" => TextSetAtom(r),
            "dateRange" => GlFieldName(Str(r, "field")) + " "
                + Ellipsis(Str(r, "from")) + "～" + Ellipsis(Str(r, "to")),
            "numRange" => NumRangeAtom(r),
            "drCrOnly" => Str(r, "drCr") == "credit" ? "僅貸方" : "僅借方",
            "manualAuto" => Str(r, "isManual") == "false" ? "自動分錄" : "人工分錄",
            "accountPair" => AccountPairAtom(r, categoryLabels),
            "specialAccountCategoryPair" => FilterConditionLabels.AccountCombinationPrefix
                + FilterConditionLabels.SpecialPairModeLabel(Str(r, "pairMode")) + "（借方 "
                + CategorySelection(r, "debitCategoryIds", categoryLabels) + "，貸方 "
                + CategorySelection(r, "creditCategoryIds", categoryLabels) + "）",
            "entityFrequency" => "GL 中" + FrequencyFieldName(JetFieldCatalog.GlSemanticFieldMappingLabel(Str(r, "field")), forDocument) +
                (Str(r, "countUnit") == "vouchers" ? "傳票張數（同號只算一張）" : "分錄筆數") + " " +
                (EntityFrequencyConditions.Operators.TryGetValue(Str(r, "countOperator"), out var countLabel) ? countLabel : "") + " " +
                Str(r, "countFrom") + (Str(r, "countOperator") == "between" ? "～" + Str(r, "countTo") + "（含端點）" : ""),
            "revenueDebitNearQuarterEnd" => QuarterEndAtom(r, periodStart, periodEnd),
            "revenueWithoutNormalCounterpart" => "貸方為收入但借方非一般對方科目",
            "manualRevenueEntry" => "收入之人工分錄",
            "preparerEqualsApprover" => "編製人員與核准人員相同",
            "typed" => TypedFieldAtom(r, rdeFieldLabels),
            "fieldValue" => FieldValueAtom(r, rdeFieldLabels, compact, neverBlankGlFields),
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
        int? zerosThreshold, IReadOnlyCollection<string>? neverBlankGlFields)
    {
        var children = ArrayItems(rule, "conditions");
        var text = string.Empty;
        foreach (var child in children)
        {
            var atom = ConditionAtom(child, categories, fields, accountCategories, preparationDate, periodStart, periodEnd, compact, forDocument, zerosThreshold, neverBlankGlFields);
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

    private static string FieldValueAtom(JsonElement rule, IReadOnlyDictionary<string, string>? labels, bool compact,
        IReadOnlyCollection<string>? neverBlankGlFields)
    {
        var fieldId = Str(rule, "fieldId");
        var glKey = Str(rule, "field");
        // 欄位已不存在時寫替代的說明文字，後面不接金額比較基準；前端 FilterValues.summary 的寫法相同。
        var fieldKnown = fieldId.Length == 0
            ? glKey == "amount" || FilterConditionLabels.GlFields.ContainsKey(glKey)
            : labels is not null && labels.ContainsKey(fieldId);
        var field = fieldId.Length > 0
            ? (fieldKnown ? labels![fieldId] : FilterConditionLabels.MissingRdeField)
            // 金額欄不在 GlFields 的文字與日期欄位名稱表裡，名稱直接寫「金額」。
            : glKey == "amount" ? "金額" : GlFieldName(glKey);
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
        // GL 中每一筆都有值的欄位沒有空白可列入或不列入，不寫這一句；前端 FilterValues.summary 的寫法相同。
        var neverBlank = fieldId.Length == 0 && neverBlankGlFields is not null && neverBlankGlFields.Contains(glKey, StringComparer.Ordinal);
        var blankText = op is "isBlank" or "isNotBlank" || neverBlank ? "" : includeBlank ? "；空白也符合" : compact ? "" : "；空白不列入";
        var unit = FieldValueConditions.IsMonthWindow(op) ? " 天" : FieldValueConditions.IsZeroRun(op) ? "個 0" : "";
        return field + " " + opLabel + operand + unit + blankText;
    }

    /// <summary>
    /// <c>typed</c> 條件的讀回文字。攸關資料元素欄位用目前欄位配對裡的顯示名稱，所以欄位只改了名稱時，讀回會跟著顯示新名稱。
    /// 欄位已不存在或沒有提供名稱對照時寫「已不在欄位配對中的攸關資料元素欄位」，後面不接比較基準。
    /// 比較的值照條件裡存的原始字串寫出：單一值讀 value，區間讀 from 與 to，清單讀 values。金額條件依 amountBasis 加註比較基準。
    /// 運算子的中文名稱定義在 Domain 的 <see cref="FilterConditionLabels.TypedOperators"/>。
    /// </summary>
    private static string TypedFieldAtom(
        JsonElement r,
        IReadOnlyDictionary<string, string>? rdeFieldLabels)
    {
        var fieldId = Str(r, "fieldId");
        var fieldKnown = rdeFieldLabels is not null && rdeFieldLabels.ContainsKey(fieldId);
        var fieldLabel = fieldKnown ? rdeFieldLabels![fieldId] : FilterConditionLabels.MissingRdeField;
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
        return GlFieldName(Str(r, "field"))
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
    /// 借方或貸方其中一側所選分類的讀回：帶 ID 陣列時逐一換成科目分類的顯示名稱，照使用者選取的順序串接；
    /// 分隔字元定義在 Domain 的 <see cref="FilterConditionLabels.CategoryListSeparator"/>。
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

    /// <summary>
    /// 統計條件的欄位名稱。畫面用「」包住，和前後的文字分開；底稿與報告的欄位名稱不加任何符號，
    /// 審計員才不會把符號當成欄位名稱的一部分，所以改用前後各一個半形空白分開。
    /// </summary>
    private static string FrequencyFieldName(string name, bool forDocument) =>
        forDocument ? " " + name + " " : "「" + name + "」";

    /// <summary>GL 欄位的名稱：認得的欄位寫讀回用的名稱；認不得的寫 <see cref="FilterConditionLabels.MissingGlField"/>。</summary>
    private static string GlFieldName(string key) =>
        FilterConditionLabels.GlFields.ContainsKey(key)
            ? FilterConditionLabels.GlFieldReadback(key)
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
    /// 把屬性讀成顯示用的字串。前端草稿的值都是字串，直接取用；遇到數字或布林時轉成它的字面寫法；
    /// 沒有這個屬性時回空字串。空字串在這裡代表沒有值，numRange 與 dateRange 用它判斷有沒有填。
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
