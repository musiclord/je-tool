using JET.AuditCore;

namespace JET.Infrastructure;

/// <summary>依 project provider 精確路由 Validation workbook planning count。</summary>
internal sealed class ProviderRoutingValidationReportPlanningFactsPort(
    ProjectProviderResolver resolver,
    IValidationReportPlanningFactsPort sqlite,
    IValidationReportPlanningFactsPort sqlServer,
    IValidationReportPlanningFactsPort duckDb) : IValidationReportPlanningFactsPort
{
    public async Task<ValidationReportPlanningFacts> ExecuteAsync(
        ValidationReportPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var provider = await resolver.ResolveAsync(plan.Request.ProjectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .ExecuteAsync(plan, cancellationToken);
    }
}
