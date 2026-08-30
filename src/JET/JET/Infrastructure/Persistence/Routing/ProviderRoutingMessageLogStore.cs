using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由訊息記錄存取(比照 <see cref="ProviderRoutingGlRepository"/>)。</summary>
public sealed class ProviderRoutingMessageLogStore(
    ProjectProviderResolver resolver,
    IMessageLogStore sqlite,
    IMessageLogStore sqlServer,
    IMessageLogStore duckDb) : IMessageLogStore
{
    public async Task AppendAsync(string projectId, string level, string text, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb).AppendAsync(projectId, level, text, cancellationToken);
    }

    public async Task<IReadOnlyList<MessageLogEntry>> GetRecentAsync(
        string projectId, int limit, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb).GetRecentAsync(projectId, limit, cancellationToken);
    }
}
