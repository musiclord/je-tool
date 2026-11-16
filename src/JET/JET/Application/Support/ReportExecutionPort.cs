using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 四種正式報告匯出共用的寫檔步驟，由 Application 負責。先核對 handler 準備的寫檔請求和 AuditCore 算出的計畫一致：
/// 份數、報告種類與來源版本都要相同。再照計畫呼叫 store：計畫要求整批時用整批寫入，整批一起發布或一起不發布；
/// 否則只有一份，單獨寫入。
/// </summary>
internal sealed class ReportExecutionPort(
    IReportStore reportStore,
    IReadOnlyList<ReportWriteRequest> requests,
    Action<ReportKind>? publishingReport = null)
    : IReportExportFactsPort
{
    public async Task<ReportExportFacts> ExecuteAsync(
        ReportExportPlan plan,
        CancellationToken cancellationToken)
    {
        EnsureRequestsMatchPlan(plan);
        if (!plan.UseAtomicBatch && requests.Count != 1)
        {
            throw new InvalidOperationException(
                $"Single report export '{plan.Request.ActionName}' 必須恰有一個 content writer。");
        }

        // 正式組裝的報告清單會寫操作紀錄；經這個介面發布才拿得到寫不進操作紀錄時的提醒。
        if (publishingReport is not null && reportStore is IAuditedReportPublishingStore auditedStore)
        {
            var publication = plan.UseAtomicBatch
                ? await auditedStore.WriteBatchAuditedAsync(
                    plan.Request.ProjectId,
                    requests,
                    publishingReport,
                    cancellationToken)
                : await auditedStore.WriteAuditedAsync(
                    plan.Request.ProjectId,
                    requests[0],
                    publishingReport,
                    cancellationToken);
            return new ReportExportFacts(publication.Reports, publication.OperationLogWarning);
        }

        IReadOnlyList<Report> reports;
        if (plan.UseAtomicBatch)
        {
            reports = publishingReport is not null
                && reportStore is IReportPublishingStore publishingStore
                ? await publishingStore.WriteBatchWithPublishingAsync(
                    plan.Request.ProjectId,
                    requests,
                    publishingReport,
                    cancellationToken)
                : await reportStore.WriteBatchAsync(
                    plan.Request.ProjectId,
                    requests,
                    cancellationToken);
        }
        else
        {
            var report = publishingReport is not null
                && reportStore is IReportPublishingStore publishingStore
                ? await publishingStore.WriteWithPublishingAsync(
                    plan.Request.ProjectId,
                    requests[0],
                    publishingReport,
                    cancellationToken)
                : await reportStore.WriteAsync(
                    plan.Request.ProjectId,
                    requests[0],
                    cancellationToken);
            reports = [report];
        }

        return new ReportExportFacts(reports);
    }

    private void EnsureRequestsMatchPlan(ReportExportPlan plan)
    {
        if (requests.Count != plan.ReportKinds.Count)
        {
            throw new InvalidOperationException(
                $"Report export '{plan.Request.ActionName}' 的 content writer 數量"
                + "與 plan 不一致。");
        }

        for (var index = 0; index < requests.Count; index++)
        {
            if (requests[index].Kind != plan.ReportKinds[index]
                || !SourceRefsEqual(requests[index].SourceRef, plan.SourceRef))
            {
                throw new InvalidOperationException(
                    $"Report export '{plan.Request.ActionName}' 的第 {index + 1} 個 content writer"
                    + "未遵守 plan。");
            }
        }
    }

    private static bool SourceRefsEqual(
        ReportSourceRefs left,
        ReportSourceRefs right) =>
        string.Equals(
            left.ValidationResultId,
            right.ValidationResultId,
            StringComparison.Ordinal)
        && string.Equals(
            left.PrescreenResultId,
            right.PrescreenResultId,
            StringComparison.Ordinal)
        && string.Equals(
            left.ScenarioRevision,
            right.ScenarioRevision,
            StringComparison.Ordinal)
        && Positions(left.ScenarioPositions).SequenceEqual(
            Positions(right.ScenarioPositions));

    private static IReadOnlyList<int> Positions(IReadOnlyList<int>? positions) =>
        positions ?? Array.Empty<int>();
}
