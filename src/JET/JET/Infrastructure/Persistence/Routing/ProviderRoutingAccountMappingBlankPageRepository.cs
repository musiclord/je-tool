using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由分類留白科目分頁（比照 <see cref="ProviderRoutingCompletenessDiffPageRepository"/>）。</summary>
public sealed class ProviderRoutingAccountMappingBlankPageRepository(
    ProjectProviderResolver resolver,
    IAccountMappingBlankPageRepository sqlite,
    IAccountMappingBlankPageRepository sqlServer,
    IAccountMappingBlankPageRepository duckDb) : IAccountMappingBlankPageRepository
{
    public async Task<PageResult<AccountMappingBlankAccount>> GetPageAsync(
        string projectId, PageRequest request, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .GetPageAsync(projectId, request, cancellationToken);
    }
}
