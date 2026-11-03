using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>正式報告發布的結果：發布的報告，以及寫不進操作紀錄時給審計員的提醒（寫進去時是 null）。</summary>
internal sealed record AuditedReportPublication(IReadOnlyList<Report> Reports, string? OperationLogWarning);

/// <summary>
/// 產生報告的動作用這個介面發布，才拿得到寫不進操作紀錄時的提醒，放進回應的 warnings
/// （裁定 2026-10-19-20）。
/// </summary>
internal interface IAuditedReportPublishingStore
{
    Task<AuditedReportPublication> WriteAuditedAsync(
        string projectId,
        ReportWriteRequest request,
        Action<ReportKind> publishingReport,
        CancellationToken cancellationToken);

    Task<AuditedReportPublication> WriteBatchAuditedAsync(
        string projectId,
        IReadOnlyList<ReportWriteRequest> requests,
        Action<ReportKind> publishingReport,
        CancellationToken cancellationToken);
}

/// <summary>
/// 正式報告清單的單一 audit 裝飾器：成功發布或汰換後 append 一筆 project-local audit event。
/// 寫不進操作紀錄時報告檔已經發布，兩種案件都不讓這次發布失敗；失敗記進支援日誌，
/// 經 <see cref="IAuditedReportPublishingStore"/> 發布時另回提醒（裁定 2026-10-19-20）。
/// 檔案 store 只負責暫存改名與 manifest；清理功能已於 2026-09-02 移除。
/// </summary>
internal sealed class AuditedReportStore(
    IReportStore inner,
    IProjectOperationLog operationLog,
    ILogger? logger = null) : IReportStore, IReportPublishingStore, IAuditedReportPublishingStore
{
    public async Task<Report> WriteAsync(
        string projectId,
        ReportWriteRequest request,
        CancellationToken cancellationToken)
    {
        var publication = await PublishAsync(
            projectId,
            [request.Kind],
            async () => [await inner.WriteAsync(projectId, request, cancellationToken)],
            cancellationToken);
        return publication.Reports[0];
    }

    public async Task<IReadOnlyList<Report>> WriteBatchAsync(
        string projectId,
        IReadOnlyList<ReportWriteRequest> requests,
        CancellationToken cancellationToken)
    {
        var publication = await PublishAsync(
            projectId,
            requests.Select(static request => request.Kind),
            () => inner.WriteBatchAsync(projectId, requests, cancellationToken),
            cancellationToken);
        return publication.Reports;
    }

    public async Task<Report> WriteWithPublishingAsync(
        string projectId,
        ReportWriteRequest request,
        Action<ReportKind> publishingReport,
        CancellationToken cancellationToken) =>
        (await WriteAuditedAsync(projectId, request, publishingReport, cancellationToken)).Reports[0];

    public async Task<IReadOnlyList<Report>> WriteBatchWithPublishingAsync(
        string projectId,
        IReadOnlyList<ReportWriteRequest> requests,
        Action<ReportKind> publishingReport,
        CancellationToken cancellationToken) =>
        (await WriteBatchAuditedAsync(projectId, requests, publishingReport, cancellationToken)).Reports;

    public Task<AuditedReportPublication> WriteAuditedAsync(
        string projectId,
        ReportWriteRequest request,
        Action<ReportKind> publishingReport,
        CancellationToken cancellationToken)
    {
        var publishingStore = PublishingStore();
        return PublishAsync(
            projectId,
            [request.Kind],
            async () => [await publishingStore.WriteWithPublishingAsync(
                projectId,
                request,
                publishingReport,
                cancellationToken)],
            cancellationToken);
    }

    public Task<AuditedReportPublication> WriteBatchAuditedAsync(
        string projectId,
        IReadOnlyList<ReportWriteRequest> requests,
        Action<ReportKind> publishingReport,
        CancellationToken cancellationToken)
    {
        var publishingStore = PublishingStore();
        return PublishAsync(
            projectId,
            requests.Select(static request => request.Kind),
            () => publishingStore.WriteBatchWithPublishingAsync(
                projectId,
                requests,
                publishingReport,
                cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<Report>> ListAsync(
        string projectId,
        CancellationToken cancellationToken) => inner.ListAsync(projectId, cancellationToken);

    public Task<string> ResolvePathAsync(
        string projectId,
        string reportId,
        CancellationToken cancellationToken) => inner.ResolvePathAsync(projectId, reportId, cancellationToken);

    public Task<int> MarkStaleAsync(
        string projectId,
        ReportKind kind,
        CancellationToken cancellationToken) => inner.MarkStaleAsync(projectId, kind, cancellationToken);

    public Task<int> MarkStaleAsync(
        string projectId,
        Func<Report, bool> predicate,
        CancellationToken cancellationToken) => inner.MarkStaleAsync(projectId, predicate, cancellationToken);

    public Task<IAsyncDisposable> AcquireProjectDeletionLeaseAsync(
        string projectId,
        CancellationToken cancellationToken) =>
        inner.AcquireProjectDeletionLeaseAsync(projectId, cancellationToken);

    private IReportPublishingStore PublishingStore() =>
        inner as IReportPublishingStore
        ?? throw new InvalidOperationException("Production report store lacks publishing-boundary support.");

    private async Task<AuditedReportPublication> PublishAsync(
        string projectId,
        IEnumerable<ReportKind> kinds,
        Func<Task<IReadOnlyList<Report>>> write,
        CancellationToken cancellationToken)
    {
        var replacedCount = await ExistingCountAsync(projectId, kinds, cancellationToken);
        var reports = await write();
        var warning = await RecordPublishedAsync(projectId, reports, replacedCount);
        return new AuditedReportPublication(reports, warning);
    }

    private async Task<long> ExistingCountAsync(
        string projectId,
        IEnumerable<ReportKind> kinds,
        CancellationToken cancellationToken)
    {
        // 工作底稿每次新增版本，舊檔仍保留，不計入同名覆蓋的數量。
        var selected = kinds.Where(static kind => kind != ReportKind.WorkingPaper).ToHashSet();
        var reports = await inner.ListAsync(projectId, cancellationToken);
        return reports.LongCount(report => selected.Contains(report.Kind));
    }

    private Task<string?> RecordPublishedAsync(
        string projectId,
        IReadOnlyList<Report> reports,
        long replacedCount)
    {
        var targetId = string.Join(
            "+",
            reports.Select(static report => ReportKindValues.ToValue(report.Kind))
                .Order(StringComparer.Ordinal));
        return CommittedOperationLog.AppendAsync(
            operationLog,
            projectId,
            ProjectOperationEvent.Create(
                ProjectOperations.ReportPublish,
                ProjectOperationTargetTypes.ReportCatalog,
                targetId,
                reports.Count,
                replacedCount),
            logger,
            CommittedOperationLog.ReportAppendFailedWarning);
    }
}
