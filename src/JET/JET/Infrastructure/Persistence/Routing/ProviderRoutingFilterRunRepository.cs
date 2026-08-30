using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由進階篩選預覽(比照 <see cref="ProviderRoutingGlRepository"/>)。</summary>
public sealed class ProviderRoutingFilterRunRepository(
    ProjectProviderResolver resolver,
    IFilterRunRepository sqlite,
    IFilterRunRepository sqlServer,
    IFilterRunRepository duckDb) : IFilterRunRepository
{
    public async Task<FilterPreviewResult> PreviewAsync(
        string projectId, FilterScenarioSpec scenario, FilterRuleContext context, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .PreviewAsync(projectId, scenario, context, cancellationToken);
    }
}
