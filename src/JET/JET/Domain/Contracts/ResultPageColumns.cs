namespace JET.Domain;

/// <summary>
/// 篩選結果與 INF 抽樣明細表的一個欄位定義，有哪些欄由後端決定。回應裡每一欄寫成
/// <c>{ key, label, valueType, isCustom }</c>；SourceColumn 不在這份定義裡。
/// </summary>
public sealed record ResultPageColumn(
    string Key,
    string Label,
    string ValueType,
    bool IsCustom);

/// <summary>
/// 一格已儲存的 RDE 值，帶著值的型別，和用哪一種資料庫無關。空白的 RDE 沒有資料列，所以這個型別只代表有值的格子；
/// 沒有值的格子，由組回應的程式依欄位定義補成 null。
/// </summary>
public sealed record ResultPageRdeValue(
    long EntryId,
    string FieldId,
    string ValueType,
    string? TextValue,
    string? DateValue,
    long? AmountScaled);

/// <summary>
/// 檢查交給 <c>IResultPageRdeValuesPort</c> 的那一批 entry id：數量不能超過一頁的上限，
/// 每個 id 都要大於 0，而且不能重複。
/// </summary>
public static class ResultPageRdeValueBatch
{
    public static IReadOnlyList<long> Validate(IReadOnlyList<long> entryIds)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        if (entryIds.Count > PageRequest.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(entryIds),
                $"RDE value batch 最多只能包含 {PageRequest.MaxPageSize} 個 entry id。");
        }

        var seen = new HashSet<long>();
        foreach (var entryId in entryIds)
        {
            if (entryId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(entryIds), "RDE value batch 的 entry id 必須大於 0。");
            }
            if (!seen.Add(entryId))
            {
                throw new ArgumentException($"RDE value batch 的 entry id '{entryId}' 重複。", nameof(entryIds));
            }
        }

        return entryIds;
    }
}
