using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// Provider prescreen repositories 共用的純技術執行輸入。Production typed path
/// 直接消費 AuditCore plan 已裁定的 gates；只有既有 public repository contract
/// 的 compatibility path 依原 PrescreenRunInput 語意建立相同 gates。
/// </summary>
internal sealed record PrescreenExecutionInput(
    string? LastPeriodStart,
    string PeriodStart,
    string PeriodEnd,
    int MoneyScale,
    int ZerosThreshold,
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
            plan.ZerosThreshold,
            request.NonWorkingDays,
            plan.RunPostPeriodApproval,
            plan.RunCreatorSummary,
            plan.RunUnexpectedAccountPair,
            plan.RunWeekendApproval,
            plan.RunHolidayPosting,
            plan.RunHolidayApproval,
            plan.RunNonAuthorizedPreparer);
    }

    internal static PrescreenExecutionInput FromCompatibility(PrescreenRunInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new PrescreenExecutionInput(
            input.LastPeriodStart,
            input.PeriodStart,
            input.PeriodEnd,
            input.MoneyScale,
            TrailingZeroThreshold.DefaultZerosThreshold,
            input.NonWorkingDays,
            RunPostPeriodApproval: input.HasApprovalDate && input.LastPeriodStart is not null,
            RunCreatorSummary: input.HasCreatedBy,
            RunUnexpectedAccountPair: input.RunUnexpectedAccountPair,
            RunWeekendApproval: input.HasApprovalDate,
            RunHolidayPosting: input.HasHolidays,
            RunHolidayApproval: input.HasHolidays && input.HasApprovalDate,
            RunNonAuthorizedPreparer: input.HasAuthorizedPreparers);
    }
}
