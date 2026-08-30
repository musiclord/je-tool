using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由命中落地（比照 <see cref="ProviderRoutingDataPreviewRepository"/>）。
/// 介面 <see cref="IFilterRunMaterializer"/> 置於 Domain（供 Application 注入）；本檔只放路由實作。</summary>
public sealed class ProviderRoutingFilterRunMaterializer(
    ProjectProviderResolver resolver,
    IFilterRunMaterializer sqlite,
    IFilterRunMaterializer sqlServer,
    IFilterRunMaterializer duckDb) : IFilterRunMaterializer
{
    public async Task MaterializeAsync(
        string projectId,
        IReadOnlyList<MaterializableScenario> scenarios,
        FilterRuleContext context,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .MaterializeAsync(projectId, scenarios, context, cancellationToken);
    }
}
