using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 規則的結果是哪一種。Validation 是資料驗證的摘要；RowTag 是逐筆標記分錄的預篩選規則，可以當成進階篩選的條件；
/// Aggregate 是彙總型的預篩選規則，只供判讀，不能當成條件。
/// </summary>
internal enum RuleShape
{
    Validation,
    RowTag,
    Aggregate
}

/// <summary>
/// 規則命名登錄表的一筆。<c>Slug</c> 用在資料表與追溯；<c>WireKey</c> 是 JSON 屬性名與 prescreenKey 的值；
/// <c>DisplayName</c> 是畫面與工作底稿分頁上的中文名稱；<c>LegacyCode</c> 是舊代號，只用來回查舊版文件，
/// 不得出現在畫面、action 的 JSON 或資料表名。
/// </summary>
internal sealed record RuleDescriptor(
    string Slug,
    string WireKey,
    string DisplayName,
    string? LegacyCode,
    RuleShape Shape);

/// <summary>
/// 規則命名登錄表：資料驗證四條寫在這裡，預篩選十二條由 <see cref="PrescreenRuleDefinitions"/> 衍生，
/// 所以 <c>PrescreenRuleKeys.FilterableKeys</c> 和本表的 RowTag 集合必然一致（RuleCatalogTests 鎖定此不變量）。
/// </summary>
internal static class RuleCatalog
{
    public static readonly IReadOnlyList<RuleDescriptor> All =
    [
        new("completeness_test", "completenessTest", "完整性測試", "V1", RuleShape.Validation),
        new("voucher_balance_test", "voucherBalanceTest", "借貸不平測試", "V2", RuleShape.Validation),
        new("inf_sampling_test", "infSamplingTest", "資料可靠性測試", "V3", RuleShape.Validation),
        new("blank_records_test", "blankRecordsTest", "資料缺漏輔助檢視", "V4", RuleShape.Validation),
        .. PrescreenRuleDefinitions.All.Select(rule => new RuleDescriptor(
            rule.Slug,
            rule.Key,
            rule.Title,
            rule.LegacyCode,
            rule.Shape == PrescreenRuleShape.RowTag ? RuleShape.RowTag : RuleShape.Aggregate))
    ];
}
