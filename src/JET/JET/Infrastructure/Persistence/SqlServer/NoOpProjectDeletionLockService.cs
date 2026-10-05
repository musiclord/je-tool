using JET.Domain;

namespace JET.Infrastructure;

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
