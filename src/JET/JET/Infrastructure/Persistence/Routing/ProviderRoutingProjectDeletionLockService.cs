using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// project.delete 的 provider 路由。SQLite／DuckDB 走本地檔案鎖；SQL Server 維持既有刪案交易內清租約，
/// 因此其 deletion arm 是 no-op，不改動線上租約模型。
/// </summary>
public sealed class ProviderRoutingProjectDeletionLockService(
    ProjectProviderResolver resolver,
    IProjectDeletionLockService sqlite,
    IProjectDeletionLockService sqlServer,
    IProjectDeletionLockService duckDb) : IProjectDeletionLockService
{
    public async Task<ProjectDeletionLockOutcome> TryAcquireAsync(
        string projectId,
        string principal,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .TryAcquireAsync(projectId, principal, cancellationToken);
    }
}

/// <summary>SQL Server deletion arm：刪案原子交易本身會清租約，本地 hardening 不改寫該模型。</summary>
public sealed class NoOpProjectDeletionLockService : IProjectDeletionLockService
{
    public Task<ProjectDeletionLockOutcome> TryAcquireAsync(
        string projectId,
        string principal,
        CancellationToken cancellationToken) =>
        Task.FromResult<ProjectDeletionLockOutcome>(
            new ProjectDeletionLockOutcome.Acquired(new NoOpLease()));

    private sealed class NoOpLease : IProjectDeletionLockLease
    {
        public void Complete()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
