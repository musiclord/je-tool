using JET.Domain;

namespace JET.Infrastructure;

public sealed class ProviderRoutingPrescreenPageRepository(
    ProjectProviderResolver resolver,
    IPrescreenPageRepository sqlite,
    IPrescreenPageRepository sqlServer,
    IPrescreenPageRepository duckDb) : IPrescreenPageRepository
{
    public async Task<PageResult<PrescreenHitRow>> GetPageAsync(
        string projectId,
        string ruleKey,
        FilterRuleContext context,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .GetPageAsync(projectId, ruleKey, context, request, cancellationToken);
    }

    public async Task<PrescreenHitCounts> GetCountsAsync(
        string projectId,
        string ruleKey,
        FilterRuleContext context,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .GetCountsAsync(projectId, ruleKey, context, cancellationToken);
    }
}
