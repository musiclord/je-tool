namespace JET.Domain;

/// <summary>
/// 專案內報告檔的窄儲存介面。實作只做三件事：把內容寫成同目錄暫存檔、完整後改名、在 manifest
/// 記下 JET 寫了什麼。它不核對檔案內容，也不因為檔案在 JET 之外被改而拒絕任何操作；
/// Application 只提供報告內容，不得指定任意檔案系統路徑。
/// </summary>
public interface IReportArtifactStore
{
    Task<ReportArtifact> WriteAsync(
        string projectId,
        ReportArtifactWriteRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// 批次內所有內容都完成暫存後才逐一改名發布。任何一份寫入失敗時，已有的正式檔不會變成半成品，
    /// 尚未成功的那些不會出現在清單中。
    /// </summary>
    Task<IReadOnlyList<ReportArtifact>> WriteBatchAsync(
        string projectId,
        IReadOnlyList<ReportArtifactWriteRequest> requests,
        CancellationToken cancellationToken);

    /// <summary>列出 manifest 記錄的報告，並依磁碟現況填入 <see cref="ReportArtifact.FileState"/>。</summary>
    Task<IReadOnlyList<ReportArtifact>> ListAsync(
        string projectId,
        CancellationToken cancellationToken);

    /// <summary>
    /// 只供本機 host 使用。回傳值是位於指定專案資料夾內的絕對路徑，不得寫入 manifest 或 wire response。
    /// 檔案已不在時回 <see cref="JetErrorCodes.FileNotFound"/>，訊息告訴使用者重新匯出即可。
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
    /// 供 project.delete 在授權通過後取得。lease 位於案件資料夾外，只排除其他報告寫入，
    /// 並保持到 provider 資料與案件資料夾刪除結束。
    /// </summary>
    Task<IAsyncDisposable> AcquireProjectDeletionLeaseAsync(
        string projectId,
        CancellationToken cancellationToken);
}

/// <summary>
/// 由正式 store 實作的內部能力：匯出可以在檔案改名前收到「即將發布哪一種報告」的回呼，供進度事件用。
/// 與公開契約分開，是因為回呼只能在暫存檔已完整寫出之後才發出。
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
