using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 匯出預篩選報告前，替 AuditCore 的 <see cref="PrescreenReportPlan"/> 查每一種明細的命中筆數。
/// 筆數由 <see cref="IPrescreenPageRepository"/> 用集合式 SQL 計算；
/// 不適用的明細不查，這裡也不讀任何明細列。
/// </summary>
internal sealed class PrescreenReportPlanningFactsPort(IPrescreenPageRepository pages)
    : IPrescreenReportPlanningFactsPort
{
    public async Task<PrescreenReportPlanningFacts> ExecuteAsync(
        PrescreenReportPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.IsFinalized)
        {
            throw new InvalidOperationException("PrescreenReportPlan 已 Finalize。");
        }

        var request = plan.Request;
        var counts = new Dictionary<PrescreenReportDetailKind, PrescreenHitCounts>();
        foreach (var detail in plan.Details.Where(detail => detail.IsApplicable))
        {
            cancellationToken.ThrowIfCancellationRequested();
            counts[detail.Kind] = await pages.GetCountsAsync(
                request.ProjectId,
                RuleKey(detail.Kind),
                request.RuleContext,
                cancellationToken).ConfigureAwait(false);
        }

        return new PrescreenReportPlanningFacts(counts);
    }

    private static string RuleKey(PrescreenReportDetailKind kind) => kind switch
    {
        PrescreenReportDetailKind.PrepPeriodApproval =>
            PrescreenRuleKeys.PrepPeriodApproval,
        PrescreenReportDetailKind.SuspiciousKeywords =>
            PrescreenRuleKeys.SuspiciousKeywords,
        PrescreenReportDetailKind.UnexpectedAccountPair =>
            PrescreenRuleKeys.UnexpectedAccountPair,
        PrescreenReportDetailKind.TrailingZeros =>
            PrescreenRuleKeys.TrailingZeros,
        PrescreenReportDetailKind.BlankDescription =>
            PrescreenRuleKeys.BlankDescription,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的 Pre-screening detail kind。")
    };
}
