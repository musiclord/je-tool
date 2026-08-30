using JET.Domain;

namespace JET.Application;

/// <summary>
/// 正式報告 catalog 的單一 audit 裝飾器：成功發布／汰換與明示清理完成後各 append 一筆
/// project-local audit event。檔案 store 仍擁有原子 package publication 與 cleanup journal。
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

    public Task<ReportArtifactCatalog> ReadCatalogAsync(
        string projectId,
        CancellationToken cancellationToken) => inner.ReadCatalogAsync(projectId, cancellationToken);

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

    public async Task<ReportArtifactCleanupResult> CleanupAsync(
        string projectId,
        string expectedCatalogRevision,
        IReadOnlyList<ReportArtifactCleanupCandidate> candidates,
        string requestedBy,
        CancellationToken cancellationToken)
    {
        var result = await inner.CleanupAsync(
            projectId,
            expectedCatalogRevision,
            candidates,
            requestedBy,
            cancellationToken);
        await auditLog.AppendAsync(
            projectId,
            ProjectAuditEvent.Create(
                ProjectAuditOperations.ReportCleanup,
                ProjectAuditTargetTypes.ReportCatalog,
                "formalReports",
                result.DeletedCount),
            CancellationToken.None);
        return result;
    }

    public Task<IAsyncDisposable> AcquireProjectDeletionLeaseAsync(
        string projectId,
        CancellationToken cancellationToken) =>
        inner.AcquireProjectDeletionLeaseAsync(projectId, cancellationToken);

    private async Task<long> ExistingCountAsync(
        string projectId,
        IEnumerable<ReportArtifactKind> kinds,
        CancellationToken cancellationToken)
    {
        var selected = kinds.ToHashSet();
        var catalog = await inner.ReadCatalogAsync(projectId, cancellationToken);
        return catalog.Artifacts.LongCount(artifact => selected.Contains(artifact.Kind));
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
