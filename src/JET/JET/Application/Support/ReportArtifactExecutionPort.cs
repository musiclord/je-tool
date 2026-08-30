using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// Application-owned persistence orchestration for the four formal report exports.
/// It checks Application-shaped content writers against the pure AuditCore plan,
/// then preserves the existing single-write or atomic-batch store call.
/// </summary>
internal sealed class ReportArtifactExecutionPort(
    IReportArtifactStore artifactStore,
    IReadOnlyList<ReportArtifactWriteRequest> requests,
    Action<ReportArtifactKind>? publishingArtifact = null)
    : IReportExportFactsPort
{
    public async Task<ReportExportFacts> ExecuteAsync(
        ReportExportPlan plan,
        CancellationToken cancellationToken)
    {
        EnsureRequestsMatchPlan(plan);

        IReadOnlyList<ReportArtifact> artifacts;
        if (plan.UseAtomicBatch)
        {
            artifacts = publishingArtifact is not null
                && artifactStore is IReportArtifactPublishingStore publishingStore
                ? await publishingStore.WriteBatchWithPublishingAsync(
                    plan.Request.ProjectId,
                    requests,
                    publishingArtifact,
                    cancellationToken)
                : await artifactStore.WriteBatchAsync(
                    plan.Request.ProjectId,
                    requests,
                    cancellationToken);
        }
        else
        {
            if (requests.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Single report export '{plan.Request.ActionName}' 必須恰有一個 content writer。");
            }

            var artifact = publishingArtifact is not null
                && artifactStore is IReportArtifactPublishingStore publishingStore
                ? await publishingStore.WriteWithPublishingAsync(
                    plan.Request.ProjectId,
                    requests[0],
                    publishingArtifact,
                    cancellationToken)
                : await artifactStore.WriteAsync(
                    plan.Request.ProjectId,
                    requests[0],
                    cancellationToken);
            artifacts = [artifact];
        }

        return new ReportExportFacts(artifacts);
    }

    private void EnsureRequestsMatchPlan(ReportExportPlan plan)
    {
        if (requests.Count != plan.ArtifactKinds.Count)
        {
            throw new InvalidOperationException(
                $"Report export '{plan.Request.ActionName}' 的 content writer 數量"
                + "與 plan 不一致。");
        }

        for (var index = 0; index < requests.Count; index++)
        {
            if (requests[index].Kind != plan.ArtifactKinds[index]
                || !SourceRefsEqual(requests[index].SourceRef, plan.SourceRef))
            {
                throw new InvalidOperationException(
                    $"Report export '{plan.Request.ActionName}' 的第 {index + 1} 個 content writer"
                    + "未遵守 plan。");
            }
        }
    }

    private static bool SourceRefsEqual(
        ReportArtifactSourceRefs left,
        ReportArtifactSourceRefs right) =>
        string.Equals(
            left.ValidationRunId,
            right.ValidationRunId,
            StringComparison.Ordinal)
        && string.Equals(
            left.PrescreenRunId,
            right.PrescreenRunId,
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
