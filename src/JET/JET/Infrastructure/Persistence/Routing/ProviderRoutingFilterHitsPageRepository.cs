using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由篩選命中明細分頁(比照 <see cref="ProviderRoutingNullRecordsPageRepository"/>)。</summary>
public sealed class ProviderRoutingFilterHitsPageRepository(
    ProjectProviderResolver resolver,
    IFilterHitsPageRepository sqlite,
    IFilterHitsPageRepository sqlServer,
    IFilterHitsPageRepository duckDb) : IFilterHitsPageRepository
{
    public async Task<PageResult<FilterHitRow>> GetPageAsync(
        string projectId, int scenarioPosition, int moneyScale, PageRequest request, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .GetPageAsync(projectId, scenarioPosition, moneyScale, request, cancellationToken);
    }
}
