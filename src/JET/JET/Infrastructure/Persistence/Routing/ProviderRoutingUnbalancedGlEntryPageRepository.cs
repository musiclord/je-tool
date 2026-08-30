using JET.Domain;

namespace JET.Infrastructure;

public sealed class ProviderRoutingUnbalancedGlEntryPageRepository(
    ProjectProviderResolver resolver,
    IUnbalancedGlEntryPageRepository sqlite,
    IUnbalancedGlEntryPageRepository sqlServer,
    IUnbalancedGlEntryPageRepository duckDb) : IUnbalancedGlEntryPageRepository
{
    public async Task<PageResult<long>> GetEntryIdsPageAsync(
        string projectId,
        string periodStart,
        string periodEnd,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .GetEntryIdsPageAsync(projectId, periodStart, periodEnd, request, cancellationToken);
    }
}
