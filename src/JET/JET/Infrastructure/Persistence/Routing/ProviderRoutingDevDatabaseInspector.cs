using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由開發檢視工具(Debug-only;比照 <see cref="ProviderRoutingGlRepository"/>)。</summary>
public sealed class ProviderRoutingDevDatabaseInspector(
    ProjectProviderResolver resolver,
    IDevDatabaseInspector sqlite,
    IDevDatabaseInspector sqlServer,
    IDevDatabaseInspector duckDb) : IDevDatabaseInspector
{
    public async Task<DevDatabaseOverview> GetOverviewAsync(string projectId, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb).GetOverviewAsync(projectId, cancellationToken);
    }

    public async Task<DevTablePage?> GetTablePageAsync(
        string projectId, string tableName, int limit, int offset, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .GetTablePageAsync(projectId, tableName, limit, offset, cancellationToken);
    }
}
