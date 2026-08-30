using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由空值/期外日期分頁(比照 <see cref="ProviderRoutingCompletenessDiffPageRepository"/>)。</summary>
public sealed class ProviderRoutingNullRecordsPageRepository(
    ProjectProviderResolver resolver,
    INullRecordsPageRepository sqlite,
    INullRecordsPageRepository sqlServer,
    INullRecordsPageRepository duckDb) : INullRecordsPageRepository
{
    public async Task<PageResult<NullRecordRow>> GetPageAsync(
        string projectId,
        NullRecordCategory category,
        string periodStart,
        string periodEnd,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .GetPageAsync(projectId, category, periodStart, periodEnd, request, cancellationToken);
    }
}
