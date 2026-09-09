namespace JET.Domain;

/// <summary>
/// 進階篩選「條件中文標籤」的單一事實來源（本表為正本；前端 <c>ui-core.js</c> 的標籤常數
/// 鏡像本表，由 <c>FilterConditionLabelMirrorTests</c> 讀 JS 檔正則抽取比對守衛漂移）。
///
/// 為什麼要有這張表：CriteriaSelection summary 的「條件內容」由
/// <see cref="JET.Application.FilterConditionRenderer"/> 渲染成與前端藍色 read-back 同構的中文布林式；
/// 兩邊各有一份標籤就會漂移，故把標籤收斂到 Domain 一份、前端以測試釘住。這裡只放
/// 「鍵 → 中文標籤」與兩端共用的固定結構片語；括號、AND／OR 組合與各型別動態句型仍屬 renderer 邏輯。
///
/// 覆蓋範圍＝條件型別顯示名，以及渲染器會用到的標籤表：預篩選鍵、文字比對模式、科目配對模式、
/// 特殊科目配對模式、可篩選 GL 邏輯欄、非營業日原子，以及 sameVoucher／textSet 的固定結構片語。
/// <see cref="PrescreenKeys"/> 的鍵集合與
/// <see cref="PrescreenRuleKeys.FilterableKeys"/> 恆等（<c>FilterConditionLabelMirrorTests</c>
/// 雙向守衛），任何 row-tag 鍵新增都必須同時給中文標籤，否則守衛紅燈。
/// </summary>
public static class FilterConditionLabels
{
    /// <summary>
    /// 進階篩選 AST type wire key → 中文顯示名（前端 <c>FILTER_RULE_TYPES[].label</c> 鏡像）。
    /// 前端的 quickLabel、分組與可用性 metadata 不屬本表。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RuleTypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["prescreen"] = "預篩選",
            ["text"] = "文字條件",
            ["textSet"] = "文字值清單",
            ["numRange"] = "金額區間",
            ["dateRange"] = "日期區間",
            ["customKeywords"] = "自訂關鍵字",
            ["drCrOnly"] = "借貸限定",
            ["manualAuto"] = "人工/自動",
            ["customTrailingZeros"] = "自訂尾數位數",
            ["accountPair"] = "借貸科目組合（看對方科目）",
            ["specialAccountCategoryPair"] = "借貸科目組合",
            ["customPreparerEntryCount"] = "自訂編製人員張數",
            ["customAccountEntryCount"] = "自訂科目張數",
            ["typed"] = "攸關資料元素條件",
            ["fieldValue"] = "欄位值比較",
            ["accountSide"] = "借貸科目分類",
            ["revenueDebitNearQuarterEnd"] = "季末前借記收入",
            ["revenueWithoutNormalCounterpart"] = "收入無一般對方科目",
            ["manualRevenueEntry"] = "收入之人工分錄",
            ["trailingDigits"] = "特定金額尾數",
            ["preparerEqualsApprover"] = "編製與核准同一人",
        };

    /// <summary>預篩選 row-tag 鍵 → 中文標籤（前端 <c>PRESCREEN_KEY_OPTIONS</c> 鏡像）。</summary>
    public static readonly IReadOnlyDictionary<string, string> PrescreenKeys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PrescreenRuleKeys.PostPeriodApproval] = "期末後核准",
            [PrescreenRuleKeys.SuspiciousKeywords] = "摘要特定描述",
            [PrescreenRuleKeys.UnexpectedAccountPair] = "未預期借貸組合",
            [PrescreenRuleKeys.TrailingZeros] = "連續零尾數金額",
            [PrescreenRuleKeys.WeekendPosting] = "週末過帳",
            [PrescreenRuleKeys.WeekendApproval] = "週末核准",
            [PrescreenRuleKeys.HolidayPosting] = "假日過帳",
            [PrescreenRuleKeys.HolidayApproval] = "假日核准",
            [PrescreenRuleKeys.BlankDescription] = "摘要空白",
            [PrescreenRuleKeys.BackdatedPosting] = "回溯過帳",
            [PrescreenRuleKeys.NonAuthorizedPreparer] = "非授權編製人員",
            [PrescreenRuleKeys.LowFrequencyPreparer] = "低頻編製者",
            [PrescreenRuleKeys.LowFrequencyAccount] = "低頻科目",
        };

    /// <summary>文字比對模式 → 中文（前端 <c>TEXT_MODE_OPTIONS</c> 鏡像）。</summary>
    public static readonly IReadOnlyDictionary<string, string> TextModes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["contains"] = "包含",
            ["exact"] = "完全符合",
            ["notContains"] = "不包含（排除）",
            ["notExact"] = "不等於（排除）",
        };

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
            ["absent"] = "整張傳票的這一側都不屬於指定分類",
        };

    /// <summary>
    /// 科目配對單側多選分類的讀回分隔字元（前端 <c>FILTER_CATEGORY_LIST_SEPARATOR</c> 鏡像）。
    /// 後端 renderer 是唯一權威輸出，前端讀回只能鏡射同一分隔字元。
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

    /// <summary>非營業日預設群組（週末 OR 假日）的原子白話（前端 <c>FILTER_KCT_ATOM_LABELS.kctNonBusinessDay</c> 鏡像）。</summary>
    public const string NonBusinessDayAtom = "非營業日（週末或假日）";

    /// <summary>sameVoucher 第一條規則的輸出列標籤（前端 read-back 鏡像）。</summary>
    public const string SameVoucherOutputAnchor = "主要條件（決定命中分錄）";

    /// <summary>sameVoucher 後續規則的跨列佐證說明（前端 help／read-back 鏡像）。</summary>
    public const string SameVoucherEvidenceExplanation = "後續條件可由同一傳票的其他分錄列符合";

    public const string TextSetContainsAny = "包含任一值";

    public const string TextSetExactAny = "完全符合任一值";

    public const string TextSetPreserveAsciiSpaces = "保留 ASCII 空白";

    public const string TextSetRemoveAsciiSpaces = "移除 ASCII 空白";

    /// <summary>
    /// typed 條件 operator → 中文標籤。後端 renderer 是唯一權威輸出；typed 前端 UI 於
    /// 「前端工作流整合」階段落地時必須鏡像本表並補 mirror 守衛，本階段尚無前端鏡像。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> TypedOperators =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["equals"] = "等於",
            ["notEquals"] = "不等於（排除）",
            ["contains"] = "包含",
            ["notContains"] = "不包含（排除）",
            ["in"] = "屬於任一值",
            ["notIn"] = "不屬於任何值（排除）",
            ["isBlank"] = "為空白",
            ["isNotBlank"] = "非空白",
            ["on"] = "等於日期",
            ["before"] = "早於",
            ["onOrBefore"] = "不晚於",
            ["after"] = "晚於",
            ["onOrAfter"] = "不早於",
            ["between"] = "介於",
            ["greaterThan"] = "大於",
            ["greaterThanOrEqual"] = "大於等於",
            ["lessThan"] = "小於",
            ["lessThanOrEqual"] = "小於等於",
        };

    /// <summary>typed money 條件 amountBasis 的讀回標籤。</summary>
    public const string TypedSignedAmountBasis = "帶正負號金額";

    public const string TypedAbsoluteAmountBasis = "金額絕對值";

    /// <summary>typed operator → 標籤；未登錄回退原值（同其他 closed token 的 fallback 慣例）。</summary>
    public static string TypedOperatorLabel(string? op) =>
        op is not null && TypedOperators.TryGetValue(op, out var label) ? label : op ?? string.Empty;

    /// <summary>預篩選鍵 → 標籤；未登錄回退原鍵（與前端 <c>ruleSummaryLabel</c> 的 fallback 同構）。</summary>
    public static string PrescreenLabel(string? key) =>
        key is not null && PrescreenKeys.TryGetValue(key, out var label) ? label : key ?? string.Empty;

    /// <summary>文字模式 → 標籤；未登錄回退原值。</summary>
    public static string TextModeLabel(string? mode) =>
        mode is not null && TextModes.TryGetValue(mode, out var label) ? label : mode ?? string.Empty;

    /// <summary>科目配對模式 → 標籤；未登錄回退原值。</summary>
    public static string AccountPairModeLabel(string? mode) =>
        mode is not null && AccountPairModes.TryGetValue(mode, out var label) ? label : mode ?? string.Empty;

    /// <summary>特殊科目配對模式 → 標籤；未登錄回退原值。</summary>
    public static string SpecialPairModeLabel(string? mode) =>
        mode is not null && SpecialPairModes.TryGetValue(mode, out var label) ? label : mode ?? string.Empty;

    /// <summary>GL 欄鍵 → 標籤；未登錄回退原鍵（同前端 <c>glFieldLabel</c>）。</summary>
    public static string GlFieldLabel(string? key) =>
        key is not null && GlFields.TryGetValue(key, out var label) ? label : key ?? string.Empty;
}
