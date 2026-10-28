using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// Provider prescreen repositories 共用的純技術執行輸入，只由 AuditCore plan 已裁定的 gates 建立。
/// </summary>
internal sealed record PrescreenExecutionInput(
    string? LastPeriodStart,
    string PeriodStart,
    string PeriodEnd,
    int MoneyScale,
    IReadOnlyList<int>? NonWorkingDays,
    bool RunPostPeriodApproval,
    bool RunCreatorSummary,
    bool RunUnexpectedAccountPair,
    bool RunWeekendApproval,
    bool RunHolidayPosting,
    bool RunHolidayApproval,
    bool RunNonAuthorizedPreparer)
{
    internal static PrescreenExecutionInput FromPlan(PrescreenPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var request = plan.Request;
        return new PrescreenExecutionInput(
            request.LastPeriodStart,
            request.PeriodStart,
            request.PeriodEnd,
            request.MoneyScale,
            request.NonWorkingDays,
            plan.RunPostPeriodApproval,
            plan.RunCreatorSummary,
            plan.RunUnexpectedAccountPair,
            plan.RunWeekendApproval,
            plan.RunHolidayPosting,
            plan.RunHolidayApproval,
            plan.RunNonAuthorizedPreparer);
    }
}
