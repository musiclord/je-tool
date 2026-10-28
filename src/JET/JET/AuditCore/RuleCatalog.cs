using JET.Domain;

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
/// 規則命名登錄表：資料驗證四條寫在這裡，預篩選十四條由 <see cref="PrescreenRuleDefinitions"/> 衍生，
/// 所以 <c>PrescreenRuleKeys.FilterableKeys</c> 和本表的 RowTag 集合必然一致（RuleCatalogTests 鎖定此不變量）。
/// </summary>
internal static class RuleCatalog
{
    public static readonly IReadOnlyList<RuleDescriptor> All =
    [
        new("completeness_test", "completenessTest", "完整性測試", "V1", RuleShape.Validation),
        new("doc_balance_test", "docBalanceTest", "借貸不平測試", "V2", RuleShape.Validation),
        new("inf_sampling_test", "infSamplingTest", "INF 抽樣測試", "V3", RuleShape.Validation),
        new("null_records_test", "nullRecordsTest", "空值紀錄測試", "V4", RuleShape.Validation),
        .. PrescreenRuleDefinitions.All.Select(rule => new RuleDescriptor(
            rule.Slug,
            rule.Key,
            rule.Title,
            rule.LegacyCode,
            rule.Shape == PrescreenRuleShape.RowTag ? RuleShape.RowTag : RuleShape.Aggregate))
    ];
}
