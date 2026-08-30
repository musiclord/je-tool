using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由匯入批次存取(比照 <see cref="ProviderRoutingGlRepository"/>)。</summary>
public sealed class ProviderRoutingImportRepository(
    ProjectProviderResolver resolver,
    IImportRepository sqlite,
    IImportRepository sqlServer,
    IImportRepository duckDb) : IImportRepository
{
    public async Task<ImportBatchResult> ReplaceBatchAsync(
        string projectId, DatasetKind kind, ImportSourceDescriptor source,
        IReadOnlyList<string> columns, IAsyncEnumerable<StagingRow> rows, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .ReplaceBatchAsync(projectId, kind, source, columns, rows, cancellationToken);
    }

    public async Task<ImportBatchResult> ReplaceBatchAsync(
        string projectId,
        DatasetKind kind,
        IReadOnlyList<ImportSourceInput> sources,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .ReplaceBatchAsync(projectId, kind, sources, cancellationToken);
    }

    public async Task<ImportBatchResult> AppendToBatchAsync(
        string projectId, DatasetKind kind, ImportSourceDescriptor source,
        IReadOnlyList<string> columns, IAsyncEnumerable<StagingRow> rows, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .AppendToBatchAsync(projectId, kind, source, columns, rows, cancellationToken);
    }

    public async Task<ImportBatchResult> AppendToBatchAsync(
        string projectId,
        DatasetKind kind,
        IReadOnlyList<ImportSourceInput> sources,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .AppendToBatchAsync(projectId, kind, sources, cancellationToken);
    }

    public async Task<ImportBatchInfo?> GetLatestBatchAsync(
        string projectId, DatasetKind kind, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .GetLatestBatchAsync(projectId, kind, cancellationToken);
    }
}
