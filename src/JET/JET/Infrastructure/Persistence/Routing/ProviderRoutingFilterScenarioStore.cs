using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由已存篩選情境存取(比照 <see cref="ProviderRoutingGlRepository"/>)。</summary>
public sealed class ProviderRoutingFilterScenarioStore(
    ProjectProviderResolver resolver,
    IFilterScenarioStore sqlite,
    IFilterScenarioStore sqlServer,
    IFilterScenarioStore duckDb) : IFilterScenarioStore
{
    public async Task ReplaceAllAsync(
        string projectId, IReadOnlyList<SavedFilterScenario> scenarios, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .ReplaceAllAsync(projectId, scenarios, cancellationToken);
    }

    public async Task<IReadOnlyList<SavedFilterScenario>> ListAsync(string projectId, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb).ListAsync(projectId, cancellationToken);
    }
}
