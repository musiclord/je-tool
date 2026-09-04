using JET.Domain;

namespace JET.Application;

/// <summary>
/// 正式報告清單的單一 audit 裝飾器：成功發布或汰換後 append 一筆 project-local audit event。
/// 檔案 store 只負責暫存改名與 manifest；清理功能已於 2026-09-02 移除。
/// </summary>
internal sealed class AuditedReportArtifactStore(
    IReportArtifactStore inner,
    IProjectAuditLog auditLog) : IReportArtifactStore, IReportArtifactPublishingStore
{
    public async Task<ReportArtifact> WriteAsync(
        string projectId,
        ReportArtifactWriteRequest request,
        CancellationToken cancellationToken)
    {
        var replacedCount = await ExistingCountAsync(projectId, [request.Kind], cancellationToken);
        var artifact = await inner.WriteAsync(projectId, request, cancellationToken);
        await RecordPublishedAsync(projectId, [artifact], replacedCount);
        return artifact;
    }

    public async Task<IReadOnlyList<ReportArtifact>> WriteBatchAsync(
        string projectId,
        IReadOnlyList<ReportArtifactWriteRequest> requests,
        CancellationToken cancellationToken)
    {
        var replacedCount = await ExistingCountAsync(
            projectId,
            requests.Select(static request => request.Kind),
            cancellationToken);
        var artifacts = await inner.WriteBatchAsync(projectId, requests, cancellationToken);
        await RecordPublishedAsync(projectId, artifacts, replacedCount);
        return artifacts;
    }

    public async Task<ReportArtifact> WriteWithPublishingAsync(
        string projectId,
        ReportArtifactWriteRequest request,
        Action<ReportArtifactKind> publishingArtifact,
        CancellationToken cancellationToken)
    {
        var publishingStore = inner as IReportArtifactPublishingStore
            ?? throw new InvalidOperationException("Production report store lacks publishing-boundary support.");
        var replacedCount = await ExistingCountAsync(projectId, [request.Kind], cancellationToken);
        var artifact = await publishingStore.WriteWithPublishingAsync(
            projectId,
            request,
            publishingArtifact,
            cancellationToken);
        await RecordPublishedAsync(projectId, [artifact], replacedCount);
        return artifact;
    }

    public async Task<IReadOnlyList<ReportArtifact>> WriteBatchWithPublishingAsync(
        string projectId,
        IReadOnlyList<ReportArtifactWriteRequest> requests,
        Action<ReportArtifactKind> publishingArtifact,
        CancellationToken cancellationToken)
    {
        var publishingStore = inner as IReportArtifactPublishingStore
            ?? throw new InvalidOperationException("Production report store lacks publishing-boundary support.");
        var replacedCount = await ExistingCountAsync(
            projectId,
            requests.Select(static request => request.Kind),
            cancellationToken);
        var artifacts = await publishingStore.WriteBatchWithPublishingAsync(
            projectId,
            requests,
            publishingArtifact,
            cancellationToken);
        await RecordPublishedAsync(projectId, artifacts, replacedCount);
        return artifacts;
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

    private Task RecordPublishedAsync(
        string projectId,
        IReadOnlyList<ReportArtifact> artifacts,
        long replacedCount)
    {
        var targetId = string.Join(
            "+",
            artifacts.Select(static artifact => ReportArtifactKindValues.ToValue(artifact.Kind))
                .Order(StringComparer.Ordinal));
        return auditLog.AppendAsync(
            projectId,
            ProjectAuditEvent.Create(
                ProjectAuditOperations.ReportPublish,
                ProjectAuditTargetTypes.ReportCatalog,
                targetId,
                artifacts.Count,
                replacedCount),
            CancellationToken.None);
    }
}
