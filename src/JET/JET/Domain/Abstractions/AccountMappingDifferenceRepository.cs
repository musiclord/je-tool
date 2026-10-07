namespace JET.Domain;

public static class AccountMappingDifferenceKinds
{
    public const string MappingOnly = "mappingOnly";
    public const string Unmapped = "unmapped";
    public static bool IsKnown(string kind) => kind is MappingOnly or Unmapped;
}

public sealed record AccountMappingDifferenceCounts(long MappingOnlyCount, long UnmappedCount);

public sealed record AccountMappingDifferencePage(
    IReadOnlyList<AccountMappingBlankAccount> Rows, string? NextCursor, long? TotalCount);

/// <summary>配對檔與有效 GL、TB 科目聯集的差異；只在匯入或使用者展開清單時計算。</summary>
public interface IAccountMappingDifferenceRepository
{
    Task<AccountMappingDifferenceCounts> CountAsync(string projectId, CancellationToken cancellationToken);
    Task<AccountMappingDifferencePage> GetPageAsync(
        string projectId, string kind, PageRequest request, CancellationToken cancellationToken);
}
