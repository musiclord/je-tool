using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由規則執行結果存取(比照 <see cref="ProviderRoutingGlRepository"/>)。</summary>
public sealed class ProviderRoutingRuleRunStore(
    ProjectProviderResolver resolver,
    IRuleRunStore sqlite,
    IRuleRunStore sqlServer,
    IRuleRunStore duckDb) : IRuleRunStore
{
    public async Task SaveAsync(string projectId, RuleRunRecord record, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb).SaveAsync(projectId, record, cancellationToken);
    }

    public async Task<RuleRunRecord?> FindLatestAsync(string projectId, string runKind, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb).FindLatestAsync(projectId, runKind, cancellationToken);
    }
}
