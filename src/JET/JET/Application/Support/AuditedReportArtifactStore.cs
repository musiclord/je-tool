using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>正式報告發布的結果：發布的報告，以及寫不進操作紀錄時給審計員的提醒（寫進去時是 null）。</summary>
internal sealed record AuditedReportPublication(IReadOnlyList<ReportArtifact> Artifacts, string? AuditLogWarning);

/// <summary>
/// 產生報告的動作用這個介面發布，才拿得到寫不進操作紀錄時的提醒，放進回應的 warnings
/// （使用者 2026-10-19 裁定回報成功並提醒）。
/// </summary>
internal interface IAuditedReportPublishingStore
{
    Task<AuditedReportPublication> WriteAuditedAsync(
        string projectId,
        ReportArtifactWriteRequest request,
        Action<ReportArtifactKind> publishingArtifact,
        CancellationToken cancellationToken);

    Task<AuditedReportPublication> WriteBatchAuditedAsync(
        string projectId,
        IReadOnlyList<ReportArtifactWriteRequest> requests,
        Action<ReportArtifactKind> publishingArtifact,
        CancellationToken cancellationToken);
}

/// <summary>
/// 正式報告清單的單一 audit 裝飾器：成功發布或汰換後 append 一筆 project-local audit event。
/// 寫不進操作紀錄時報告檔已經發布，兩種案件都不讓這次發布失敗；失敗記進支援日誌，
/// 經 <see cref="IAuditedReportPublishingStore"/> 發布時另回提醒（使用者 2026-10-19 裁定）。
/// 檔案 store 只負責暫存改名與 manifest；清理功能已於 2026-09-02 移除。
/// </summary>
internal sealed class AuditedReportArtifactStore(
    IReportArtifactStore inner,
    IProjectAuditLog auditLog,
    ILogger? logger = null) : IReportArtifactStore, IReportArtifactPublishingStore, IAuditedReportPublishingStore
{
    public async Task<ReportArtifact> WriteAsync(
        string projectId,
        ReportArtifactWriteRequest request,
        CancellationToken cancellationToken)
    {
        var publication = await PublishAsync(
            projectId,
            [request.Kind],
            async () => [await inner.WriteAsync(projectId, request, cancellationToken)],
            cancellationToken);
        return publication.Artifacts[0];
    }

    public async Task<IReadOnlyList<ReportArtifact>> WriteBatchAsync(
        string projectId,
        IReadOnlyList<ReportArtifactWriteRequest> requests,
        CancellationToken cancellationToken)
    {
        var publication = await PublishAsync(
            projectId,
            requests.Select(static request => request.Kind),
            () => inner.WriteBatchAsync(projectId, requests, cancellationToken),
            cancellationToken);
        return publication.Artifacts;
    }

    public async Task<ReportArtifact> WriteWithPublishingAsync(
        string projectId,
        ReportArtifactWriteRequest request,
        Action<ReportArtifactKind> publishingArtifact,
        CancellationToken cancellationToken) =>
        (await WriteAuditedAsync(projectId, request, publishingArtifact, cancellationToken)).Artifacts[0];

    public async Task<IReadOnlyList<ReportArtifact>> WriteBatchWithPublishingAsync(
        string projectId,
        IReadOnlyList<ReportArtifactWriteRequest> requests,
        Action<ReportArtifactKind> publishingArtifact,
        CancellationToken cancellationToken) =>
        (await WriteBatchAuditedAsync(projectId, requests, publishingArtifact, cancellationToken)).Artifacts;

    public Task<AuditedReportPublication> WriteAuditedAsync(
        string projectId,
        ReportArtifactWriteRequest request,
        Action<ReportArtifactKind> publishingArtifact,
        CancellationToken cancellationToken)
    {
        var publishingStore = PublishingStore();
        return PublishAsync(
            projectId,
            [request.Kind],
            async () => [await publishingStore.WriteWithPublishingAsync(
                projectId,
                request,
                publishingArtifact,
                cancellationToken)],
            cancellationToken);
    }

    public Task<AuditedReportPublication> WriteBatchAuditedAsync(
        string projectId,
        IReadOnlyList<ReportArtifactWriteRequest> requests,
        Action<ReportArtifactKind> publishingArtifact,
        CancellationToken cancellationToken)
    {
        var publishingStore = PublishingStore();
        return PublishAsync(
            projectId,
            requests.Select(static request => request.Kind),
            () => publishingStore.WriteBatchWithPublishingAsync(
                projectId,
                requests,
                publishingArtifact,
                cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<ReportArtifact>> ListAsync(
        string projectId,
        CancellationToken cancellationToken) => inner.ListAsync(projectId, cancellationToken);

    public Task<string> ResolvePathAsync(
        string projectId,
        string artifactId,
        CancellationToken cancellationToken) => inner.ResolvePathAsync(projectId, artifactId, cancellationToken);

    public Task<int> MarkStaleAsync(
        string projectId,
        ReportArtifactKind kind,
        CancellationToken cancellationToken) => inner.MarkStaleAsync(projectId, kind, cancellationToken);

    public Task<int> MarkStaleAsync(
        string projectId,
        Func<ReportArtifact, bool> predicate,
        CancellationToken cancellationToken) => inner.MarkStaleAsync(projectId, predicate, cancellationToken);

    public Task<IAsyncDisposable> AcquireProjectDeletionLeaseAsync(
        string projectId,
        CancellationToken cancellationToken) =>
        inner.AcquireProjectDeletionLeaseAsync(projectId, cancellationToken);

    private IReportArtifactPublishingStore PublishingStore() =>
        inner as IReportArtifactPublishingStore
        ?? throw new InvalidOperationException("Production report store lacks publishing-boundary support.");

    private async Task<AuditedReportPublication> PublishAsync(
        string projectId,
        IEnumerable<ReportArtifactKind> kinds,
        Func<Task<IReadOnlyList<ReportArtifact>>> write,
        CancellationToken cancellationToken)
    {
        var replacedCount = await ExistingCountAsync(projectId, kinds, cancellationToken);
        var artifacts = await write();
        var warning = await RecordPublishedAsync(projectId, artifacts, replacedCount);
        return new AuditedReportPublication(artifacts, warning);
    }

    private async Task<long> ExistingCountAsync(
        string projectId,
        IEnumerable<ReportArtifactKind> kinds,
        CancellationToken cancellationToken)
    {
        // Working Paper 每次新增版本，舊檔仍保留，不計入同名覆蓋的數量。
        var selected = kinds.Where(static kind => kind != ReportArtifactKind.WorkingPaper).ToHashSet();
        var artifacts = await inner.ListAsync(projectId, cancellationToken);
        return artifacts.LongCount(artifact => selected.Contains(artifact.Kind));
    }

    private Task<string?> RecordPublishedAsync(
        string projectId,
        IReadOnlyList<ReportArtifact> artifacts,
        long replacedCount)
    {
        var targetId = string.Join(
            "+",
            artifacts.Select(static artifact => ReportArtifactKindValues.ToValue(artifact.Kind))
                .Order(StringComparer.Ordinal));
        return CommittedAuditLog.AppendAsync(
            auditLog,
            projectId,
            ProjectAuditEvent.Create(
                ProjectAuditOperations.ReportPublish,
                ProjectAuditTargetTypes.ReportCatalog,
                targetId,
                artifacts.Count,
                replacedCount),
            logger,
            CommittedAuditLog.ReportAppendFailedWarning);
    }
}
