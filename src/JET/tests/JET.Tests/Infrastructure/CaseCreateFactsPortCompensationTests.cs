using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class CaseCreateFactsPortCompensationTests
{
    [Fact]
    public async Task ExecuteAsync_BeginsBackendOwnershipBeforeWorkLockAndPreparesAfterWorkLock()
    {
        var calls = new List<string>();
        var store = new RecordingProjectStore(calls);
        var backend = new RecordingCreateBackend(calls);
        var locks = new RecordingLockService(calls, new LockOutcome.Acquired());
        var port = Port(store, backend, locks);

        var facts = await port.ExecuteAsync(Plan("synthetic-create-order"), CancellationToken.None);

        Assert.Equal(
            [
                "project.create",
                "backend.prepare",
                "backend.begin",
                "lock.acquire",
                "backend.materialize"
            ],
            calls);
        Assert.True(facts.LockAcquiredThisCall);
        Assert.True(backend.Exists);
        Assert.True(locks.Held);
        Assert.True(store.Exists);
    }

    [Fact]
    public async Task ExecuteAsync_UserNamedProjectStoreCollision_AttributesCaseNameWithoutParsingMessage()
    {
        var calls = new List<string>();
        var store = new RecordingProjectStore(
            calls,
            new ProjectStoreCollisionException("synthetic-create-collision"));
        var backend = new RecordingCreateBackend(calls);
        var locks = new RecordingLockService(calls, new LockOutcome.Acquired());
        var port = Port(store, backend, locks);

        var thrown = await Assert.ThrowsAsync<JetActionException>(
            () => port.ExecuteAsync(
                Plan("synthetic-create-collision", hasUserSuppliedCaseName: true),
                CancellationToken.None));

        Assert.Equal(JetErrorCodes.InvalidPayload, thrown.Code);
        Assert.Equal(JetErrorFields.CaseName, thrown.Field);
        Assert.Contains("synthetic-create-collision", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(["project.create"], calls);
    }

    [Fact]
    public async Task ExecuteAsync_UnrelatedStoreInvalidPayload_DoesNotAttributeCaseName()
    {
        var calls = new List<string>();
        var failure = new JetActionException(JetErrorCodes.InvalidPayload, "synthetic unrelated failure");
        var store = new RecordingProjectStore(calls, failure);
        var backend = new RecordingCreateBackend(calls);
        var locks = new RecordingLockService(calls, new LockOutcome.Acquired());
        var port = Port(store, backend, locks);

        var thrown = await Assert.ThrowsAsync<JetActionException>(
            () => port.ExecuteAsync(
                Plan("synthetic-unrelated-failure", hasUserSuppliedCaseName: true),
                CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.Null(thrown.Field);
        Assert.Equal(["project.create"], calls);
    }

    [Fact]
    public async Task ExecuteAsync_WhenBackendPrepareFails_ReleasesWorkLockBeforeBackendOwnershipThenDeletesProjectFolderAndRethrowsOriginalFailure()
    {
        var calls = new List<string>();
        var failure = new SyntheticCreateFailureException();
        var store = new RecordingProjectStore(calls);
        var backend = new RecordingCreateBackend(calls, prepareFailure: failure);
        var locks = new RecordingLockService(calls, new LockOutcome.Acquired());
        var port = Port(store, backend, locks);

        var thrown = await Assert.ThrowsAsync<SyntheticCreateFailureException>(
            () => port.ExecuteAsync(Plan("synthetic-create-compensation"), CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.Equal(
            [
                "project.create",
                "backend.prepare",
                "backend.begin",
                "lock.acquire",
                "backend.materialize",
                "lock.release",
                "backend.rollback",
                "project.delete"
            ],
            calls);
        Assert.False(backend.Exists);
        Assert.False(locks.Held);
        Assert.False(store.Exists);
        Assert.False(backend.RollbackTokenCanBeCanceled);
        Assert.False(locks.ReleaseTokenCanBeCanceled);
        Assert.False(store.DeleteTokenCanBeCanceled);
    }

    [Fact]
    public async Task ExecuteAsync_WhenBackendRollbackFails_ReleasesWorkLockAndPreservesProjectFolderAndRethrowsOriginalFailure()
    {
        var calls = new List<string>();
        var failure = new SyntheticCreateFailureException();
        var store = new RecordingProjectStore(calls);
        var backend = new RecordingCreateBackend(
            calls,
            prepareFailure: failure,
            rollbackFailure: new SyntheticRollbackFailureException());
        var locks = new RecordingLockService(calls, new LockOutcome.Acquired());
        var port = Port(store, backend, locks);

        var thrown = await Assert.ThrowsAsync<SyntheticCreateFailureException>(
            () => port.ExecuteAsync(Plan("synthetic-create-rollback-failure"), CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.Equal(
            [
                "project.create",
                "backend.prepare",
                "backend.begin",
                "lock.acquire",
                "backend.materialize",
                "lock.release",
                "backend.rollback.fail"
            ],
            calls);
        Assert.True(backend.Exists);
        Assert.False(locks.Held);
        Assert.True(store.Exists);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCreateLockIsHeld_RollsBackBackendOwnershipBeforeDeletingLocalFolder()
    {
        var calls = new List<string>();
        var store = new RecordingProjectStore(calls);
        var backend = new RecordingCreateBackend(calls);
        var heldUtc = new DateTimeOffset(2026, 4, 5, 6, 7, 8, TimeSpan.Zero);
        var locks = new RecordingLockService(
            calls,
            new LockOutcome.Held("synthetic-owner", "synthetic-machine", heldUtc));
        var port = Port(store, backend, locks);

        var thrown = await Assert.ThrowsAsync<CaseCreateLockHeldException>(
            () => port.ExecuteAsync(Plan("synthetic-create-lock-held"), CancellationToken.None));

        Assert.Equal("synthetic-owner", thrown.LockedBy);
        Assert.Equal(
            [
                "project.create",
                "backend.prepare",
                "backend.begin",
                "lock.acquire",
                "backend.rollback",
                "project.delete"
            ],
            calls);
        Assert.False(backend.PrepareCalled);
        Assert.False(store.Exists);
    }

    [Fact]
    public async Task ExecuteAsync_WhenWorkLockIsReentrantAcquired_RejectsCreateBeforeBackendPrepare()
    {
        var calls = new List<string>();
        var store = new RecordingProjectStore(calls);
        var backend = new RecordingCreateBackend(calls);
        var locks = new RecordingLockService(
            calls,
            new LockOutcome.Acquired(NewlyAcquired: false));
        var port = Port(store, backend, locks);

        await Assert.ThrowsAsync<CaseCreateExistingWorkLockException>(
            () => port.ExecuteAsync(Plan("synthetic-create-reentrant-lock"), CancellationToken.None));

        Assert.Equal(
            [
                "project.create",
                "backend.prepare",
                "backend.begin",
                "lock.acquire",
                "backend.rollback",
                "project.delete"
            ],
            calls);
        Assert.False(backend.PrepareCalled);
        Assert.True(locks.Held);
        Assert.False(store.Exists);
    }

    [Fact]
    public async Task RollbackAsync_AfterFactsPublication_ReleasesLockBeforeBackendOwnershipThenDeletesProjectFolder()
    {
        var calls = new List<string>();
        var store = new RecordingProjectStore(calls);
        var backend = new RecordingCreateBackend(calls);
        var locks = new RecordingLockService(calls, new LockOutcome.Acquired());
        var port = Port(store, backend, locks);
        var facts = await port.ExecuteAsync(
            Plan("synthetic-create-post-facts-failure"),
            CancellationToken.None);

        await port.RollbackAsync(facts, CancellationToken.None);

        Assert.Equal(
            [
                "project.create",
                "backend.prepare",
                "backend.begin",
                "lock.acquire",
                "backend.materialize",
                "lock.release",
                "backend.rollback",
                "project.delete"
            ],
            calls);
        Assert.False(backend.Exists);
        Assert.False(locks.Held);
        Assert.False(store.Exists);
    }

    [Fact]
    public async Task RollbackAsync_WhenWorkLockReleaseFails_RollsBackBackendButPreservesProjectFolder()
    {
        var calls = new List<string>();
        var store = new RecordingProjectStore(calls);
        var backend = new RecordingCreateBackend(calls);
        var locks = new RecordingLockService(
            calls,
            new LockOutcome.Acquired(),
            new SyntheticLockReleaseFailureException());
        var port = Port(store, backend, locks);
        var facts = await port.ExecuteAsync(
            Plan("synthetic-create-release-failure"),
            CancellationToken.None);

        await port.RollbackAsync(facts, CancellationToken.None);

        Assert.Equal(
            [
                "project.create",
                "backend.prepare",
                "backend.begin",
                "lock.acquire",
                "backend.materialize",
                "lock.release.fail",
                "backend.rollback"
            ],
            calls);
        Assert.False(backend.Exists);
        Assert.True(locks.Held);
        Assert.True(store.Exists);
    }

    private static CaseCreateFactsPort Port(
        RecordingProjectStore store,
        RecordingCreateBackend backend,
        RecordingLockService locks) =>
        new(store, new NoOpDatabaseInitializer(), new EmptyProjectRegistry(), backend, locks);

    private static CaseCreatePlan Plan(string projectId, bool hasUserSuppliedCaseName = false)
    {
        var document = ProjectDocument.CreateNew(
            projectId,
            "SYNTHETIC",
            "Synthetic entity",
            "synthetic-operator",
            "2026-01-01",
            "2026-12-31",
            null,
            ProjectDocument.SqlServerDatabaseProvider,
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            sampleSeed: 123,
            // 目前版本建案一律寫入現行 INF 抽樣版本；版本 1 已改判為舊版 JET 案件，測試資料跟著用現行版本。
            sampleSeedVersion: JetAuditProgram.CurrentInfSamplingAlgorithmVersion);
        return JetAuditProgram.Plan(new CaseCreateRequest(
            document,
            HasUserSuppliedCaseName: hasUserSuppliedCaseName,
            Principal: "synthetic-principal"));
    }

    private sealed class RecordingProjectStore(
        List<string> calls,
        Exception? createFailure = null) : IProjectStore
    {
        internal bool Exists { get; private set; }
        internal bool DeleteTokenCanBeCanceled { get; private set; }

        public Task CreateAsync(ProjectDocument document, CancellationToken cancellationToken)
        {
            calls.Add("project.create");
            if (createFailure is not null)
            {
                return Task.FromException(createFailure);
            }
            Exists = true;
            return Task.CompletedTask;
        }

        // 第 9 批中低 12：測試替身沿用原本的正常清單，不在產品介面提供相容實作。
        public Task<IReadOnlyList<ProjectStoreEntry>> ListEntriesAsync(CancellationToken cancellationToken) =>
            ProjectStoreTestEntries.FromAsync(ListAsync(cancellationToken));

        public Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProjectDocument>>([]);

        public Task<ProjectDocument?> FindAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult<ProjectDocument?>(null);

        public Task SaveAsync(ProjectDocument document, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteAsync(string projectId, CancellationToken cancellationToken)
        {
            calls.Add("project.delete");
            DeleteTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            Exists = false;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCreateBackend(
        List<string> calls,
        Exception? prepareFailure = null,
        Exception? rollbackFailure = null) : ICaseCreateBackendPort
    {
        private readonly RecordingAttempt attempt =
            new(calls, prepareFailure, rollbackFailure);

        internal bool Exists => attempt.Exists;
        internal bool PrepareCalled => attempt.PrepareCalled;
        internal bool RollbackTokenCanBeCanceled => attempt.RollbackTokenCanBeCanceled;

        public Task PrepareAsync(ProjectDocument document, CancellationToken cancellationToken)
        {
            calls.Add("backend.prepare");
            return Task.CompletedTask;
        }

        public Task<ICaseCreateBackendAttempt> BeginAsync(
            ProjectDocument document,
            string principal,
            CancellationToken cancellationToken)
        {
            calls.Add("backend.begin");
            return Task.FromResult<ICaseCreateBackendAttempt>(attempt);
        }

        private sealed class RecordingAttempt(
            List<string> calls,
            Exception? prepareFailure,
            Exception? rollbackFailure) : ICaseCreateBackendAttempt
        {
            internal bool Exists { get; private set; }
            internal bool PrepareCalled { get; private set; }
            internal bool RollbackTokenCanBeCanceled { get; private set; }

            public Task PrepareAsync(CancellationToken cancellationToken)
            {
                calls.Add("backend.materialize");
                PrepareCalled = true;
                Exists = true;
                return prepareFailure is null
                    ? Task.CompletedTask
                    : Task.FromException(prepareFailure);
            }

            public Task CommitAsync(CancellationToken cancellationToken)
            {
                calls.Add("backend.commit");
                return Task.CompletedTask;
            }

            public Task RollbackAsync(CancellationToken cancellationToken)
            {
                RollbackTokenCanBeCanceled = cancellationToken.CanBeCanceled;
                if (rollbackFailure is not null)
                {
                    calls.Add("backend.rollback.fail");
                    return Task.FromException(rollbackFailure);
                }

                calls.Add("backend.rollback");
                Exists = false;
                return Task.CompletedTask;
            }
        }
    }

    private sealed class RecordingLockService(
        List<string> calls,
        LockOutcome outcome,
        Exception? releaseFailure = null) : ILockService
    {
        internal bool Held { get; private set; }
        internal bool ReleaseTokenCanBeCanceled { get; private set; }

        public Task<LockOutcome> AcquireAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken)
        {
            calls.Add("lock.acquire");
            Held = outcome is LockOutcome.Acquired;
            return Task.FromResult(outcome);
        }

        public Task RenewAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReleaseAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken)
        {
            ReleaseTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            if (releaseFailure is not null)
            {
                calls.Add("lock.release.fail");
                return Task.FromException(releaseFailure);
            }

            calls.Add("lock.release");
            Held = false;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProjectLockInfo>> ListActiveAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProjectLockInfo>>([]);
    }

    private sealed class NoOpDatabaseInitializer : IProjectDatabaseInitializer
    {
        public Task EnsureCreatedAsync(string projectId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<bool> DatabaseExistsAsync(
            string projectId,
            string databaseProvider,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class EmptyProjectRegistry : IProjectRegistry
    {
        public Task RegisterAsync(
            ProjectDocument document,
            string principal,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpdateDocumentAsync(ProjectDocument document, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<bool> ExistsAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<RegisteredProject>> ListVisibleAsync(
            string principal,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RegisteredProject>>([]);

        public Task<RegisteredProject?> FindVisibleAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken) =>
            Task.FromResult<RegisteredProject?>(null);

        public Task TouchLastOpenedAsync(string projectId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task UnregisterAsync(string projectId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class SyntheticCreateFailureException : Exception;
    private sealed class SyntheticRollbackFailureException : Exception;
    private sealed class SyntheticLockReleaseFailureException : Exception;
}
