using JET.AuditCore;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由資料驗證 facts port(比照 <see cref="ProviderRoutingGlRepository"/>)。</summary>
internal sealed class ProviderRoutingValidationFactsPort(
    ProjectProviderResolver resolver,
    IValidationFactsPort sqlite,
    IValidationFactsPort sqlServer,
    IValidationFactsPort duckDb) : IValidationFactsPort
{
    public async Task<ValidationFacts> ExecuteAsync(
        ValidationPlan plan,
        CancellationToken cancellationToken)
    {
        var projectId = plan.Request.ProjectId;
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .ExecuteAsync(plan, cancellationToken);
    }
}
