using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>project.delete 的報告租約編排：授權後取得，並持有到 DB 與資料夾處理結束。</summary>
public sealed class ProjectDeleteReportLeaseTests
{
    [Fact]
    public async Task DeletePreview_CountsExistingIndexedFilesSeparatelyWithoutLoadingOrDeleting()
    {
        var events = new List<string>();
        var reports = new LeaseReportStore(events)
        {
            PreviewReports = [
                PreviewReport("v", ReportKind.ValidationReport, "validation.xlsx"),
                PreviewReport("p", ReportKind.PrescreenReport, "prescreen.xlsx", ReportFileState.ModifiedOutside),
                PreviewReport("old", ReportKind.ValidationReport, "validation.xlsx"),
                PreviewReport("missing", ReportKind.InfReport, "missing.xlsx", ReportFileState.Missing),
                PreviewReport("wp1", ReportKind.WorkingPaper, "working1.xlsx"),
                PreviewReport("wp2", ReportKind.WorkingPaper, "working2.xlsx", ReportFileState.ModifiedOutside),
                PreviewReport("wpm", ReportKind.WorkingPaper, "working3.xlsx", ReportFileState.Missing),
                PreviewReport("template", ReportKind.AccountMapping, "template.xlsx")]
        };
        var handler = PreviewHandler(new FakeProjectStore(Document("project-1"), events), reports, new FakeRegistry());
        using var payload = JsonDocument.Parse("{\"projectId\":\"project-1\"}");
        var response = JsonSerializer.SerializeToElement(await handler.HandleAsync(payload.RootElement, default));
        Assert.Equal("project-1", response.GetProperty("projectId").GetString());
        Assert.Equal("sqlite", response.GetProperty("databaseProvider").GetString());
        Assert.Equal(2, response.GetProperty("reportCount").GetInt32());
        Assert.Equal(2, response.GetProperty("workingPaperCount").GetInt32());
        Assert.Equal(["reports.list"], events);
        Assert.False(reports.LeaseHeld);
    }

    [Fact]
    public async Task DeletePreview_UnauthorizedServerProjectDoesNotReadCatalog()
    {
        var events = new List<string>();
        var reports = new LeaseReportStore(events) { PreviewReports = [] };
        var handler = PreviewHandler(new FakeProjectStore(Document("project-1") with
            { DatabaseProvider = ProjectDocument.SqlServerDatabaseProvider }, events), reports, new FakeRegistry(exists: true));
        using var payload = JsonDocument.Parse("{\"projectId\":\"project-1\"}");
        var error = await Assert.ThrowsAsync<JetActionException>(() => handler.HandleAsync(payload.RootElement, default));
        Assert.Equal(JetErrorCodes.NotAuthorized, error.Code);
        Assert.Empty(events);
    }

