using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 照 AuditCore 排好的欄位配對計畫，呼叫 <see cref="IGlRepository"/> 或 <see cref="ITbRepository"/>
/// 把匯入暫存表的資料轉寫進 GL 或 TB。參數都取自計畫，這裡不做判斷。
/// </summary>
internal sealed class MappingFactsPort(
    IGlRepository glRepository,
    ITbRepository tbRepository) : IMappingFactsPort
{
    public Task<ProjectionResult> ExecuteAsync(
        GlMappingPlan plan,
        CancellationToken cancellationToken,
        Action<ProjectionProgress>? progress = null) =>
        glRepository.ProjectStagingToTargetAsync(
            plan.Request.ProjectId,
            plan.Request.BatchId,
            plan.Spec,
            plan.Request.MoneyScale,
            plan.Request.DateOptions,
            plan.GlExcludedPopulationPlan.PeriodStart,
            plan.GlExcludedPopulationPlan.PeriodEnd,
            plan.GlExcludedPopulationPlan.PostingStatusMapped,
            plan.GlExcludedPopulationPlan.PostingStatusPolicy,
            plan.Request.CommittedUtc,
            cancellationToken,
            progress);

    public Task<ProjectionResult> ExecuteAsync(
        TbMappingPlan plan,
        CancellationToken cancellationToken,
        Action<ProjectionProgress>? progress = null) =>
        tbRepository.ProjectStagingToTargetAsync(
            plan.Request.ProjectId,
            plan.Request.BatchId,
            plan.Spec,
            plan.Request.MoneyScale,
            plan.Request.CommittedUtc,
            cancellationToken,
            progress);
}
