namespace JET.Domain;

/// <summary>
/// 專案內正式報告的窄儲存介面。實作負責同目錄暫存、原子發布、雜湊與相對路徑索引；
/// Application 只提供報告內容，不得指定任意檔案系統路徑。
/// </summary>
public interface IReportArtifactStore
{
    Task<ReportArtifact> WriteAsync(
        string projectId,
        ReportArtifactWriteRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// 批次內所有內容都完成暫存後才發布。任何寫入、發布或索引更新失敗時，整批不會出現在索引中。
    /// </summary>
    Task<IReadOnlyList<ReportArtifact>> WriteBatchAsync(
        string projectId,
        IReadOnlyList<ReportArtifactWriteRequest> requests,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ReportArtifact>> ListAsync(
        string projectId,
        CancellationToken cancellationToken);

    /// <summary>在同一把 project-scoped cross-process lock 內復原 pending journal 並讀取語意 revision。</summary>
    Task<ReportArtifactCatalog> ReadCatalogAsync(
        string projectId,
        CancellationToken cancellationToken);

    /// <summary>
    /// 只供本機 host 使用。回傳值是已驗證仍位於指定專案資料夾內的絕對路徑，不得寫入 manifest 或 wire response。
    /// </summary>
    Task<string> ResolvePathAsync(
        string projectId,
        string artifactId,
        CancellationToken cancellationToken);

    Task<int> MarkStaleAsync(
        string projectId,
        ReportArtifactKind kind,
        CancellationToken cancellationToken);

    Task<int> MarkStaleAsync(
        string projectId,
        Func<ReportArtifact, bool> predicate,
        CancellationToken cancellationToken);

    /// <summary>
    /// 以 expected revision 防止 preview→confirm TOCTOU，並原子發布 manifest、project-local audit 與檔案清理。
    /// candidates 只能由後端 policy 產生，wire payload 不得提供 IDs。
    /// </summary>
    Task<ReportArtifactCleanupResult> CleanupAsync(
        string projectId,
        string expectedCatalogRevision,
        IReadOnlyList<ReportArtifactCleanupCandidate> candidates,
        string requestedBy,
        CancellationToken cancellationToken);

    /// <summary>
    /// 供 project.delete 在授權通過後取得。lease 位於案件資料夾外，只排除其他 artifact 操作，
    /// 不讀取或復原案件內 journal；並保持到 provider 資料與案件資料夾刪除結束。
    /// </summary>
    Task<IAsyncDisposable> AcquireProjectDeletionLeaseAsync(
        string projectId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Internal capability implemented by the production artifact store so an
/// export can announce the exact pre-journal publishing boundary. It is kept
/// separate from the public storage contract: callers cannot manufacture a
/// publishing event before temporary files have been closed, flushed and
/// hashed by the store.
/// </summary>
internal interface IReportArtifactPublishingStore
{
    Task<ReportArtifact> WriteWithPublishingAsync(
        string projectId,
        ReportArtifactWriteRequest request,
        Action<ReportArtifactKind> publishingArtifact,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ReportArtifact>> WriteBatchWithPublishingAsync(
        string projectId,
        IReadOnlyList<ReportArtifactWriteRequest> requests,
        Action<ReportArtifactKind> publishingArtifact,
        CancellationToken cancellationToken);
}
