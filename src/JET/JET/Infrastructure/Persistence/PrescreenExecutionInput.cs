using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 執行預篩選的 repository 共用的輸入：期間、金額倍率、每週休息日，以及名稱以 Run 開頭的旗標，表示那一條規則這次要不要執行。
/// 要不要執行由 AuditCore 的 <see cref="PrescreenPlan"/> 判定，這裡只照抄。
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
