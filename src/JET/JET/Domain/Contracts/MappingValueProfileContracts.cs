namespace JET.Domain;

/// <summary>來源欄位非空值的一筆有界頻率摘要。</summary>
public sealed record MappingValueProfileValue(string Value, long Count);

/// <summary>
/// GL 匯入暫存表裡一個來源欄的值統計，只帶有限筆數的值。BlankCount 包含缺欄、null，以及去掉頭尾空白後是空字串的值；
/// DistinctCount 依去掉頭尾空白、不分大小寫後的值分組，用 <see cref="MappingCodeIdentity"/>（.NET 的 OrdinalIgnoreCase）。
/// </summary>
public sealed record MappingValueProfile(
    string SourceColumn,
    long BlankCount,
    long DistinctCount,
    IReadOnlyList<MappingValueProfileValue> Values,
    bool Truncated);

/// <summary>
/// 唯讀查詢匯入暫存表裡來源欄的值統計，用集合式 SQL 計算。實作只能回傳 <c>limit</c> 筆彙總後的值，
/// 不能把暫存表的全部資料列載入 Application。
/// </summary>
public interface IMappingValueProfileRepository
{
    /// <param name="fillDown">
    /// 這個來源欄勾了往下填補時為 true：先在每份來源內依列序把空白補成上一列的值再計數，和確認配對時的填補同一規則，
    /// 每份來源開頭的空白仍算空白。
    /// </param>
    Task<MappingValueProfile> GetAsync(
        string projectId,
        string batchId,
        string sourceColumn,
        int limit,
        bool fillDown,
        CancellationToken cancellationToken);

    /// <summary>
    /// 明確要求時核對完整來源；回傳原請求中查無的值。往下填補只複製來源裡已有的值，非空白的值存不存在不受它影響，所以這裡照原樣核對。null 代表 provider 尚未支援；
    /// SQLite 已實作。
    /// </summary>
    Task<IReadOnlyList<string>?> FindMissingValuesAsync(
        string projectId,
        string batchId,
        string sourceColumn,
        IReadOnlyList<string> comparisonValues,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>?>(null);

    /// <summary>
    /// 同時算出值概況與來源值核對，結果分別和 <see cref="GetAsync"/>、<see cref="FindMissingValuesAsync"/> 相同。
    /// 預設依序呼叫這兩個方法；實作可以改成只掃一次來源。
    /// </summary>
    async Task<CheckedMappingValueProfile> GetWithSourceValueCheckAsync(
        string projectId,
        string batchId,
        string sourceColumn,
        int limit,
        bool fillDown,
        IReadOnlyList<string> comparisonValues,
        CancellationToken cancellationToken)
    {
        var profile = await GetAsync(projectId, batchId, sourceColumn, limit, fillDown, cancellationToken);
        var missing = await FindMissingValuesAsync(projectId, batchId, sourceColumn, comparisonValues, cancellationToken);
        return new CheckedMappingValueProfile(profile, missing);
    }
}

/// <summary>
/// 值概況與來源值核對一起算出的結果。<c>MissingValues</c> 是要核對的值裡在來源找不到的，依請求的順序；
/// null 代表資料庫實作沒有這項核對。
/// </summary>
public sealed record CheckedMappingValueProfile(MappingValueProfile Profile, IReadOnlyList<string>? MissingValues);
