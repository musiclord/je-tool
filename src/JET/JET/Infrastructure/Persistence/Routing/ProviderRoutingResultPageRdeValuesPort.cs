using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由 bounded result-page RDE values reader。</summary>
public sealed class ProviderRoutingResultPageRdeValuesPort(
    ProjectProviderResolver resolver,
    IResultPageRdeValuesPort sqlite,
    IResultPageRdeValuesPort sqlServer,
    IResultPageRdeValuesPort duckDb) : IResultPageRdeValuesPort
{
    public async Task<IReadOnlyList<ResultPageRdeValue>> ReadAsync(
        string projectId,
        IReadOnlyList<long> entryIds,
        CancellationToken cancellationToken)
    {
        ResultPageRdeValueBatch.Validate(entryIds);
        if (entryIds.Count == 0)
        {
            return [];
        }

        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .ReadAsync(projectId, entryIds, cancellationToken);
    }
}
