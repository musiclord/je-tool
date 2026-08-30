using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由 Legacy 欄位定義 facts；caller 不接觸 provider SQL。</summary>
internal sealed class ProviderRoutingFieldDefinitionFactsPort(
    ProjectProviderResolver resolver,
    ILegacyFieldDefinitionFactsPort sqlite,
    ILegacyFieldDefinitionFactsPort sqlServer,
    ILegacyFieldDefinitionFactsPort duckDb) : ILegacyFieldDefinitionFactsPort
{
    public async Task<IReadOnlyList<LegacyFieldDefinition>> ReadAsync(
        string projectId,
        DatasetKind kind,
        LegacyFieldDefinitionScope scope,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .ReadAsync(projectId, kind, scope, cancellationToken);
    }
}
