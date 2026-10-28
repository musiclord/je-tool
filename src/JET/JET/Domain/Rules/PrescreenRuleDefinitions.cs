namespace JET.Domain;

/// <summary>規則的結果形狀：逐筆標記可以作第五步的條件，彙總只供第四步判讀。</summary>
public enum PrescreenRuleShape
{
    RowTag,
    Aggregate
}

/// <summary>
/// 一條預篩選規則的定義：回應鍵、程序代號、規則名稱、第五步條件用的標籤、歷史代號、形狀，
/// 以及需要的 GL 欄位與案件資料。規則名稱只流進 RuleCatalog 的顯示名稱；第四步卡片的標題另寫在前端 validate-step.js，
/// 其中「金額尾數連續 6 個 0 的分錄」與「非授權編製人員之分錄」兩張卡和這裡的名稱不同（2026-10-28 複審查到，改正說明，畫面不動）。述詞由 AuditCore 的 PrescreenRulePredicates 依 <see cref="Key"/> 對應，一條規則一個方法。
/// 彙總規則沒有條件標籤。歷史代號只供回查舊文件，不進畫面、回應或資料表名稱。
/// </summary>
public sealed record PrescreenRuleDefinition(
    string Key,
    string Slug,
    string Title,
    string? ConditionLabel,
    string? LegacyCode,
    PrescreenRuleShape Shape,
    IReadOnlyList<JetRulePrerequisite> Requirements);

/// <summary>
/// 十四條預篩選規則的唯一登錄（使用者 2026-10-22 裁定「一條規則一個定義」）。
/// 規則目錄 <c>RuleCatalog</c>、程序清單、可篩選的鍵 <see cref="PrescreenRuleKeys.FilterableKeys"/>、
/// 條件標籤 <see cref="FilterConditionLabels.PrescreenKeys"/> 與規則需求表 <c>JetRuleRequirements.PrescreenRules</c>
/// 都從這裡衍生；前端 ui-core.js 的 PRESCREEN_RULES 鏡像十二條逐筆規則的鍵、標籤與需求。
/// 需求依原因列出的順序；核心欄位一律列出，必填欄位確認配對時一定有來源欄，列出來只是讓每條規則用到什麼都寫在同一處。
/// </summary>
public static class PrescreenRuleDefinitions
{
    private static readonly JetRulePrerequisite LastPeriodStart = new(JetRulePrerequisiteKind.LastPeriodStart);
    private static readonly JetRulePrerequisite AccountMapping = new(JetRulePrerequisiteKind.AccountMapping);
    private static readonly JetRulePrerequisite RevenueCategory = new(JetRulePrerequisiteKind.RevenueCategory);
    private static readonly JetRulePrerequisite CounterpartCategory = new(JetRulePrerequisiteKind.CounterpartCategory);
    private static readonly JetRulePrerequisite AuthorizedPreparers = new(JetRulePrerequisiteKind.AuthorizedPreparers);
    private static readonly JetRulePrerequisite HolidayCalendar = new(JetRulePrerequisiteKind.HolidayCalendar);

