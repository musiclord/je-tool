using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>project.delete 的 artifact lease 編排：授權後取得，並持有到 DB 與資料夾處理結束。</summary>
public sealed class ProjectDeleteArtifactLeaseTests
{
    [Fact]
    public async Task Delete_AuthorizedProject_HoldsArtifactLeaseAcrossDatabaseAndFolderDeletion()
    {
        var events = new List<string>();
        var projectId = "project-1";
        var projectStore = new FakeProjectStore(Document(projectId), events);
        var artifacts = new LeaseArtifactStore(events);
        var deletionLocks = new LeaseDeletionLockService(events);
        var session = new ProjectSession();
        session.Enter(projectId);
        var handler = new ProjectDeleteHandler(
            projectStore,
            new FakeDatabaseDeleter(events, artifacts, deletionLocks),
            new FakeRegistry(),
            new CurrentPrincipal("CONTOSO\\auditor"),
            artifacts,
            deletionLocks,
            session);
        using var payload = JsonDocument.Parse("{\"projectId\":\"project-1\"}");

        await handler.HandleAsync(payload.RootElement, CancellationToken.None);

        Assert.Equal(
            [
                "deletion-lock.acquire",
                "lease.acquire",
                "database.delete",
                "folder.delete",
                "deletion-lock.complete",
                "lease.dispose",
                "deletion-lock.dispose"
            ],
            events);
        Assert.False(artifacts.LeaseHeld);
        Assert.False(deletionLocks.LeaseHeld);
        Assert.Null(session.CurrentProjectId);
    }

    [Fact]
    public async Task Delete_UnauthorizedSqlServerProject_DoesNotAcquireArtifactLease()
    {
        var events = new List<string>();
        var projectId = "project-1";
        var projectStore = new FakeProjectStore(
            Document(projectId) with { DatabaseProvider = ProjectDocument.SqlServerDatabaseProvider },
            events);
        var artifacts = new LeaseArtifactStore(events);
        var deletionLocks = new LeaseDeletionLockService(events);
        var handler = new ProjectDeleteHandler(
            projectStore,
            new FakeDatabaseDeleter(events, artifacts, deletionLocks),
            new FakeRegistry(exists: true, visible: null),
            new CurrentPrincipal("CONTOSO\\intruder"),
            artifacts,
            deletionLocks,
            new ProjectSession());
        using var payload = JsonDocument.Parse("{\"projectId\":\"project-1\"}");

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            handler.HandleAsync(payload.RootElement, CancellationToken.None));

