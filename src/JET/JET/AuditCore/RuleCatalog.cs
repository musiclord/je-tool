namespace JET.AuditCore;

/// <summary>
/// 規則的結果形狀：資料驗證 summary、預篩選 row-tag（可作進階篩選列述詞）、
/// 預篩選彙總（僅供判讀，不可作列述詞）。
/// </summary>
internal enum RuleShape
{
    Validation,
    RowTag,
    Aggregate
}

/// <summary>
/// 規則命名登錄表的單筆描述：slug（資料表/追溯）、
/// wire key（JSON 屬性與 prescreenKey）、中文顯示名（UI 與工作底稿分頁）、
/// 歷史代號（僅供回查 legacy 文件，不得進入 UI / wire / 資料表名）。
/// </summary>
internal sealed record RuleDescriptor(
    string Slug,
    string WireKey,
    string DisplayName,
    string? LegacyCode,
    RuleShape Shape);

/// <summary>
/// 規則命名登錄表的程式內單一事實來源。
/// `PrescreenRuleKeys.FilterableKeys` 必須與本表 RowTag 集合一致
/// （RuleCatalogTests 鎖定此不變量）。
/// </summary>
internal static class RuleCatalog
{
    public static readonly IReadOnlyList<RuleDescriptor> All =
    [
        new("completeness_test", "completenessTest", "完整性測試", "V1", RuleShape.Validation),
        new("doc_balance_test", "docBalanceTest", "借貸不平測試", "V2", RuleShape.Validation),
        new("inf_sampling_test", "infSamplingTest", "INF 抽樣測試", "V3", RuleShape.Validation),
        new("null_records_test", "nullRecordsTest", "空值紀錄測試", "V4", RuleShape.Validation),
        new("post_period_approval", "postPeriodApproval", "期末財報準備日起核准之分錄", "R1", RuleShape.RowTag),
        new("suspicious_keywords", "suspiciousKeywords", "分錄摘要出現特定描述", "R2", RuleShape.RowTag),
        new("unexpected_account_pair", "unexpectedAccountPair", "未預期出現之特定借貸組合", "R3", RuleShape.RowTag),
        new("trailing_zeros", "trailingZeros", "分錄金額中有連續零的尾數", "R4", RuleShape.RowTag),
        new("creator_summary", "creatorSummary", "依分錄編製者彙總", "R5", RuleShape.Aggregate),
        new("rare_accounts", "rareAccounts", "較少使用之科目", "R6", RuleShape.Aggregate),
        new("weekend_posting", "weekendPosting", "週末過帳", null, RuleShape.RowTag),
        new("weekend_approval", "weekendApproval", "週末核准", null, RuleShape.RowTag),
        new("holiday_posting", "holidayPosting", "假日過帳", null, RuleShape.RowTag),
        new("holiday_approval", "holidayApproval", "假日核准", null, RuleShape.RowTag),
        new("blank_description", "blankDescription", "空白摘要", "R7", RuleShape.RowTag),
        new("backdated_posting", "backdatedPosting", "回溯過帳（總帳入帳日早於傳票日期）", null, RuleShape.RowTag),
        new("non_authorized_preparer", "nonAuthorizedPreparer", "非授權編製人員", null, RuleShape.RowTag),
        new("low_frequency_preparer", "lowFrequencyPreparer", "編製分錄較少的人員（11 筆以下）", null, RuleShape.RowTag),
        new("low_frequency_account", "lowFrequencyAccount", "使用較少的科目（11 筆以下）", null, RuleShape.RowTag)
    ];
}
