using JET.Domain;

namespace JET.Infrastructure;

public sealed class ProviderRoutingRawGlExportRepository(
    ProjectProviderResolver resolver,
    IRawGlExportRepository sqlite,
    IRawGlExportRepository sqlServer,
    IRawGlExportRepository duckDb) : IRawGlExportRepository
{
    public async Task<IReadOnlyDictionary<long, string>> FetchJsonByEntryIdsAsync(
        string projectId,
        IReadOnlyList<long> entryIds,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .FetchJsonByEntryIdsAsync(projectId, entryIds, cancellationToken);
    }
}
