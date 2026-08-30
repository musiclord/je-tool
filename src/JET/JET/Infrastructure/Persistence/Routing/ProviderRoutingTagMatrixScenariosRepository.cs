using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由 tag 矩陣情境摘要命中數(比照 <see cref="ProviderRoutingFilterHitsPageRepository"/>)。</summary>
public sealed class ProviderRoutingTagMatrixScenariosRepository(
    ProjectProviderResolver resolver,
    ITagMatrixScenariosRepository sqlite,
    ITagMatrixScenariosRepository sqlServer,
    ITagMatrixScenariosRepository duckDb) : ITagMatrixScenariosRepository
{
    public async Task<IReadOnlyDictionary<int, (long VoucherHitCount, long RowHitCount)>> GetCountsAsync(
        string projectId, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .GetCountsAsync(projectId, cancellationToken);
    }
}
