using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class FilterRunMaterializeServiceGateTests
{
    [Fact]
    public async Task ConcurrentMaterialization_WhenExclusiveGateIsHeld_FailsFastWithoutReadingOrWriting()
    {
        var gate = new ActionExecutionGate();
        using var held = Assert.IsAssignableFrom<IDisposable>(gate.TryAcquire());
        var projectStore = new StubProjectStore(Document());
        var materializer = new RecordingMaterializer();
        var session = ActiveSession();
        var service = new FilterRunMaterializeService(
            materializer,
            projectStore,
            new StubScenarioStore([Scenario()]),
            new NullMappingStore(),
            gate,
            session);

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => service.MaterializeForConcurrentQueryAsync("project-1", CancellationToken.None));

        Assert.Equal(JetErrorCodes.OperationInProgress, exception.Code);
        Assert.Equal(0, projectStore.FindCalls);
        Assert.Equal(0, materializer.Calls);

        held.Dispose();
        var snapshot = await service.MaterializeForConcurrentQueryAsync(
            "project-1",
            CancellationToken.None);
        Assert.Equal("project-1", snapshot.Document.ProjectId);
        Assert.Single(snapshot.Scenarios);
        Assert.Equal(1, materializer.Calls);
    }

    [Fact]
    public async Task ConcurrentMaterialization_WhenCancelled_ReleasesGateForImmediateRetry()
    {
        var gate = new ActionExecutionGate();
        var materializer = new RecordingMaterializer();
        var session = ActiveSession();
        var service = new FilterRunMaterializeService(
            materializer,
            new StubProjectStore(Document()),
            new StubScenarioStore([Scenario()]),
            new NullMappingStore(),
            gate,
            session);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.MaterializeForConcurrentQueryAsync("project-1", cancellation.Token));

        var retried = await service.MaterializeForConcurrentQueryAsync(
            "project-1",
            CancellationToken.None);
        Assert.Equal("project-1", retried.Document.ProjectId);
        Assert.Equal(2, materializer.Calls);
    }

    [Fact]
    public async Task ConcurrentMaterialization_WithoutSharedSession_FailsClosedBeforeReadingOrWriting()
    {
        var projectStore = new StubProjectStore(Document());
        var materializer = new RecordingMaterializer();
        var service = new FilterRunMaterializeService(
            materializer,
            projectStore,
            new StubScenarioStore([Scenario()]),
            new NullMappingStore(),
            new ActionExecutionGate());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.MaterializeForConcurrentQueryAsync("project-1", CancellationToken.None));

        Assert.Equal(0, projectStore.FindCalls);
        Assert.Equal(0, materializer.Calls);
    }

    [Fact]
    public async Task ConcurrentMaterialization_AfterReleaseWins_DoesNotReadOrWriteFormerProject()
    {
        var gate = new ActionExecutionGate();
        var session = ActiveSession();
        var projectStore = new StubProjectStore(Document());
        var materializer = new RecordingMaterializer();
        var service = new FilterRunMaterializeService(
            materializer,
            projectStore,
            new StubScenarioStore([Scenario()]),
            new NullMappingStore(),
            gate,
            session);
        var locks = new RecordingLockService();
        var release = new ProjectReleaseLockHandler(
            locks,
            new CurrentPrincipal("CONTOSO\\auditor"),
            session,
            gate);

        // 模擬 concurrent query 已在進閘前捕捉 project-1；release 先取得同一把閘並完成離場。
        await release.HandleAsync(EmptyPayload(), CancellationToken.None);
        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => service.MaterializeForConcurrentQueryAsync("project-1", CancellationToken.None));

        Assert.Equal(JetErrorCodes.NoActiveProject, exception.Code);
        Assert.Null(session.CurrentProjectId);
        Assert.Equal(1, locks.ReleaseCalls);
        Assert.Equal(0, projectStore.FindCalls);
        Assert.Equal(0, materializer.Calls);
    }

    [Fact]
    public async Task ConcurrentMaterialization_PreEffectivePopulationScenario_FailsStaleWithoutWritingHits()
    {
        var projectStore = new StubProjectStore(Document());
        var materializer = new RecordingMaterializer();
        var service = new FilterRunMaterializeService(
            materializer,
            projectStore,
            new StubScenarioStore([Scenario("filter-2026-08-04-v6")]),
            new NullMappingStore(),
            new ActionExecutionGate(),
            ActiveSession());

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => service.MaterializeForConcurrentQueryAsync("project-1", CancellationToken.None));

        Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
        Assert.Equal(1, projectStore.FindCalls);
        Assert.Equal(0, materializer.Calls);
    }

    private static ProjectSession ActiveSession()
    {
        var session = new ProjectSession();
        session.Enter("project-1");
        return session;
    }

    private static System.Text.Json.JsonElement EmptyPayload()
    {
        using var document = System.Text.Json.JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static ProjectDocument Document() => new(
        ProjectId: "project-1",
        ProjectCode: "P-1",
        EntityName: "共享閘測試",
        OperatorId: "tester",
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        LastAccountingPeriodDate: "2024-12-31",
        MoneyScale: ProjectDocument.DefaultMoneyScale,
        RoundingMode: ProjectDocument.DefaultRoundingMode,
        CreatedUtc: new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero),
        CurrentStep: 4,
        SchemaVersion: ProjectDocument.CurrentSchemaVersion);

    private static SavedFilterScenario Scenario(string logicVersion = RuleLogicVersions.Filter) => new(
        Position: 1,
        Name: "借方",
        Rationale: "共享閘測試",
        DefinitionJson:
            $$"""
            {
              "name": "借方",
              "rationale": "共享閘測試",
              "groups": [
                {
                  "join": "AND",
                  "rules": [
                    { "join": "AND", "type": "drCrOnly", "drCr": "debit" }
                  ]
                }
              ],
              "populationScope": "auditPeriod",
              "logicVersion": "{{logicVersion}}"
            }
            """,
        SavedUtc: new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero));

    private sealed class RecordingMaterializer : IFilterRunMaterializer
    {
        public int Calls { get; private set; }

        public Task MaterializeAsync(
            string projectId,
            IReadOnlyList<MaterializableScenario> scenarios,
            FilterRuleContext context,
            CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLockService : ILockService
    {
        public int ReleaseCalls { get; private set; }

        public Task<LockOutcome> AcquireAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken) =>
            Task.FromResult<LockOutcome>(new LockOutcome.Acquired());

        public Task RenewAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReleaseAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken)
        {
            ReleaseCalls++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProjectLockInfo>> ListActiveAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProjectLockInfo>>([]);
    }

    private sealed class StubScenarioStore(IReadOnlyList<SavedFilterScenario> scenarios)
        : IFilterScenarioStore
    {
        public Task ReplaceAllAsync(
            string projectId,
            IReadOnlyList<SavedFilterScenario> replacement,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SavedFilterScenario>> ListAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            Task.FromResult(scenarios);
    }

    private sealed class StubProjectStore(ProjectDocument document) : IProjectStore
    {
        public int FindCalls { get; private set; }

        public Task CreateAsync(ProjectDocument created, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProjectDocument>>([document]);

        public Task<ProjectDocument?> FindAsync(string projectId, CancellationToken cancellationToken)
        {
            FindCalls++;
            return Task.FromResult<ProjectDocument?>(document);
        }

        public Task SaveAsync(ProjectDocument saved, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string projectId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NullMappingStore : IMappingStateStore
    {
        public Task SaveAsync(
            string projectId,
            CommittedMapping mapping,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CommittedMapping?> FindAsync(
            string projectId,
            DatasetKind kind,
            CancellationToken cancellationToken) =>
            Task.FromResult<CommittedMapping?>(null);
    }
}
