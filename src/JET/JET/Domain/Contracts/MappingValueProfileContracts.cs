namespace JET.Domain;

/// <summary>來源欄位非空值的一筆有界頻率摘要。</summary>
public sealed record MappingValueProfileValue(string Value, long Count);

/// <summary>
/// GL staging 來源欄位的有界 value profile。BlankCount 包含缺欄、null 與 trim 後空字串；
/// DistinctCount 只計 trim 後非空、大小寫敏感的相異值。
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
}
