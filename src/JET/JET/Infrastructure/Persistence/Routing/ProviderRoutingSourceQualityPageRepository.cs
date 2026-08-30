using JET.Domain;

namespace JET.Infrastructure;

public sealed class ProviderRoutingSourceQualityPageRepository(
    ProjectProviderResolver resolver,
    ISourceQualityPageRepository sqlite,
    ISourceQualityPageRepository sqlServer,
    ISourceQualityPageRepository duckDb) : ISourceQualityPageRepository
{
    public async Task<PageResult<SourceQualityFindingRow>> GetPageAsync(
        string projectId,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .GetPageAsync(projectId, request, cancellationToken);
    }
}