    /// <summary>十四條規則，依目錄順序；第四步卡片、流程總覽與報告都照這個順序列出。</summary>
    public static IReadOnlyList<PrescreenRuleDefinition> All { get; } =
    [
        RowTag(PrescreenRuleKeys.PostPeriodApproval, "post_period_approval", "期末財報準備日起核准之分錄", "財報準備日起核准", "R1",
            Field(JetFieldCatalog.GlDocDate), LastPeriodStart),
        RowTag(PrescreenRuleKeys.SuspiciousKeywords, "suspicious_keywords", "分錄摘要出現特定描述", "摘要特定描述", "R2",
            Field(JetFieldCatalog.GlDescription)),
        RowTag(PrescreenRuleKeys.UnexpectedAccountPair, "unexpected_account_pair", "未預期出現之特定借貸組合", "未預期借貸組合", "R3",
            AccountMapping, RevenueCategory, CounterpartCategory),
        RowTag(PrescreenRuleKeys.TrailingZeros, "trailing_zeros", "分錄金額中有連續零的尾數",
            $"金額尾數連續 {TrailingZeroThreshold.DefaultZerosThreshold} 個 0", "R4",
            Field(JetFieldCatalog.GlAmount)),
        Aggregate(JetRuleRequirements.CreatorSummary, "creator_summary", "依分錄編製者彙總", "R5",
            Field(JetFieldCatalog.GlCreateBy)),
        Aggregate(JetRuleRequirements.RareAccounts, "rare_accounts", "較少使用之科目", "R6",
            Field(JetFieldCatalog.GlAccNum)),
        RowTag(PrescreenRuleKeys.WeekendPosting, "weekend_posting", "週末過帳", "週末過帳", null,
            Field(JetFieldCatalog.GlPostDate)),
        RowTag(PrescreenRuleKeys.WeekendApproval, "weekend_approval", "週末核准", "週末核准", null,
            Field(JetFieldCatalog.GlDocDate)),
        RowTag(PrescreenRuleKeys.HolidayPosting, "holiday_posting", "假日過帳", "假日過帳", null,
            HolidayCalendar, Field(JetFieldCatalog.GlPostDate)),
        RowTag(PrescreenRuleKeys.HolidayApproval, "holiday_approval", "假日核准", "假日核准", null,
            HolidayCalendar, Field(JetFieldCatalog.GlDocDate)),
        RowTag(PrescreenRuleKeys.BlankDescription, "blank_description", "空白摘要", "空白摘要", "R7",
            Field(JetFieldCatalog.GlDescription)),
        RowTag(PrescreenRuleKeys.NonAuthorizedPreparer, "non_authorized_preparer", "非授權編製人員", "非授權編製人員", null,
            AuthorizedPreparers, Field(JetFieldCatalog.GlCreateBy)),
        RowTag(PrescreenRuleKeys.LowFrequencyPreparer, "low_frequency_preparer",
            $"編製分錄較少的人員（{PreparerFrequency.DefaultMaxEntries} 筆以下）",
            $"編製分錄較少的人員（{PreparerFrequency.DefaultMaxEntries} 筆以下）", null,
            Field(JetFieldCatalog.GlCreateBy)),
        RowTag(PrescreenRuleKeys.LowFrequencyAccount, "low_frequency_account",
            $"使用較少的科目（{AccountFrequency.DefaultMaxEntries} 筆以下）",
            $"使用較少的科目（{AccountFrequency.DefaultMaxEntries} 筆以下）", null,
            Field(JetFieldCatalog.GlAccNum))
    ];

    /// <summary>十二條逐筆規則，可作第五步的「預篩選」條件。</summary>
    public static IReadOnlyList<PrescreenRuleDefinition> RowTags { get; } =
        All.Where(rule => rule.Shape == PrescreenRuleShape.RowTag).ToArray();

    /// <summary>依回應鍵查定義；鍵不存在時由呼叫端的例外說明，不在這裡包裝。</summary>
    public static IReadOnlyDictionary<string, PrescreenRuleDefinition> ByKey { get; } =
        All.ToDictionary(rule => rule.Key, StringComparer.Ordinal);

    /// <summary>依程序代號查定義。</summary>
    public static PrescreenRuleDefinition BySlug(string slug) =>
        All.Single(rule => string.Equals(rule.Slug, slug, StringComparison.Ordinal));

    private static PrescreenRuleDefinition RowTag(
        string key, string slug, string title, string conditionLabel, string? legacyCode, params JetRulePrerequisite[] requirements) =>
        new(key, slug, title, conditionLabel, legacyCode, PrescreenRuleShape.RowTag, requirements);

    private static PrescreenRuleDefinition Aggregate(
        string key, string slug, string title, string? legacyCode, params JetRulePrerequisite[] requirements) =>
        new(key, slug, title, null, legacyCode, PrescreenRuleShape.Aggregate, requirements);

    private static JetRulePrerequisite Field(string semanticIdentity) =>
        new(JetRulePrerequisiteKind.GlField, semanticIdentity);
}
