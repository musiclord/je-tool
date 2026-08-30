namespace JET.Domain;

/// <summary>
/// 報告 writer 在單一 keyset page 內把 target entry 對回原始 GL row_json。
/// 呼叫端每次最多傳一頁 entry id；repository 不提供全表列舉，避免繞過分頁邊界。
/// </summary>
public interface IRawGlExportRepository
{
    Task<IReadOnlyDictionary<long, string>> FetchJsonByEntryIdsAsync(
        string projectId,
        IReadOnlyList<long> entryIds,
        CancellationToken cancellationToken);
}
