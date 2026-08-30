using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 既有 public prescreen repository 的 provider-routing compatibility surface。
/// Production typed lifecycle 使用 <see cref="ProviderRoutingPrescreenFactsPort"/>；
/// 此型別保留給 assembly 外既有 <see cref="IPrescreenRunRepository"/> caller。
/// </summary>
public sealed class ProviderRoutingPrescreenRunRepository(
    ProjectProviderResolver resolver,
    IPrescreenRunRepository sqlite,
    IPrescreenRunRepository sqlServer,
    IPrescreenRunRepository duckDb) : IPrescreenRunRepository
{
    public async Task<PrescreenRunResult> RunAsync(
        string projectId,
        PrescreenRunInput input,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .RunAsync(projectId, input, cancellationToken);
    }
}
