namespace JET.Domain;

/// <summary>來源欄位非空值的一筆有界頻率摘要。</summary>
public sealed record MappingValueProfileValue(string Value, long Count);

/// <summary>
/// GL staging 來源欄位的有界 value profile。BlankCount 包含缺欄、null 與 trim 後空字串；
/// 本地資料庫的 DistinctCount 依 trim 與 OrdinalIgnoreCase 分組；SQL Server 尚待同步此規則。
/// </summary>
public sealed record MappingValueProfile(
    string SourceColumn,
    long BlankCount,
    long DistinctCount,
    IReadOnlyList<MappingValueProfileValue> Values,
    bool Truncated);

/// <summary>
/// 只讀、set-based 的 staging value profile port。實作只可回傳 <c>limit</c> 筆聚合值，
/// 不得把完整 staging rows 載入 Application。
/// </summary>
public interface IMappingValueProfileRepository
{
    Task<MappingValueProfile> GetAsync(
        string projectId,
        string batchId,
        string sourceColumn,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>明確要求時核對完整來源；回傳原請求中查無的值，null 代表目前 provider 尚未支援。</summary>
    Task<IReadOnlyList<string>?> FindMissingValuesAsync(
        string projectId,
        string batchId,
        string sourceColumn,
        IReadOnlyList<string> comparisonValues,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>?>(null);
}
