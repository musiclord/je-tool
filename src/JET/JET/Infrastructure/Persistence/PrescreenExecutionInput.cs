using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// Provider prescreen repositories 共用的純技術執行輸入，只由 AuditCore plan 已裁定的 gates 建立。
/// </summary>
internal sealed record PrescreenExecutionInput(
    string? PrepStartDate,
    string PeriodStart,
    string PeriodEnd,
    int MoneyScale,
    IReadOnlyList<int>? NonWorkingDays,
    bool RunPrepPeriodApproval,
    bool RunPreparerSummary,
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
            request.PrepStartDate,
            request.PeriodStart,
            request.PeriodEnd,
            request.MoneyScale,
            request.NonWorkingDays,
            plan.RunPrepPeriodApproval,
            plan.RunPreparerSummary,
            plan.RunUnexpectedAccountPair,
            plan.RunWeekendApproval,
            plan.RunHolidayPosting,
            plan.RunHolidayApproval,
            plan.RunNonAuthorizedPreparer);
    }
}
