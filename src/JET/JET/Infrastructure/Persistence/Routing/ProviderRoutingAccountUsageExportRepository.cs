using JET.Domain;

namespace JET.Infrastructure;

public sealed class ProviderRoutingAccountUsageExportRepository(
    ProjectProviderResolver resolver,
    IAccountUsageExportRepository sqlite,
    IAccountUsageExportRepository sqlServer,
    IAccountUsageExportRepository duckDb) : IAccountUsageExportRepository
{
    public async Task<IReadOnlyList<AccountUsageExportRow>> FetchAllAsync(
        string projectId,
        string periodStart,
        string periodEnd,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .FetchAllAsync(projectId, periodStart, periodEnd, cancellationToken);
    }
}
