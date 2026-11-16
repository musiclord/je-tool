namespace JET.Domain;

/// <summary>
/// 報告寫出程式用一頁分錄的 entry id，取回每筆分錄在來源檔的原始列 row_json。
/// 呼叫端每次最多傳一頁的 entry id；repository 不提供列出整張表的方法，避免繞過分頁。
/// </summary>
public interface IRawGlExportRepository
{
    Task<IReadOnlyDictionary<long, string>> FetchJsonByEntryIdsAsync(
        string projectId,
        IReadOnlyList<long> entryIds,
        CancellationToken cancellationToken);
}
