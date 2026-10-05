using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class ProjectCreateHandlerCompensationTests
{
    [Fact]
    public async Task HandleAsync_WhenSessionPublicationFails_RollsBackPublishedCreateFactsAndRethrowsOriginalFailure()
    {
        var factsPort = new RecordingCreateFactsPort();
        var failure = new SyntheticSessionPublicationFailureException();
        var session = new MutatingThenThrowingSessionPublisher(failure);
        var handler = new ProjectCreateHandler(
            new EmptyProjectStore(),
            CatalogWith(factsPort),
            new CurrentPrincipal("synthetic-principal"),
            session);
        using var payload = JsonDocument.Parse(
            """
            {
              "projectCode": "SYNTHETIC",
              "entityName": "Synthetic entity",
              "operatorId": "synthetic-operator",
              "periodStart": "2026-01-01",
              "periodEnd": "2026-12-31",
              "databaseProvider": "sqlite"
            }
            """);

        var thrown = await Assert.ThrowsAsync<SyntheticSessionPublicationFailureException>(
            () => handler.HandleAsync(payload.RootElement, CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.NotNull(factsPort.ExecutedFacts);
        Assert.Same(factsPort.ExecutedFacts, factsPort.RolledBackFacts);
        Assert.False(factsPort.RollbackTokenCanBeCanceled);
        Assert.True(session.LeaveCalled);
        Assert.Null(session.CurrentProjectId);
    }

    [Fact]
    public async Task HandleAsync_WhenFinalBackendCommitFails_ClearsStagedSessionAndRollsBackCreateFacts()
    {
        var failure = new SyntheticFinalCommitFailureException();
        var factsPort = new RecordingCreateFactsPort(failure);
        var session = new ProjectSession();
        var handler = new ProjectCreateHandler(
            new EmptyProjectStore(),
            CatalogWith(factsPort),
            new CurrentPrincipal("synthetic-principal"),
            session);
        using var payload = JsonDocument.Parse(
            """
            {
              "projectCode": "SYNTHETIC",
              "entityName": "Synthetic entity",
              "operatorId": "synthetic-operator",
              "periodStart": "2026-01-01",
              "periodEnd": "2026-12-31",
              "databaseProvider": "sqlite"
            }
            """);

        var thrown = await Assert.ThrowsAsync<SyntheticFinalCommitFailureException>(
            () => handler.HandleAsync(payload.RootElement, CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.Null(session.CurrentProjectId);
        Assert.Same(factsPort.ExecutedFacts, factsPort.RolledBackFacts);
        Assert.False(factsPort.CompleteTokenCanBeCanceled);
        Assert.False(factsPort.RollbackTokenCanBeCanceled);
    }

    private static ProjectRepositoryCatalog CatalogWith(ICaseCreateFactsPort factsPort) =>
        TestProjectRepositories.CatalogWithSameObjects(
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with
            {
                CaseCreateFacts = factsPort
            });

    private sealed class RecordingCreateFactsPort(Exception? completeFailure = null)
        : ICaseCreateFactsPort
    {
        internal CaseCreateFacts? ExecutedFacts { get; private set; }
        internal CaseCreateFacts? RolledBackFacts { get; private set; }
        internal bool CompleteTokenCanBeCanceled { get; private set; }
        internal bool RollbackTokenCanBeCanceled { get; private set; }

        public Task PreflightAsync(
            CaseCreatePreflightRequest request,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CaseCreateFacts> ExecuteAsync(
            CaseCreatePlan plan,
            CancellationToken cancellationToken)
        {
            ExecutedFacts = new CaseCreateFacts(
                plan.Document,
                plan.Principal,
                LockAcquiredThisCall: true,
                new RecordingBackendAttempt());
            return Task.FromResult(ExecutedFacts);
        }

        public Task CompleteAsync(
            CaseCreateFacts facts,
            CancellationToken cancellationToken)
        {
            CompleteTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            return completeFailure is null
                ? Task.CompletedTask
                : Task.FromException(completeFailure);
        }

        public Task RollbackAsync(
            CaseCreateFacts facts,
            CancellationToken cancellationToken)
        {
            RolledBackFacts = facts;
            RollbackTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            return Task.CompletedTask;
        }
    }

    private sealed class MutatingThenThrowingSessionPublisher(Exception failure)
        : IProjectSessionPublisher
    {
        internal string? CurrentProjectId { get; private set; }
        internal bool LeaveCalled { get; private set; }

        public void Enter(string projectId, ProjectRepositories repositories)
        {
            CurrentProjectId = projectId;
            throw failure;
        }

        public bool Leave(string expectedProjectId)
        {
            LeaveCalled = true;
            if (!string.Equals(CurrentProjectId, expectedProjectId, StringComparison.Ordinal))
            {
                return false;
            }

            CurrentProjectId = null;
            return true;
        }
    }

    private sealed class RecordingBackendAttempt : ICaseCreateBackendAttempt
    {
        public Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EmptyProjectStore : IProjectStore
    {
        public Task CreateAsync(ProjectDocument document, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        // 第 9 批中低 12：測試替身沿用原本的正常清單，不在產品介面提供相容實作。
        public Task<IReadOnlyList<ProjectStoreEntry>> ListEntriesAsync(CancellationToken cancellationToken) =>
            ProjectStoreTestEntries.FromAsync(ListAsync(cancellationToken));

        public Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProjectDocument>>([]);

        public Task<ProjectDocument?> FindAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult<ProjectDocument?>(null);

        public Task SaveAsync(ProjectDocument document, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteAsync(string projectId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class SyntheticSessionPublicationFailureException : Exception;
    private sealed class SyntheticFinalCommitFailureException : Exception;
}
