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
            session,
            resultStaleStateStore: new FixedStaleStore(true));

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => service.MaterializeForConcurrentQueryAsync("project-1", CancellationToken.None));

        Assert.Equal(JetErrorCodes.OperationInProgress, exception.Code);
        Assert.Equal(0, projectStore.FindCalls);
        Assert.Equal(0, materializer.Calls);

        held.Dispose();
        var snapshot = await service.MaterializeForConcurrentQueryAsync(
            "project-1",
            CancellationToken.None);
        Assert.NotNull(snapshot);
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
            session,
            resultStaleStateStore: new FixedStaleStore(true));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.MaterializeForConcurrentQueryAsync("project-1", cancellation.Token));

        var retried = await service.MaterializeForConcurrentQueryAsync(
            "project-1",
            CancellationToken.None);
        Assert.NotNull(retried);
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
        var locks = new RecordingLockService();
        var session = ActiveSession(locks);
        var projectStore = new StubProjectStore(Document());
        var materializer = new RecordingMaterializer();
        var service = new FilterRunMaterializeService(
            materializer,
            projectStore,
            new StubScenarioStore([Scenario()]),
            new NullMappingStore(),
            gate,
            session,
            resultStaleStateStore: new FixedStaleStore(true));
        var release = new ProjectReleaseLockHandler(
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
            ActiveSession(),
            resultStaleStateStore: new FixedStaleStore(true));

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => service.MaterializeForConcurrentQueryAsync("project-1", CancellationToken.None));

        Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
        Assert.Equal(1, projectStore.FindCalls);
        Assert.Equal(0, materializer.Calls);
    }

    [Fact]
    public async Task ConcurrentMaterialization_CurrentResult_DoesNotAcquireGateOrReadProject()
    {
        var gate = new ActionExecutionGate();
        using var held = Assert.IsAssignableFrom<IDisposable>(gate.TryAcquire());
        var projectStore = new StubProjectStore(Document());
        var materializer = new RecordingMaterializer();
        var service = new FilterRunMaterializeService(materializer, projectStore,
            new StubScenarioStore([Scenario()]), new NullMappingStore(), gate, ActiveSession(),
            resultStaleStateStore: new FixedStaleStore(false));

        Assert.Null(await service.MaterializeForConcurrentQueryAsync("project-1", CancellationToken.None));
        Assert.Equal(0, projectStore.FindCalls);
        Assert.Equal(0, materializer.Calls);
    }

    [Fact]
    public async Task ConcurrentMaterialization_AnotherRequestRefreshedBeforeGate_RechecksBeforeWriting()
    {
        var projectStore = new StubProjectStore(Document());
        var materializer = new RecordingMaterializer();
        var staleStore = new SequenceStaleStore();
        var service = new FilterRunMaterializeService(materializer, projectStore,
            new StubScenarioStore([Scenario()]), new NullMappingStore(), new ActionExecutionGate(), ActiveSession(),
            resultStaleStateStore: staleStore);

        Assert.NotNull(await service.MaterializeForConcurrentQueryAsync("project-1", CancellationToken.None));
        Assert.Equal(2, staleStore.ReadCalls);
        Assert.Equal(1, projectStore.FindCalls);
        Assert.Equal(0, materializer.Calls);
    }

    [Theory]
    [InlineData("data")]
    [InlineData("definition")]
    [InlineData("project")]
    public async Task ScenarioSummary_WhenGenerationChangesDuringCountRead_DoesNotReturnMixedResults(string change)
    {
        var session = new ProjectSession();
        var projectStore = new StubProjectStore(Document());
        var scenarioStore = new StubScenarioStore([Scenario()]);
        var staleStore = new MutableStaleStore();
        var materializer = new RecordingMaterializer();
        var gate = new ActionExecutionGate();
        using var held = Assert.IsAssignableFrom<IDisposable>(gate.TryAcquire());
        var service = new FilterRunMaterializeService(materializer, projectStore,
            scenarioStore, new NullMappingStore(), gate, session, resultStaleStateStore: staleStore);
        var repository = new CountReadRepository(() =>
        {
            if (change == "data") { staleStore.DataRevision = "2"; }
            else if (change == "definition")
            {
                var previous = Scenario();
                scenarioStore.Scenarios = [previous with { SavedUtc = previous.SavedUtc.AddSeconds(1) }];
            }
            else { session.Leave("project-1"); }
        });
        // handler 從作用中案件的資料庫組取 repository 與共用補算服務，替身放進資料庫組後再進入 session。
        session.Enter(
            "project-1",
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with
            {
                TagMatrixScenarios = repository,
                FilterScenarios = scenarioStore,
                FilterRunMaterializeService = service,
            });
        var handler = new QueryTagMatrixScenariosHandler(projectStore, session);

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            handler.HandleAsync(EmptyPayload(), CancellationToken.None));

        Assert.Equal(change == "project" ? JetErrorCodes.NoActiveProject : JetErrorCodes.StaleResult, exception.Code);
        Assert.Equal(1, repository.Calls);
        Assert.Equal(0, materializer.Calls);
    }

    private sealed class CountReadRepository(Action onRead) : ITagMatrixScenariosRepository
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyDictionary<int, (long VoucherHitCount, long RowHitCount)>> GetCountsAsync(
            string projectId, CancellationToken cancellationToken)
        {
            Calls++;
            onRead();
            return Task.FromResult<IReadOnlyDictionary<int, (long VoucherHitCount, long RowHitCount)>>(
                new Dictionary<int, (long, long)> { [1] = (3, 5) });
        }
    }

    private sealed class MutableStaleStore : IResultStaleStateStore
    {
        public Task InvalidateForPreparationDateChangeAsync(string projectId, Func<CancellationToken, Task> saveSettings, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public string DataRevision { get; set; } = "1";
        public Task<string> ReadFilterDataRevisionAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult(DataRevision);
        public Task<AuditResultStaleState> ReadAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult(new AuditResultStaleState(false, false, false));
    }

    private sealed class FixedStaleStore(bool filter) : IResultStaleStateStore
    {
        public Task InvalidateForPreparationDateChangeAsync(string projectId, Func<CancellationToken, Task> saveSettings, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<string> ReadFilterDataRevisionAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult("0");
        public Task<AuditResultStaleState> ReadAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult(new AuditResultStaleState(false, false, filter));
    }

    private sealed class SequenceStaleStore : IResultStaleStateStore
    {
        public Task InvalidateForPreparationDateChangeAsync(string projectId, Func<CancellationToken, Task> saveSettings, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<string> ReadFilterDataRevisionAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult("0");
        public int ReadCalls { get; private set; }
        public Task<AuditResultStaleState> ReadAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult(new AuditResultStaleState(false, false, ++ReadCalls == 1));
    }

    // 2026-10-02 資料庫分流簡化：只設案件編號的舊 Enter(string) 已移除，改帶資料庫組進入；
    // releaseLock 從資料庫組取鎖服務，所以需要時把替身鎖放進去。
    private static ProjectSession ActiveSession(ILockService? locks = null)
    {
        var session = new ProjectSession();
        session.Enter(
            "project-1",
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with
            {
                LockService = locks!,
            });
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
            CancellationToken cancellationToken,
            bool replaceAll = true)
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
        public IReadOnlyList<SavedFilterScenario> Scenarios { get; set; } = scenarios;
        public Task ReplaceAllAsync(
            string projectId,
            IReadOnlyList<SavedFilterScenario> replacement,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SavedFilterScenario>> ListAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Scenarios);
    }

    private sealed class StubProjectStore(ProjectDocument document) : IProjectStore
    {
        public int FindCalls { get; private set; }

        public Task CreateAsync(ProjectDocument created, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        // 第 9 批中低 12：測試替身沿用原本的正常清單，不在產品介面提供相容實作。
        public Task<IReadOnlyList<ProjectStoreEntry>> ListEntriesAsync(CancellationToken cancellationToken) =>
            ProjectStoreTestEntries.FromAsync(ListAsync(cancellationToken));

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
