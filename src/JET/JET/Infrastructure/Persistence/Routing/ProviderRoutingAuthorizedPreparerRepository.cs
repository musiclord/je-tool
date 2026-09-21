using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由授權編製人員清單存取(比照 <see cref="ProviderRoutingAccountMappingRepository"/>)。</summary>
public sealed class ProviderRoutingAuthorizedPreparerRepository(
    ProjectProviderResolver resolver,
    IAuthorizedPreparerStore sqlite,
    IAuthorizedPreparerStore sqlServer,
    IAuthorizedPreparerStore duckDb) : IAuthorizedPreparerStore, IAuthorizedPreparerImportPersistence
{
    public async Task ClearAsync(string projectId, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb).ClearAsync(projectId, cancellationToken);
    }

    public async Task<AuthorizedPreparerImportResult> ImportAsync(
        string projectId, ImportSourceDescriptor source, IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .ImportAsync(projectId, source, columns, rows, cancellationToken);
    }

    async Task<AuthorizedPreparerImportResult> IAuthorizedPreparerImportPersistence.ImportAsync(
        string projectId,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        AuthorizedPreparerProjection projection,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        var selected = ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb);
        var persistence = selected as IAuthorizedPreparerImportPersistence
            ?? throw new InvalidOperationException(
                $"Provider store '{selected.GetType().FullName}' 未實作 typed 授權編製人員匯入縫。");
        return await persistence.ImportAsync(
            projectId,
            source,
            columns,
            projection,
            rows,
            cancellationToken);
    }

    public async Task<long> CountAsync(string projectId, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb).CountAsync(projectId, cancellationToken);
    }

    public async Task<AuthorizedPreparerState?> FindStateAsync(string projectId, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb).FindStateAsync(projectId, cancellationToken);
    }
}
