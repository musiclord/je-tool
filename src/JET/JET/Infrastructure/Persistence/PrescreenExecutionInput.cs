using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// Provider prescreen repositories 共用的純技術執行輸入，只由 AuditCore plan 已判定的 gates 建立。
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
    /// <summary>「分錄金額中有連續 0 的尾數」的 0 的個數，照請求帶入。</summary>
    internal required int ZerosThreshold { get; init; }

    /// <summary>預篩選計數用的條件環境；尾零規則讀這裡的 0 的個數。</summary>
    internal FilterConditionContext FilterContext() =>
        new(MoneyScale, PrepStartDate, PeriodStart, PeriodEnd, NonWorkingDays, GlPopulationScope.AuditPeriod)
        {
            ZerosThreshold = ZerosThreshold
        };

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
            plan.RunNonAuthorizedPreparer)
        {
            ZerosThreshold = request.ZerosThreshold
        };
    }
}
