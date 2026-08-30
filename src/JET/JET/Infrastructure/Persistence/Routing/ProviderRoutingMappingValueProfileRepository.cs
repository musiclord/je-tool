using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依目前專案 provider 路由 mapping value profile。</summary>
public sealed class ProviderRoutingMappingValueProfileRepository(
    ProjectProviderResolver resolver,
    IMappingValueProfileRepository sqlite,
    IMappingValueProfileRepository sqlServer,
    IMappingValueProfileRepository duckDb) : IMappingValueProfileRepository
{
    public async Task<MappingValueProfile> GetAsync(
        string projectId,
        string batchId,
        string sourceColumn,
        int limit,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .GetAsync(projectId, batchId, sourceColumn, limit, cancellationToken);
    }
}
