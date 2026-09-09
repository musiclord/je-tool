using JET.Domain;

namespace JET.Infrastructure;

public sealed class ProviderRoutingFilterVoucherRepository(ProjectProviderResolver resolver,
    IFilterVoucherRepository sqlite, IFilterVoucherRepository sqlServer, IFilterVoucherRepository duckDb) : IFilterVoucherRepository
{
    public async Task<string> ReadRevisionAsync(string projectId, CancellationToken ct) =>
        await ProviderSelection.Pick(await resolver.ResolveAsync(projectId, ct), sqlite, sqlServer, duckDb).ReadRevisionAsync(projectId, ct);

    public async Task<FilterVoucherPage> ReadAsync(string projectId, FilterScenarioSpec scenario, FilterRuleContext context,
        string? documentNumber, PageRequest request, CancellationToken ct) =>
        await ProviderSelection.Pick(await resolver.ResolveAsync(projectId, ct), sqlite, sqlServer, duckDb)
            .ReadAsync(projectId, scenario, context, documentNumber, request, ct);
}
