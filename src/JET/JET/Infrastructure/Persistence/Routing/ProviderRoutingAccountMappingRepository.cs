using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由科目配對存取(比照 <see cref="ProviderRoutingGlRepository"/>)。</summary>
public sealed class ProviderRoutingAccountMappingRepository(
    ProjectProviderResolver resolver,
    IAccountMappingStore sqlite,
    IAccountMappingStore sqlServer,
    IAccountMappingStore duckDb) : IAccountMappingStore, IAccountMappingImportPersistence
{
    public async Task<AccountMappingImportResult> ImportAsync(
        string projectId, ImportSourceDescriptor source, IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .ImportAsync(projectId, source, columns, rows, cancellationToken);
    }

    async Task<AccountMappingImportResult> IAccountMappingImportPersistence.ImportAsync(
        string projectId,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        AccountMappingProjection projection,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        var selected = ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb);
        var persistence = selected as IAccountMappingImportPersistence
            ?? throw new InvalidOperationException(
                $"Provider store '{selected.GetType().FullName}' 未實作 typed 科目配對匯入縫。");
        return await persistence.ImportAsync(
            projectId,
            source,
            columns,
            projection,
            rows,
            cancellationToken);
    }

    public async Task<AccountMappingState?> FindStateAsync(string projectId, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb).FindStateAsync(projectId, cancellationToken);
    }
}