    [Fact]
    public async Task DeletePreview_CatalogFailureDoesNotInventZeroCounts()
    {
        var events = new List<string>();
        var failure = new IOException("Synthetic catalog unavailable");
        var reports = new LeaseReportStore(events) { PreviewFailure = failure };
        var handler = PreviewHandler(new FakeProjectStore(Document("project-1"), events), reports, new FakeRegistry());
        using var payload = JsonDocument.Parse("{\"projectId\":\"project-1\"}");
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => handler.HandleAsync(payload.RootElement, default)));
        Assert.Equal(["reports.list"], events);
    }

    [Fact]
    public async Task DeleteFolderFailureDoesNotMislabelRemainingReportsAsCache()
    {
        var events = new List<string>();
        var reports = new LeaseReportStore(events);
        var locks = new LeaseDeletionLockService(events);
        var session = new ProjectSession();
        session.Enter("project-1", TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider));
        var handler = new ProjectDeleteHandler(new FakeProjectStore(Document("project-1"), events, new IOException("Synthetic folder failure")),
            CatalogWith(new FakeDatabaseDeleter(events, reports, locks), reports, locks), new FakeRegistry(), new CurrentPrincipal("synthetic"), session);
        using var payload = JsonDocument.Parse("{\"projectId\":\"project-1\"}");
        var response = JsonSerializer.SerializeToElement(await handler.HandleAsync(payload.RootElement, default));
        var message = response.GetProperty("message").GetString()!;
        Assert.Contains("案件資料夾", message);
        Assert.DoesNotContain("快取", message);
        // 預期值換成裁定 2026-10-13-28 的句子（第一次失敗：收據 20261013-130645716-968482adb634494a97e2594c7e73decf）。
        Assert.Contains("刪除剩餘的檔案", message);
        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Null(session.CurrentProjectId);
    }

    private static IApplicationActionHandler PreviewHandler(IProjectStore projects, IReportStore reports, IProjectRegistry registry)
    {
        // Reflection keeps the first-failure tests compilable before the new read-only handler exists.
        var type = typeof(ProjectDeleteHandler).Assembly.GetType("JET.Application.ProjectDeletePreviewHandler");
        Assert.NotNull(type);
        var repositories = TestProjectRepositories.CatalogWithSameObjects(
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with { ReportStore = reports });
        return Assert.IsAssignableFrom<IApplicationActionHandler>(Activator.CreateInstance(type,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            binder: null, args: [projects, repositories, registry, new CurrentPrincipal("synthetic")], culture: null));
    }

    private static Report PreviewReport(string id, ReportKind kind, string filename,
        ReportFileState state = ReportFileState.AsPublished) =>
        new(id, kind, filename, new ReportSourceRefs(), DateTimeOffset.UnixEpoch, 10, DateTimeOffset.UnixEpoch, false, state);

    [Fact]
    public async Task Delete_AuthorizedProject_HoldsReportLeaseAcrossDatabaseAndFolderDeletion()
    {
        var events = new List<string>();
        var projectId = "project-1";
        var projectStore = new FakeProjectStore(Document(projectId), events);
        var reports = new LeaseReportStore(events);
        var deletionLocks = new LeaseDeletionLockService(events);
        var session = new ProjectSession();
        session.Enter(projectId, TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider));
        var handler = new ProjectDeleteHandler(
            projectStore,
            CatalogWith(new FakeDatabaseDeleter(events, reports, deletionLocks), reports, deletionLocks),
            new FakeRegistry(),
            new CurrentPrincipal("CONTOSO\\auditor"),
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
        Assert.False(reports.LeaseHeld);
        Assert.False(deletionLocks.LeaseHeld);
        Assert.Null(session.CurrentProjectId);
    }

    [Fact]
    public async Task Delete_UnauthorizedSqlServerProject_DoesNotAcquireReportLease()
    {
        var events = new List<string>();
        var projectId = "project-1";
        var projectStore = new FakeProjectStore(
            Document(projectId) with { DatabaseProvider = ProjectDocument.SqlServerDatabaseProvider },
            events);
        var reports = new LeaseReportStore(events);
        var deletionLocks = new LeaseDeletionLockService(events);
        var handler = new ProjectDeleteHandler(
            projectStore,
            CatalogWith(new FakeDatabaseDeleter(events, reports, deletionLocks), reports, deletionLocks),
            new FakeRegistry(exists: true, visible: null),
            new CurrentPrincipal("CONTOSO\\intruder"),
            new ProjectSession());
        using var payload = JsonDocument.Parse("{\"projectId\":\"project-1\"}");

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            handler.HandleAsync(payload.RootElement, CancellationToken.None));

        Assert.Equal(JetErrorCodes.NotAuthorized, exception.Code);
        Assert.Empty(events);
        Assert.False(reports.LeaseHeld);
        Assert.False(deletionLocks.LeaseHeld);
    }

    [Fact]
    public async Task Delete_DatabaseFails_DoesNotCompleteLeaseOrLeaveSession()
    {
        var events = new List<string>();
        var projectId = "project-1";
        var projectStore = new FakeProjectStore(Document(projectId), events);
        var reports = new LeaseReportStore(events);
        var deletionLocks = new LeaseDeletionLockService(events);
        var session = new ProjectSession();
        session.Enter(projectId, TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider));
        var failure = new IOException("database delete failed");
        var handler = new ProjectDeleteHandler(
            projectStore,
            CatalogWith(new FakeDatabaseDeleter(events, reports, deletionLocks, failure), reports, deletionLocks),
            new FakeRegistry(),
            new CurrentPrincipal("CONTOSO\\auditor"),
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
        var reports = new LeaseReportStore(events);
        var deletionLocks = new LeaseDeletionLockService(events);
        var session = new ProjectSession();
        session.Enter(projectId, TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider));
        var handler = new ProjectDeleteHandler(
            projectStore,
            CatalogWith(new FakeDatabaseDeleter(events, reports, deletionLocks), reports, deletionLocks),
            new FakeRegistry(),
            new CurrentPrincipal("CONTOSO\\auditor"),
            session);
        using var payload = JsonDocument.Parse("{\"projectId\":\"project-1\"}");

        var response = await handler.HandleAsync(payload.RootElement, CancellationToken.None);
        var responseJson = JsonSerializer.SerializeToElement(response);

        // 刪除會移除整個案件資料夾，報告與底稿不是快取；保留session離開與刪除lease完成的原斷言。
        // 首次失敗：20261004-100911120-57efb95a0cae44beb892ec3c2d058592。
        Assert.Equal(
// 預期值換成裁定 2026-10-13-28 的句子（第一次失敗：收據 20261013-130645716-968482adb634494a97e2594c7e73decf）。
                        "案件已刪除，但案件資料夾未能完全刪除，請自行確認並刪除剩餘的檔案。",
            responseJson.GetProperty("message").GetString());
        Assert.DoesNotContain("reconcile", responseJson.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(session.CurrentProjectId);
        Assert.True(deletionLocks.Completed);
    }

    private static ProjectRepositoryCatalog CatalogWith(
        IProjectDatabaseDeleter databaseDeleter,
        IReportStore reports,
        IProjectDeletionLockService deletionLocks) =>
        TestProjectRepositories.CatalogWithSameObjects(
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with
            {
                DatabaseDeleter = databaseDeleter,
                ReportStore = reports,
                DeletionLockService = deletionLocks
            });

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

        // 測試替身沿用原本的正常清單，不在產品介面提供相容實作。
        public Task<IReadOnlyList<ProjectStoreEntry>> ListEntriesAsync(CancellationToken cancellationToken) =>
            ProjectStoreTestEntries.FromAsync(ListAsync(cancellationToken));

        public Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(ProjectDocument value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDatabaseDeleter(
        List<string> events,
        LeaseReportStore reports,
        LeaseDeletionLockService deletionLocks,
        Exception? failure = null) : IProjectDatabaseDeleter
    {
        public Task DeleteAsync(string projectId, CancellationToken cancellationToken)
        {
            Assert.True(reports.LeaseHeld);
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

    private sealed class LeaseReportStore(List<string> events) : IReportStore
    {
        public bool LeaseHeld { get; private set; }
        public IReadOnlyList<Report>? PreviewReports { get; init; }
        public Exception? PreviewFailure { get; init; }

        public Task<IAsyncDisposable> AcquireProjectDeletionLeaseAsync(
            string projectId,
            CancellationToken cancellationToken)
        {
            Assert.False(LeaseHeld);
            LeaseHeld = true;
            events.Add("lease.acquire");
            return Task.FromResult<IAsyncDisposable>(new Lease(this, events));
        }

        public Task<Report> WriteAsync(
            string projectId,
            ReportWriteRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Report>> WriteBatchAsync(
            string projectId,
            IReadOnlyList<ReportWriteRequest> requests,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Report>> ListAsync(
            string projectId,
            CancellationToken cancellationToken)
        {
            events.Add("reports.list");
            return PreviewFailure is not null ? Task.FromException<IReadOnlyList<Report>>(PreviewFailure)
                : Task.FromResult(PreviewReports ?? throw new NotSupportedException());
        }

        public Task<string> ResolvePathAsync(
            string projectId,
            string reportId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> MarkStaleAsync(
            string projectId,
            ReportKind kind,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> MarkStaleAsync(
            string projectId,
            Func<Report, bool> predicate,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        private sealed class Lease(LeaseReportStore owner, List<string> events) : IAsyncDisposable
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
