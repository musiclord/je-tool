namespace JET.Domain;

/// <summary>
/// Filter／INF result page 的 backend column registry entry。公開 wire renderer 逐欄投影為
/// <c>{ key, label, valueType, isCustom }</c>；SourceColumn 不屬於此契約。
/// </summary>
public sealed record ResultPageColumn(
    string Key,
    string Label,
    string ValueType,
    bool IsCustom);

/// <summary>
/// 一個已保存 RDE cell 的 provider-neutral typed value。空白 RDE 沒有資料列；因此本型別只代表
/// present cell，缺值由 renderer 依 column registry 補成 explicit null。
/// </summary>
public sealed record ResultPageRdeValue(
    long EntryId,
    string FieldId,
    string ValueType,
    string? TextValue,
    string? DateValue,
    long? AmountScaled);

/// <summary>RDE page port 的 entry-id batch 守衛；只允許單一 keyset page 的唯一正整數 id。</summary>
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
