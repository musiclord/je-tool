using JET.AuditCore;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由預篩選 raw facts 執行。</summary>
internal sealed class ProviderRoutingPrescreenFactsPort(
    ProjectProviderResolver resolver,
    IPrescreenFactsPort sqlite,
    IPrescreenFactsPort sqlServer,
    IPrescreenFactsPort duckDb) : IPrescreenFactsPort
{
    public async Task<PrescreenFacts> ExecuteAsync(
        PrescreenPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var provider = await resolver.ResolveAsync(plan.Request.ProjectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .ExecuteAsync(plan, cancellationToken);
    }
}
