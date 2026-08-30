using JET.Domain;

namespace JET.Infrastructure;

public sealed class ProviderRoutingResultStaleStateStore(
    ProjectProviderResolver resolver,
    IResultStaleStateStore sqlite,
    IResultStaleStateStore sqlServer,
    IResultStaleStateStore duckDb) : IResultStaleStateStore
{
    public async Task<AuditResultStaleState> ReadAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .ReadAsync(projectId, cancellationToken);
    }
}
