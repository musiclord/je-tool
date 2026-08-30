using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由欄位配對狀態存取(比照 <see cref="ProviderRoutingGlRepository"/>)。</summary>
public sealed class ProviderRoutingMappingStateStore(
    ProjectProviderResolver resolver,
    IMappingStateStore sqlite,
    IMappingStateStore sqlServer,
    IMappingStateStore duckDb) : IMappingStateStore
{
    public async Task SaveAsync(string projectId, CommittedMapping mapping, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb).SaveAsync(projectId, mapping, cancellationToken);
    }

    public async Task<CommittedMapping?> FindAsync(string projectId, DatasetKind kind, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb).FindAsync(projectId, kind, cancellationToken);
    }
}
