using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// AuditCore mapping plan 到同一個資料庫組裡 GL 與 TB repository 的 typed facts adapter。
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
            plan.EffectivePopulation.PeriodStart,
            plan.EffectivePopulation.PeriodEnd,
            plan.EffectivePopulation.PostingStatusMapped,
            plan.EffectivePopulation.PostingStatusPolicy,
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
