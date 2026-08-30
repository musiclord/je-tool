using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由 filter.commit 整批發布。</summary>
public sealed class ProviderRoutingFilterCommitRepository(
    ProjectProviderResolver resolver,
    IFilterCommitRepository sqlite,
    IFilterCommitRepository sqlServer,
    IFilterCommitRepository duckDb) : IFilterCommitRepository
{
    public async Task CommitAsync(
        string projectId,
        IReadOnlyList<FilterCommitItem> items,
        FilterRuleContext context,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .CommitAsync(projectId, items, context, cancellationToken);
    }
}
