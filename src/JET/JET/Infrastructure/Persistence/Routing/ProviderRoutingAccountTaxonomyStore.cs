using JET.Domain;

namespace JET.Infrastructure;

public sealed class ProviderRoutingAccountTaxonomyStore(
    ProjectProviderResolver resolver,
    IAccountTaxonomyStore sqlite,
    IAccountTaxonomyStore sqlServer,
    IAccountTaxonomyStore duckDb) : IAccountTaxonomyStore
{
    public async Task<AccountTaxonomySnapshot> ReadAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .ReadAsync(projectId, cancellationToken);
    }

    public async Task<AccountTaxonomySnapshot> SaveAsync(
        string projectId,
        int expectedRevision,
        IReadOnlyList<AccountTaxonomyCategory> categories,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .SaveAsync(projectId, expectedRevision, categories, cancellationToken);
    }
}
