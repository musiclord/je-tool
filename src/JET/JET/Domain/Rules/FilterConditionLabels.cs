namespace JET.Domain;

/// <summary>
/// 進階篩選「條件中文標籤」只定義在這裡。前端 <c>ui-core.js</c> 的標籤常數照這張表寫，
/// <c>FilterConditionLabelMirrorTests</c> 用正規表示式讀 JS 檔，核對兩邊一致。
///
/// 為什麼要有這張表：CriteriaSelection summary 的「條件內容」由
/// <see cref="JET.Application.FilterConditionRenderer"/> 寫成和前端藍色讀回文字結構相同的中文布林式；
/// 兩邊各有一份標籤就會不一致，所以標籤只放在 Domain 這一份，前端由測試核對。這裡只放
/// 每個鍵的中文標籤與兩端共用的固定片語；括號、AND 與 OR 的組合，以及各型別的動態句型由 renderer 處理。
///
/// 覆蓋範圍＝條件型別顯示名，以及渲染器會用到的標籤表：預篩選鍵、科目配對模式、
/// 特殊科目配對模式、可篩選 GL 邏輯欄與非營業日原子。
/// <see cref="PrescreenKeys"/> 與 <see cref="PrescreenRuleKeys.FilterableKeys"/> 都由
/// <see cref="PrescreenRuleDefinitions"/> 衍生，鍵集合必然相同；新增逐筆規則時在登錄裡給中文標籤即可。
/// </summary>
public static class FilterConditionLabels
{
    /// <summary>
    /// 進階篩選 AST type wire key → 中文顯示名（前端 <c>FILTER_RULE_TYPES[].label</c> 鏡像）。
    /// 前端的分組與可用性 metadata 不屬本表。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RuleTypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["prescreen"] = "預篩選",
            ["drCrOnly"] = "借貸限定",
            ["manualAuto"] = "人工/自動分錄",
            ["accountPair"] = "借貸科目組合（看對方科目）",
            ["specialAccountCategoryPair"] = "借貸科目組合",
            ["entityFrequency"] = "科目與人員統計",
            ["group"] = "子條件組",
            ["voucher"] = "整張傳票條件",
            ["fieldValue"] = "欄位值比較",
            // 型別標籤和卡片名稱統一。
            ["accountSide"] = "科目分類",
            ["revenueDebitNearQuarterEnd"] = "在該季度前 X 天借記收入",
            ["revenueWithoutNormalCounterpart"] = "貸方為收入但借方非一般對方科目",
            ["manualRevenueEntry"] = "收入之人工分錄",
            ["preparerEqualsApprover"] = "編製人員與核准人員相同",
        };

    /// <summary>預篩選 row-tag 鍵 → 中文標籤，由規則登錄衍生（前端 <c>PRESCREEN_RULES</c> 的 label 鏡像）。</summary>
    public static readonly IReadOnlyDictionary<string, string> PrescreenKeys =
        PrescreenRuleDefinitions.RowTags.ToDictionary(rule => rule.Key, rule => rule.ConditionLabel!, StringComparer.Ordinal);

    /// <summary>科目配對分析模式 → 中文（前端 <c>ACCOUNT_PAIR_MODE_OPTIONS</c> 鏡像）。</summary>
    public static readonly IReadOnlyDictionary<string, string> AccountPairModes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JET.Domain.AccountPairModes.Exact] = "借方是 A 且貸方是 B",
            [JET.Domain.AccountPairModes.DebitAnchor] = "借方是 A，看它的對方科目",
            [JET.Domain.AccountPairModes.CreditAnchor] = "貸方是 B，看它的對方科目",
        };

    /// <summary>借貸科目分類（accountSide）模式 → 中文（前端 <c>ACCOUNT_SIDE_MODE_OPTIONS</c> 鏡像）。</summary>
    public static readonly IReadOnlyDictionary<string, string> AccountSideModes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["is"] = "科目屬於指定分類",
            ["isNot"] = "科目不屬於指定分類",
        };

    /// <summary>
    /// 科目配對單側多選分類的讀回分隔字元（前端 <c>FILTER_CATEGORY_LIST_SEPARATOR</c> 鏡像）。
    /// 讀回文字以後端 renderer 的輸出為準，前端讀回只能鏡射同一分隔字元。
    /// </summary>
    public const string CategoryListSeparator = "、";

    /// <summary>特殊科目類別配對模式 → 中文（前端 <c>SPECIAL_PAIR_MODE_OPTIONS</c> 鏡像）。</summary>
    public static readonly IReadOnlyDictionary<string, string> SpecialPairModes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SpecialAccountCategoryPairModes.DrAndCr] = "借方是 A 且貸方是 B",
            [SpecialAccountCategoryPairModes.DrNotCr] = "借方是 A 且整張傳票沒有 B 貸方",
            [SpecialAccountCategoryPairModes.NotDrCr] = "貸方是 B 且整張傳票沒有 A 借方",
        };

    /// <summary>借貸科目組合讀回的前綴（兩個 wire 型別共用，畫面上是同一張卡）。</summary>
    public const string AccountCombinationPrefix = "借貸科目組合：";

    /// <summary>
    /// 可作條件的 GL 邏輯欄（文字/日期欄）→ 中文（前端 <c>GL_FIELDS</c> 的 label 子集鏡像）。
    /// 只含 <c>FILTER_TEXT_FIELDS ∪ FILTER_DATE_FIELDS</c>；GL_FIELDS 另含金額/借貸機制欄供配對步驟用，
    /// 非條件渲染所需，不納入本表（守衛僅比對本表鍵在 GL_FIELDS 內且 label 相符）。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> GlFields =
        JetFieldCatalog.GlFields
            .Where(static field =>
                field.IsGenericFilterField
                && field.Kind is JetFieldValueKind.Text or JetFieldValueKind.Date)
            .OrderBy(static field => field.Order)
            .ToDictionary(
                static field => field.MappingSlots.Single(static slot => slot.IncludeInFieldInfo).Key,
                static field => field.MappingSlots.Single(static slot => slot.IncludeInFieldInfo).Label,
                StringComparer.Ordinal);

    /// <summary>已儲存情境裡，非營業日卡片加入的子條件組讀回這個名稱（前端 <c>FILTER_COMMON_ATOM_LABELS.nonBusinessDay</c> 鏡像）。</summary>
    public const string NonBusinessDayAtom = "非營業日";

    /// <summary>
    /// 認不得的預篩選條件、比對方式、借貸組合方式或條件型別在讀回裡的寫法，不印程式代號
    /// （前端 <c>RETIRED_CONDITION_LABEL</c> 同一句）。
    /// </summary>
    public const string RetiredCondition = "已停用的條件";

    /// <summary>攸關資料元素欄位已不在欄位配對中時，讀回與標題寫的名稱。</summary>
    public const string MissingRdeField = "已不在欄位配對中的攸關資料元素欄位";

    /// <summary>認不得的 GL 欄位在讀回裡的名稱。</summary>
    public const string MissingGlField = "已不存在的欄位";

    /// <summary>預篩選鍵 → 標籤；未登錄時寫 <see cref="RetiredCondition"/>（與前端 <c>ruleSummaryLabel</c> 同構）。</summary>
    public static string PrescreenLabel(string? key) =>
        key is not null && PrescreenKeys.TryGetValue(key, out var label) ? label : RetiredCondition;

    /// <summary>
    /// 知道 0 的個數時，「分錄金額中有連續 0 的尾數」寫成「金額尾數連續 N 個 0」；其他規則和不知道個數時同上。
    /// 前端 <c>prescreenConditionLabel</c> 同樣寫法。
    /// </summary>
    public static string PrescreenLabel(string? key, int? zerosThreshold) =>
        key == PrescreenRuleKeys.TrailingZeros && zerosThreshold is { } zeros
            ? $"金額尾數連續 {zeros} 個 0"
            : PrescreenLabel(key);

    /// <summary>科目配對模式 → 標籤；未登錄時寫 <see cref="RetiredCondition"/>。</summary>
    public static string AccountPairModeLabel(string? mode) =>
        mode is not null && AccountPairModes.TryGetValue(mode, out var label) ? label : RetiredCondition;

    /// <summary>特殊科目配對模式 → 標籤；未登錄時寫 <see cref="RetiredCondition"/>。</summary>
    public static string SpecialPairModeLabel(string? mode) =>
        mode is not null && SpecialPairModes.TryGetValue(mode, out var label) ? label : RetiredCondition;

    /// <summary>GL 欄鍵 → 標籤；未登錄回退原鍵（同前端 <c>glFieldLabel</c>）。讀回改用 <see cref="GlFieldReadback"/>。</summary>
    public static string GlFieldLabel(string? key) =>
        key is not null && GlFields.TryGetValue(key, out var label) ? label : key ?? string.Empty;

    /// <summary>
    /// 讀回裡的 GL 欄位名稱（前端 <c>glFieldReadback</c> 同構）：認不得的欄位寫 <see cref="MissingGlField"/>，不印程式代號。
    /// </summary>
    public static string GlFieldReadback(string? key) =>
        key is not null && GlFields.TryGetValue(key, out var label) ? label : MissingGlField;
}
