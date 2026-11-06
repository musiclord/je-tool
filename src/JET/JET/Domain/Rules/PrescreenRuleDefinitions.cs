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
/// 其中「金額尾數連續 N 個 0 的分錄」與「非授權編製人員之分錄」兩張卡和這裡的名稱不同（2026-10-28 複審查到，改正說明，畫面不動）；N 是由 GL 算出的個數。述詞由 AuditCore 的 PrescreenRulePredicates 依 <see cref="Key"/> 對應，一條規則一個方法。
/// 彙總規則沒有條件標籤。歷史代號只供回查舊文件，不進畫面、回應或資料表名稱。
/// </summary>
public sealed record PrescreenRuleDefinition(
    string Key,
    string Slug,
    string Title,
    string? ConditionLabel,
    string? LegacyCode,
    PrescreenRuleShape Shape,
    IReadOnlyList<JetRulePrerequisite> Requirements)
{
    /// <summary>
    /// 情境只有一組、這組只有這一條條件時，報告摘要的列數、第五步的分錄筆數與底稿的逐列標記改算整張命中傳票的全部分錄。
    /// 只有「未預期借貸組合」照舊 IDEA 這樣算（裁定 2026-08-28-01）；和其他條件組合時仍只算直接命中的分錄。
    /// 判斷寫在規則定義，<c>JetAuditProgram.UsesLegacyWholeVoucherRows</c> 讀這裡，不再比對規則代號（裁定 2026-11-05-02）。
    /// </summary>
    public bool ListsWholeHitVouchersWhenAlone { get; init; }
}

/// <summary>
/// 十二條預篩選規則的唯一登錄，一條規則一個定義（裁定 2026-10-22-13）。
/// 規則目錄 <c>RuleCatalog</c>、程序清單、可篩選的鍵 <see cref="PrescreenRuleKeys.FilterableKeys"/>、
/// 條件標籤 <see cref="FilterConditionLabels.PrescreenKeys"/> 與規則需求表 <c>JetRuleRequirements.PrescreenRules</c>
/// 都從這裡衍生；前端 ui-core.js 的 PRESCREEN_RULES 鏡像十條逐筆規則的鍵、標籤與需求。
/// 第四步照舊版只留依筆數排序的兩份彙總，不另外挑出 11 筆以下的人員與科目（裁定 2026-10-29-27）；
/// 第五步欄位條件的常用查法「較少使用的科目」「編製分錄較少的人員」是「科目與人員統計」條件，不是這裡的規則。
/// 需求依原因列出的順序；核心欄位一律列出，必填欄位確認配對時一定有來源欄，列出來只是讓每條規則用到什麼都寫在同一處。
/// </summary>
public static class PrescreenRuleDefinitions
{
    private static readonly JetRulePrerequisite PrepStartDate = new(JetRulePrerequisiteKind.PrepStartDate);
    private static readonly JetRulePrerequisite AccountMapping = new(JetRulePrerequisiteKind.AccountMapping);
    private static readonly JetRulePrerequisite RevenueCategory = new(JetRulePrerequisiteKind.RevenueCategory);
    private static readonly JetRulePrerequisite CounterpartCategory = new(JetRulePrerequisiteKind.CounterpartCategory);
    private static readonly JetRulePrerequisite AuthorizedPreparer = new(JetRulePrerequisiteKind.AuthorizedPreparer);
    private static readonly JetRulePrerequisite HolidayCalendar = new(JetRulePrerequisiteKind.HolidayCalendar);

    /// <summary>十二條規則，依目錄順序；第四步卡片、流程總覽與報告都照這個順序列出。</summary>
    public static IReadOnlyList<PrescreenRuleDefinition> All { get; } =
    [
        RowTag(PrescreenRuleKeys.PrepPeriodApproval, "prep_period_approval", "期末財報準備日起核准之分錄", "財報準備日起核准", "R1",
            Field(JetFieldCatalog.GlApprovalDate), PrepStartDate),
        RowTag(PrescreenRuleKeys.SuspiciousKeywords, "suspicious_keywords", "分錄摘要出現特定描述", "摘要特定描述", "R2",
            Field(JetFieldCatalog.GlDescription)),
        RowTag(PrescreenRuleKeys.UnexpectedAccountPair, "unexpected_account_pair", "未預期出現之特定借貸組合", "未預期借貸組合", "R3",
            AccountMapping, RevenueCategory, CounterpartCategory) with
        {
            ListsWholeHitVouchersWhenAlone = true
        },
        // 0 的個數由 GL 算出（裁定 2026-11-05-25），條件標籤在知道個數時才寫成「金額尾數連續 N 個 0」
        // （FilterConditionLabels.PrescreenLabel 帶個數的寫法）；這裡是不知道個數時用的規則名稱。
        RowTag(PrescreenRuleKeys.TrailingZeros, "trailing_zeros", "分錄金額中有連續 0 的尾數",
            "分錄金額中有連續 0 的尾數", "R4",
            Field(JetFieldCatalog.GlAmount)),
        Aggregate(JetRuleRequirements.PreparerSummary, "preparer_summary", "依分錄編製者彙總", "R5",
            Field(JetFieldCatalog.GlCreatedBy)),
        Aggregate(JetRuleRequirements.RareAccounts, "rare_accounts", "較少使用之科目", "R6",
            Field(JetFieldCatalog.GlAccountNumber)),
        RowTag(PrescreenRuleKeys.WeekendPosting, "weekend_posting", "週末過帳", "週末過帳", null,
            Field(JetFieldCatalog.GlDate)),
        RowTag(PrescreenRuleKeys.WeekendApproval, "weekend_approval", "週末核准", "週末核准", null,
            Field(JetFieldCatalog.GlApprovalDate)),
        RowTag(PrescreenRuleKeys.HolidayPosting, "holiday_posting", "假日過帳", "假日過帳", null,
            HolidayCalendar, Field(JetFieldCatalog.GlDate)),
        RowTag(PrescreenRuleKeys.HolidayApproval, "holiday_approval", "假日核准", "假日核准", null,
            HolidayCalendar, Field(JetFieldCatalog.GlApprovalDate)),
        RowTag(PrescreenRuleKeys.BlankDescription, "blank_description", "空白摘要", "空白摘要", "R7",
            Field(JetFieldCatalog.GlDescription)),
        RowTag(PrescreenRuleKeys.NonAuthorizedPreparer, "non_authorized_preparer", "非授權編製人員", "非授權編製人員", null,
            AuthorizedPreparer, Field(JetFieldCatalog.GlCreatedBy))
    ];

    /// <summary>十條逐筆規則，可作第五步的「預篩選」條件。</summary>
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