        Assert.Equal(JetErrorCodes.NotAuthorized, exception.Code);
        Assert.Empty(events);
        Assert.False(artifacts.LeaseHeld);
        Assert.False(deletionLocks.LeaseHeld);
    }

    [Fact]
    public async Task Delete_DatabaseFails_DoesNotCompleteLeaseOrLeaveSession()
    {
        var events = new List<string>();
        var projectId = "project-1";
        var projectStore = new FakeProjectStore(Document(projectId), events);
        var artifacts = new LeaseArtifactStore(events);
        var deletionLocks = new LeaseDeletionLockService(events);
        var session = new ProjectSession();
        session.Enter(projectId);
        var failure = new IOException("database delete failed");
        var handler = new ProjectDeleteHandler(
            projectStore,
            new FakeDatabaseDeleter(events, artifacts, deletionLocks, failure),
            new FakeRegistry(),
            new CurrentPrincipal("CONTOSO\\auditor"),
            artifacts,
            deletionLocks,
            session);
        using var payload = JsonDocument.Parse("{\"projectId\":\"project-1\"}");

        var actual = await Assert.ThrowsAsync<IOException>(() =>
            handler.HandleAsync(payload.RootElement, CancellationToken.None));

        Assert.Same(failure, actual);
        Assert.Equal(
            [
                "deletion-lock.acquire",
                "lease.acquire",
                "database.delete",
                "lease.dispose",
                "deletion-lock.dispose"
            ],
            events);
        Assert.False(deletionLocks.Completed);
        Assert.Equal(projectId, session.CurrentProjectId);
    }

    [Fact]
    public async Task Delete_FolderCleanupFails_ReturnsReleaseUsableManualCleanupGuidance()
    {
        var events = new List<string>();
        var projectId = "project-1";
        var projectStore = new FakeProjectStore(
            Document(projectId),
            events,
            new IOException("folder delete failed"));
        var artifacts = new LeaseArtifactStore(events);
        var deletionLocks = new LeaseDeletionLockService(events);
        var session = new ProjectSession();
        session.Enter(projectId);
        var handler = new ProjectDeleteHandler(
            projectStore,
            new FakeDatabaseDeleter(events, artifacts, deletionLocks),
            new FakeRegistry(),
            new CurrentPrincipal("CONTOSO\\auditor"),
            artifacts,
            deletionLocks,
            session);
        using var payload = JsonDocument.Parse("{\"projectId\":\"project-1\"}");

        var response = await handler.HandleAsync(payload.RootElement, CancellationToken.None);
        var responseJson = JsonSerializer.SerializeToElement(response);

        Assert.Equal(
            "案件已刪除；本機快取資料夾清理失敗，可稍後手動移除。",
            responseJson.GetProperty("message").GetString());
        Assert.DoesNotContain("reconcile", responseJson.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(session.CurrentProjectId);
        Assert.True(deletionLocks.Completed);
    }

    private static ProjectDocument Document(string projectId) => new(
        projectId,
        "CODE-1",
        "Entity",
        "operator",
        "2025-01-01",
        "2025-12-31",
        null,
        ProjectDocument.DefaultMoneyScale,
        ProjectDocument.DefaultRoundingMode,
        new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero),
        1,
        ProjectDocument.CurrentSchemaVersion);

    private sealed class FakeProjectStore(
        ProjectDocument document,
        List<string> events,
        Exception? deleteFailure = null) : IProjectStore
    {
        public Task<ProjectDocument?> FindAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult<ProjectDocument?>(
                string.Equals(projectId, document.ProjectId, StringComparison.Ordinal) ? document : null);

        public Task DeleteAsync(string projectId, CancellationToken cancellationToken)
        {
            events.Add("folder.delete");
            return deleteFailure is null
                ? Task.CompletedTask
                : Task.FromException(deleteFailure);
        }

        public Task CreateAsync(ProjectDocument value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(ProjectDocument value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDatabaseDeleter(
        List<string> events,
        LeaseArtifactStore artifacts,
        LeaseDeletionLockService deletionLocks,
        Exception? failure = null) : IProjectDatabaseDeleter
    {
        public Task DeleteAsync(string projectId, CancellationToken cancellationToken)
        {
            Assert.True(artifacts.LeaseHeld);
            Assert.True(deletionLocks.LeaseHeld);
            events.Add("database.delete");
            if (failure is not null)
            {
                return Task.FromException(failure);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeRegistry(bool exists = false, RegisteredProject? visible = null) : IProjectRegistry
    {
        public Task<bool> ExistsAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult(exists);

        public Task<RegisteredProject?> FindVisibleAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken) => Task.FromResult(visible);

        public Task RegisterAsync(
            ProjectDocument document,
            string principal,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UpdateDocumentAsync(
            ProjectDocument document,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<RegisteredProject>> ListVisibleAsync(
            string principal,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task TouchLastOpenedAsync(string projectId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UnregisterAsync(string projectId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class LeaseArtifactStore(List<string> events) : IReportArtifactStore
    {
        public bool LeaseHeld { get; private set; }

        public Task<IAsyncDisposable> AcquireProjectDeletionLeaseAsync(
            string projectId,
            CancellationToken cancellationToken)
        {
            Assert.False(LeaseHeld);
            LeaseHeld = true;
            events.Add("lease.acquire");
            return Task.FromResult<IAsyncDisposable>(new Lease(this, events));
        }

        public Task<ReportArtifact> WriteAsync(
            string projectId,
            ReportArtifactWriteRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ReportArtifact>> WriteBatchAsync(
            string projectId,
            IReadOnlyList<ReportArtifactWriteRequest> requests,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ReportArtifact>> ListAsync(
            string projectId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ReportArtifactCatalog> ReadCatalogAsync(
            string projectId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> ResolvePathAsync(
            string projectId,
            string artifactId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> MarkStaleAsync(
            string projectId,
            ReportArtifactKind kind,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> MarkStaleAsync(
            string projectId,
            Func<ReportArtifact, bool> predicate,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ReportArtifactCleanupResult> CleanupAsync(
            string projectId,
            string expectedCatalogRevision,
            IReadOnlyList<ReportArtifactCleanupCandidate> candidates,
            string requestedBy,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        private sealed class Lease(LeaseArtifactStore owner, List<string> events) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                Assert.True(owner.LeaseHeld);
                owner.LeaseHeld = false;
                events.Add("lease.dispose");
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class LeaseDeletionLockService(List<string> events) : IProjectDeletionLockService
    {
        public bool LeaseHeld { get; private set; }
        public bool Completed { get; private set; }

        public Task<ProjectDeletionLockOutcome> TryAcquireAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken)
        {
            Assert.False(LeaseHeld);
            LeaseHeld = true;
            events.Add("deletion-lock.acquire");
            return Task.FromResult<ProjectDeletionLockOutcome>(
                new ProjectDeletionLockOutcome.Acquired(new Lease(this, events)));
        }

        private sealed class Lease(LeaseDeletionLockService owner, List<string> events)
            : IProjectDeletionLockLease
        {
            public void Complete()
            {
                Assert.True(owner.LeaseHeld);
                owner.Completed = true;
                events.Add("deletion-lock.complete");
            }

            public ValueTask DisposeAsync()
            {
                Assert.True(owner.LeaseHeld);
                owner.LeaseHeld = false;
                events.Add("deletion-lock.dispose");
                return ValueTask.CompletedTask;
            }
        }
    }
}
