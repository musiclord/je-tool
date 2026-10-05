using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

// Pure handler test: the registry and stores are synthetic; no SQL Server connection or service is used.
public sealed class Batch9ServerOnlyProjectPeriodTests
{
    [Theory]
    [InlineData("2026-01-01", "2025-12-31")]
    [InlineData("2025/01/01", "2025-12-31")]
    [InlineData("2025-01-01", "not-a-date")]
    public async Task InvalidRemotePeriod_IsRejectedBeforeMaterializationLockAndSessionChange(string start, string end)
    {
        var document = ProjectDocument.CreateNew("server-invalid", "", "", "synthetic-user", start, end,
            null, ProjectDocument.SqlServerDatabaseProvider, DateTimeOffset.UnixEpoch, 7, 2);
        var store = new EmptyLocalStore();
        var registry = new SyntheticRegistry(document);
        var session = new ProjectSession();
        var previous = TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider);
        session.Enter("keep-active", previous);
        var handler = new ProjectLoadHandler(store, TestProjectRepositories.CatalogWithSameObjects(previous),
            registry, new UnusedConfig(), new CurrentPrincipal("synthetic-user"), session);
        using var payload = JsonDocument.Parse("""{"projectId":"server-invalid","databaseProvider":"sqlServer"}""");
        var error = await Assert.ThrowsAsync<JetActionException>(() => handler.HandleAsync(payload.RootElement, CancellationToken.None));
        Assert.Equal(JetErrorCodes.InvalidProjectSchema, error.Code);
        Assert.Contains("查核期間", error.Message);
        Assert.Contains("yyyy-MM-dd", error.Message);
        Assert.Contains("備份", error.Message);
        Assert.Contains("另建案件", error.Message);
        Assert.Equal(1, registry.FindCalls);
        Assert.Equal(0, store.CreateCalls);
        Assert.Equal("keep-active", session.CurrentProjectId);
        Assert.Same(previous, session.Current!.Repositories);
    }

    private sealed class EmptyLocalStore : IProjectStore
    {
        internal int CreateCalls;
        public Task CreateAsync(ProjectDocument document, CancellationToken cancellationToken)
        {
            CreateCalls++;
            throw new InvalidOperationException("Invalid registry document reached materialization.");
        }
        public Task<ProjectDocument?> FindAsync(string projectId, CancellationToken cancellationToken) => Task.FromResult<ProjectDocument?>(null);
        public Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectStoreEntry>> ListEntriesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveAsync(ProjectDocument document, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class SyntheticRegistry(ProjectDocument document) : IProjectRegistry
    {
        internal int FindCalls;
        public Task<RegisteredProject?> FindVisibleAsync(string projectId, string principal, CancellationToken cancellationToken)
        {
            FindCalls++;
            return Task.FromResult<RegisteredProject?>(new(document, DateTimeOffset.UnixEpoch, null));
        }
        public Task RegisterAsync(ProjectDocument document, string principal, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateDocumentAsync(ProjectDocument document, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<RegisteredProject>> ListVisibleAsync(string principal, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task TouchLastOpenedAsync(string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UnregisterAsync(string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedConfig : IAppConfigStore
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetAsync(string key, string valueJson, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
