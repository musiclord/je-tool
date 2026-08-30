using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由科目配對全列匯出(比照 <see cref="ProviderRoutingCreatorSummaryExportRepository"/>)。</summary>
public sealed class ProviderRoutingAccountMappingExportRepository(
    ProjectProviderResolver resolver,
    IAccountMappingExportRepository sqlite,
    IAccountMappingExportRepository sqlServer,
    IAccountMappingExportRepository duckDb) : IAccountMappingExportRepository
{
    public async Task<IReadOnlyList<AccountMappingExportRow>> FetchAllAsync(
        string projectId, string periodStart, string periodEnd, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .FetchAllAsync(projectId, periodStart, periodEnd, cancellationToken);
    }

    public async Task<IReadOnlyList<AccountMappingTemplateRow>> FetchTemplateRowsAsync(
        string projectId, string periodStart, string periodEnd, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .FetchTemplateRowsAsync(projectId, periodStart, periodEnd, cancellationToken);
    }
}
