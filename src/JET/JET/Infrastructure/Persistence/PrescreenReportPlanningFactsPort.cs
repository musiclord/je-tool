using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// Pre-screening workbook planning adapter。沿用 provider-routed page repository 的
/// set-based count query；N/A family 不發出查詢，也不讀取 detail page。
/// </summary>
internal sealed class PrescreenReportPlanningFactsPort(IPrescreenPageRepository pages)
    : IPrescreenReportPlanningFactsPort
{
    public async Task<PrescreenReportPlanningFacts> ExecuteAsync(
        PrescreenReportPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

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
        PrescreenReportDetailKind.PostPeriodApproval =>
            PrescreenRuleKeys.PostPeriodApproval,
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
