using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依案件 provider 路由專案 audit_event_log 寫入。</summary>
public sealed class ProviderRoutingProjectAuditLog(
    ProjectProviderResolver resolver,
    IProjectAuditLog sqlite,
    IProjectAuditLog sqlServer,
    IProjectAuditLog duckDb) : IProjectAuditLog
{
    public async Task AppendAsync(
        string projectId,
        ProjectAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .AppendAsync(projectId, auditEvent, cancellationToken);
    }

    public async Task<bool> RequiresMappingRecommitAuditAsync(
        string projectId,
        string dataset,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .RequiresMappingRecommitAuditAsync(projectId, dataset, cancellationToken);
    }
}
