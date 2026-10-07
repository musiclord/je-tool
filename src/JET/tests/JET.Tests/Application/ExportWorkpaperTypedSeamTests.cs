using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// Production Working Paper 的唯一 constructor 必須走完整 typed lifecycle，沒有
/// rollback fallback；公開 writer contract 僅保留相容用途。此測試同時鎖定惰性
/// stale refresh 的既有 predicate 路徑。
/// </summary>
public sealed class ExportWorkpaperTypedSeamTests
{
    [Fact]
    public Task SolePath_RunsTypedLifecycleAndWriterAfterLazyStaleRefresh() =>
        VerifyTypedLifecycleAsync(rejectUnusedPrepublicationCatalog: false);

    [Fact]
    public Task UnusedPrepublicationCatalogRead_CannotPreventWorkingPaperPublication() =>
        VerifyTypedLifecycleAsync(rejectUnusedPrepublicationCatalog: true);

    private static async Task VerifyTypedLifecycleAsync(bool rejectUnusedPrepublicationCatalog)
    {
        const string projectId = "typed-workpaper-export";
        const string validationRunId = "validation-current";
        const string prescreenRunId = "prescreen-current";
        var order = new List<string>();
        var savedUtc = new DateTimeOffset(2026, 7, 19, 3, 4, 5, TimeSpan.Zero);
        var scenario = Scenario(savedUtc);
        var scenarios = new RecordingScenarioStore([scenario]);
        var revision = FilterPopulationScopeParser
            .RequireCurrentRevision([scenario])
            .Revision;
        var runs = new CurrentRunStore(
            Run(validationRunId, RuleRunKinds.Validate, RuleLogicVersions.Validation),
            Run(prescreenRunId, RuleRunKinds.Prescreen, RuleLogicVersions.Prescreen));
        var project = Project(projectId);
        var projectStore = new FixedProjectStore(project);
        var materializer = new RecordingMaterializer(order);
        var materializeService = new FilterRunMaterializeService(
            materializer,
            projectStore,
            scenarios,
            new NullMappingStore(),
            new ActionExecutionGate(),
            accountMappingStore: new ReadyAccountMappingStore());
        var factsPort = new RecordingPlanningFactsPort(order);
        var writer = new RecordingPlanWriter(order);
        using var cancellation = new CancellationTokenSource();
        var artifactStore = new ExecutingArtifactStore(
            projectId,
            CurrentCriteria(
                validationRunId,
                prescreenRunId,
                revision,
                savedUtc),
            StaleWorkingPaper(savedUtc),
            order,
            cancellation,
            rejectUnusedPrepublicationCatalog);
        // 2026-10-02 資料庫分流簡化：handler 改從作用中案件的資料庫組取 writer 與 repository，
        // 原本逐一傳給建構式的替身改放進資料庫組後再進入 session。
        var session = new ProjectSession();
        session.Enter(
            projectId,
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with
            {
                WorkpaperPlanWriter = writer,
                WorkpaperPlanningFacts = factsPort,
                FilterScenarios = scenarios,
                FilterRunMaterializeService = materializeService,
                RuleRuns = runs,
                ResultStaleStates = new FixedResultStaleStateStore(Filter: false),
                ReportArtifactStore = artifactStore,
                MappingStates = new FixedMetadataMappingStore(),
                AccountTaxonomy = new BuiltInAccountTaxonomyStore(),
            });
        var handler = new ExportWorkpaperStreamHandler(projectStore, session, new NullEventPublisher());
        using var payload = JsonDocument.Parse(
            $$"""
            {
              "validationRunId": "{{validationRunId}}",
              "scenarioRevision": "{{revision}}",
              "scenarioPositions": [1]
            }
            """);

        var response = await handler.HandleAsync(payload.RootElement, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);

        Assert.Equal(1, writer.TypedCalls);
        Assert.Equal(0, writer.PublicCalls);
        Assert.Equal(1, factsPort.Calls);
        Assert.False(factsPort.InitialPlan?.IsFinalized);
        Assert.True(writer.Plan?.IsFinalized);
        // 第9批高3；Public首敗101806919-e8b6a1be3d8b4bd3b5bbe7349a92eddc：發布後須再重判全部索引。
        // 保留writer先完成、最後才刷新，以及發布前不讀清單的完整順序與次數斷言。
        Assert.Equal(
            new[] { "stale-refresh", "materialize", "facts", "typed-writer", "stale-refresh" },
            order);

        Assert.Equal(2, artifactStore.MarkStaleCalls);
        Assert.False(artifactStore.CriteriaWasMarkedStale);
        Assert.True(artifactStore.OldWorkingPaperWasMarkedStale);
        // 不再需要 Criteria 檔作為底稿前置，發布前的清單回傳也沒有消費者。
        // 改驗只在發布後刷新清單；過期標記、完整typed計算與已發布內容的斷言仍保留。
        Assert.Equal(1, artifactStore.ListCalls);
        Assert.Equal(new[] { "published", "response-catalog" }, artifactStore.CatalogOrder);
        Assert.Equal(new[] { "criteria-current", "working-paper-old", "working-paper-current" }, Assert.Single(artifactStore.CatalogArtifactIds));
        var data = JsonSerializer.SerializeToElement(response, JetJsonStorage.Options);
        var catalog = data.GetProperty("reportArtifacts");
        Assert.Equal(new[] { "criteria-current", "working-paper-old", "working-paper-current" }, catalog.EnumerateArray()
            .Select(artifact => artifact.GetProperty("artifactId").GetString()));
        var old = Assert.Single(catalog.EnumerateArray(), artifact => artifact.GetProperty("artifactId").GetString() == "working-paper-old");
        Assert.True(old.GetProperty("stale").GetBoolean());
        var published = Assert.Single(catalog.EnumerateArray(), artifact => artifact.GetProperty("artifactId").GetString() == "working-paper-current");
        Assert.False(published.GetProperty("stale").GetBoolean());
        Assert.Equal(data.GetProperty("artifact").GetRawText(), published.GetRawText());

        Assert.Equal(projectId, writer.Context?.ProjectId);
        Assert.Equal(validationRunId, writer.Context?.ValidationRunId);
        Assert.Null(typeof(WorkpaperContext).GetProperty("PrescreenRunId"));
        Assert.Equal(revision, writer.Context?.ScenarioRevision);
        Assert.Equal(new[] { 1 }, writer.Context?.ScenarioPositions);
        Assert.Null(writer.Context?.ScenarioConditionLogic);

        var plan = Assert.IsType<WorkpaperPlan>(writer.Plan);
        Assert.Equal(validationRunId, plan.Request.ValidationRunId);
        Assert.Null(typeof(WorkpaperRequest).GetProperty("PrescreenRunId"));
        Assert.Equal(revision, plan.Request.ScenarioRevision);
        Assert.NotNull(plan.Request.WorkbookMetadata);
        Assert.Empty(plan.Request.CustomFields ?? []);
        var selected = Assert.Single(plan.Scenarios);
        Assert.Equal(1, selected.Position);
        Assert.Equal("未預期借貸組合", selected.Name);
        Assert.Equal("驗證舊 IDEA 工作底稿標記範圍", selected.Rationale);
        Assert.Equal(WorkpaperScenarioTagScope.HitVoucherRows, selected.TagScope);
        // Every saved rule now carries its full explanation into Working Paper, including prescreen rules.
        Assert.Equal("預篩選：未預期借貸組合", selected.ConditionLogic);
        Assert.Equal(7, selected.VoucherHitCount);
        Assert.Equal(3, selected.RowHitCount);
        var fieldInfo = Assert.IsType<FieldInfoProjection>(plan.FieldInfo);
        Assert.Equal(
            [new FieldInfoRow("來源科目", "文字型態", 18, null, "會計科目編號_TB")],
            fieldInfo.TbRows);
        Assert.Equal(
            [new FieldInfoRow("來源金額", "數字型態", null, 4, "傳票金額_JE")],
            fieldInfo.GlRows);
        Assert.Equal(
            new[] { 1 },
            Sheet(plan, WorkpaperSheetCatalog.Step4).ScenarioPositions);
        Assert.Equal(
            new[] { 1 },
            Sheet(plan, WorkpaperSheetCatalog.Step41).ScenarioPositions);
        Assert.Equal(
            "基於上述程序，查核團隊對於JE測試母體之完整性，尚需於Step1-3說明以取得足夠的查核證據。",
            Sheet(plan, WorkpaperSheetCatalog.Step1).Conclusion);

        var source = Assert.IsType<ReportArtifactSourceRefs>(
            artifactStore.WorkingPaperRequest?.SourceRef);
        Assert.Equal(validationRunId, source.ValidationRunId);
        Assert.Null(source.PrescreenRunId);
        Assert.Equal(revision, source.ScenarioRevision);
        Assert.Equal(new[] { 1 }, source.ScenarioPositions);
    }

    private static WorkpaperSheetPlan Sheet(WorkpaperPlan plan, string name) =>
        plan.Sheets.Single(sheet =>
            string.Equals(sheet.SheetName, name, StringComparison.Ordinal));

    private static RuleRunRecord Run(string id, string kind, string logicVersion) => new(
        id,
        kind,
        new DateTimeOffset(2026, 7, 19, 3, 0, 0, TimeSpan.Zero),
        string.Equals(kind, RuleRunKinds.Validate, StringComparison.Ordinal)
            ? CurrentValidationSummaryTestData.Create(
                id,
                new DateTimeOffset(2026, 7, 19, 3, 0, 0, TimeSpan.Zero),
                logicVersion)
            : $"{{\"resultRef\":{{\"logicVersion\":\"{logicVersion}\"}}}}");

    private static SavedFilterScenario Scenario(DateTimeOffset savedUtc) => new(
        Position: 1,
        Name: "未預期借貸組合",
        Rationale: "驗證舊 IDEA 工作底稿標記範圍",
        DefinitionJson:
            $$"""
            {
              "name": "未預期借貸組合",
              "rationale": "驗證舊 IDEA 工作底稿標記範圍",
              "groups": [
                {
                  "join": "AND",
                  "rules": [
                    { "join": "AND", "type": "prescreen", "prescreenKey": "unexpectedAccountPair" }
                  ]
                }
              ],
              "populationScope": "auditPeriod",
              "logicVersion": "{{RuleLogicVersions.Filter}}"
            }
            """,
        SavedUtc: savedUtc);

    private static ProjectDocument Project(string projectId) => new(
        ProjectId: projectId,
        ProjectCode: "TYPED-WORKPAPER",
        EntityName: "Typed Workpaper Entity",
        OperatorId: "tester",
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        LastAccountingPeriodDate: "2024-12-31",
        MoneyScale: 10_000,
        RoundingMode: ProjectDocument.DefaultRoundingMode,
        CreatedUtc: DateTimeOffset.UnixEpoch,
        CurrentStep: 5,
        SchemaVersion: ProjectDocument.CurrentSchemaVersion);

    private static ReportArtifact CurrentCriteria(
        string validationRunId,
        string prescreenRunId,
        string revision,
        DateTimeOffset generatedUtc) => new(
            "criteria-current",
            ReportArtifactKind.CriteriaSelectionReport,
            "criteria-current.xlsx",
            new ReportArtifactSourceRefs(
                validationRunId,
                prescreenRunId,
                revision,
                // 第9批高3：這是刻意保持current的fixture，明示與FixedResultStaleStateStore相同來源版本；舊報告缺版本另有固定stale測試。
                [1], FilterDataRevision: "synthetic-filter-data-revision"),
            generatedUtc,
            1,
            LastWriteUtc: null,
            Stale: false);

    private static ReportArtifact StaleWorkingPaper(DateTimeOffset generatedUtc) => new(
        "working-paper-old",
        ReportArtifactKind.WorkingPaper,
        "working-paper-old.xlsx",
        new ReportArtifactSourceRefs(
            "validation-old",
            "prescreen-current",
            "old-revision",
            [1]),
        generatedUtc.AddMinutes(-1),
        1,
        LastWriteUtc: null,
        Stale: false);

    private sealed class RecordingPlanWriter(List<string> order) : IWorkpaperPlanWriter
    {
        public int PublicCalls { get; private set; }

        public int TypedCalls { get; private set; }

        public WorkpaperContext? Context { get; private set; }

        public WorkpaperPlan? Plan { get; private set; }

        public Task<ExportStats> WriteAsync(
            Stream output,
            WorkpaperContext context,
            CancellationToken cancellationToken,
            Action<WorkpaperProgress>? progress = null)
        {
            PublicCalls++;
            throw new InvalidOperationException(
                "Production Workpaper export must not use the public compatibility writer.");
        }

        public Task<ExportStats> WriteAsync(
            Stream output,
            WorkpaperContext context,
            WorkpaperPlan plan,
            CancellationToken cancellationToken,
            Action<WorkpaperProgress>? progress = null)
        {
            order.Add("typed-writer");
            TypedCalls++;
            Context = context;
            Plan = plan;
            return Task.FromResult(new ExportStats(
                0,
                [new SheetStat(WorkpaperSheetCatalog.Cover, 0)]));
        }
    }

    private sealed class RecordingPlanningFactsPort(List<string> order)
        : IWorkpaperPlanningFactsPort
    {
        public int Calls { get; private set; }

        public WorkpaperPlan? InitialPlan { get; private set; }

        public Task<WorkpaperPlanningFacts> ExecuteAsync(
            WorkpaperPlan plan,
            CancellationToken cancellationToken)
        {
            order.Add("facts");
            Calls++;
            InitialPlan = plan;
            return Task.FromResult(new WorkpaperPlanningFacts(
                HasCompletenessDifferences: true,
                HasUnbalancedDocuments: true,
                ScenarioHitCounts: new Dictionary<
                    int,
                    (long VoucherHitCount, long RowHitCount)>
                {
                    [1] = (7, 3)
                },
                TargetTbDefinitions:
                [
                    new LegacyFieldDefinition(
                        1,
                        "會計科目編號_TB",
                        "來源科目",
                        LegacyFieldKind.Text,
                        18,
                        null)
                ],
                TargetGlDefinitions:
                [
                    new LegacyFieldDefinition(
                        1,
                        "傳票金額_JE",
                        "來源金額",
                        LegacyFieldKind.Number,
                        null,
                        4)
                ]));
        }
    }

    private sealed class RecordingMaterializer(List<string> order)
        : IFilterRunMaterializer
    {
        public Task MaterializeAsync(
            string projectId,
            IReadOnlyList<MaterializableScenario> scenarios,
            FilterRuleContext context,
            CancellationToken cancellationToken,
            bool replaceAll = true)
        {
            order.Add("materialize");
            Assert.Equal("typed-workpaper-export", projectId);
            Assert.Single(scenarios);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingScenarioStore(IReadOnlyList<SavedFilterScenario> scenarios)
        : IFilterScenarioStore
    {
        public int ListCalls { get; private set; }

        public Task ReplaceAllAsync(
            string projectId,
            IReadOnlyList<SavedFilterScenario> replacement,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SavedFilterScenario>> ListAsync(
            string projectId,
            CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult(scenarios);
        }
    }

    private sealed class CurrentRunStore(
        RuleRunRecord validation,
        RuleRunRecord prescreen) : IRuleRunStore
    {
        public Task SaveAsync(
            string projectId,
            RuleRunRecord record,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RuleRunRecord?> FindLatestAsync(
            string projectId,
            string runKind,
            CancellationToken cancellationToken) =>
            Task.FromResult<RuleRunRecord?>(
                runKind == RuleRunKinds.Validate ? validation : prescreen);
    }

    private sealed class FixedResultStaleStateStore(bool Filter) : IResultStaleStateStore
    {
        public Task InvalidateForPreparationDateChangeAsync(string projectId, Func<CancellationToken, Task> saveSettings, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<string> ReadFilterDataRevisionAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult("synthetic-filter-data-revision");
        public Task<AuditResultStaleState> ReadAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AuditResultStaleState(
                Validation: false,
                Prescreen: false,
                Filter));
    }

    private sealed class FixedProjectStore(ProjectDocument document) : IProjectStore
    {
        public Task CreateAsync(
            ProjectDocument created,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        // 第 9 批中低 12：測試替身沿用原本的正常清單，不在產品介面提供相容實作。
        public Task<IReadOnlyList<ProjectStoreEntry>> ListEntriesAsync(CancellationToken cancellationToken) =>
            ProjectStoreTestEntries.FromAsync(ListAsync(cancellationToken));

        public Task<IReadOnlyList<ProjectDocument>> ListAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProjectDocument>>([document]);

        public Task<ProjectDocument?> FindAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            Task.FromResult<ProjectDocument?>(document);

        public Task SaveAsync(
            ProjectDocument saved,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ExecutingArtifactStore(
        string expectedProjectId,
        ReportArtifact criteria,
        ReportArtifact oldWorkingPaper,
        List<string> order,
        CancellationTokenSource operationCancellation,
        bool rejectUnusedPrepublicationCatalog) : IReportArtifactStore
    {
        private ReportArtifact[] artifacts = [criteria, oldWorkingPaper];

        public int MarkStaleCalls { get; private set; }

        public int ListCalls { get; private set; }
        public List<string> CatalogOrder { get; } = [];
        public List<string[]> CatalogArtifactIds { get; } = [];
        private bool _published;

        public bool CriteriaWasMarkedStale { get; private set; }

        public bool OldWorkingPaperWasMarkedStale { get; private set; }

        public ReportArtifactWriteRequest? WorkingPaperRequest { get; private set; }

        public async Task<ReportArtifact> WriteAsync(
            string projectId,
            ReportArtifactWriteRequest request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(expectedProjectId, projectId);
            Assert.Equal(operationCancellation.Token, cancellationToken);
            Assert.Empty(CatalogOrder);
            WorkingPaperRequest = request;
            await using var output = new MemoryStream();
            await request.WriteContentAsync(output, cancellationToken);
            var artifact = new ReportArtifact(
                "working-paper-current",
                request.Kind,
                "working-paper-current.xlsx",
                request.SourceRef,
                new DateTimeOffset(2026, 7, 19, 3, 10, 0, TimeSpan.Zero),
                output.Length,
                LastWriteUtc: null,
                Stale: false);
            artifacts = [.. artifacts, artifact];
            _published = true;
            CatalogOrder.Add("published");
            operationCancellation.Cancel();
            return artifact;
        }

        public Task<IReadOnlyList<ReportArtifact>> WriteBatchAsync(
            string projectId,
            IReadOnlyList<ReportArtifactWriteRequest> requests,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ReportArtifact>> ListAsync(
            string projectId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(expectedProjectId, projectId);
            ListCalls++;
            if (!_published)
            {
                if (rejectUnusedPrepublicationCatalog)
                    throw new IOException("synthetic optional prepublication catalog read failed");
                Assert.Equal(1, ListCalls);
                Assert.Equal(operationCancellation.Token, cancellationToken);
                Assert.False(cancellationToken.IsCancellationRequested);
                Assert.Equal(new[] { "stale-refresh" }, order);
                CatalogOrder.Add("source-catalog");
            }
            else
            {
                Assert.Equal(1, ListCalls);
                Assert.True(operationCancellation.IsCancellationRequested);
                // 發布後清單有自己的短期限；使用者的晚到取消不得傳入此刷新。
                Assert.True(cancellationToken.CanBeCanceled);
                Assert.False(cancellationToken.IsCancellationRequested);
                // 第9批高3，發布後清單要在第二次來源重判之後；其餘取消與發布順序斷言保留。
                Assert.Equal(new[] { "stale-refresh", "materialize", "facts", "typed-writer", "stale-refresh" }, order);
                Assert.Equal(new[] { "published" }, CatalogOrder);
                CatalogOrder.Add("response-catalog");
            }
            CatalogArtifactIds.Add(artifacts.Select(artifact => artifact.ArtifactId).ToArray());
            return Task.FromResult<IReadOnlyList<ReportArtifact>>(artifacts.ToArray());
        }

        public Task<string> ResolvePathAsync(
            string projectId,
            string artifactId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(criteria.ArtifactId, artifactId);
            return Task.FromResult(criteria.RelativeFileName);
        }

        public Task<int> MarkStaleAsync(
            string projectId,
            ReportArtifactKind kind,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> MarkStaleAsync(
            string projectId,
            Func<ReportArtifact, bool> predicate,
            CancellationToken cancellationToken)
        {
            order.Add("stale-refresh");
            MarkStaleCalls++;
            CriteriaWasMarkedStale = predicate(criteria);
            OldWorkingPaperWasMarkedStale = predicate(oldWorkingPaper);
            artifacts = artifacts
                .Select(artifact =>
                    !artifact.Stale && predicate(artifact)
                        ? artifact with { Stale = true }
                        : artifact)
                .ToArray();
            return Task.FromResult(OldWorkingPaperWasMarkedStale ? 1 : 0);
        }

        public Task<IAsyncDisposable> AcquireProjectDeletionLeaseAsync(
            string projectId,
            CancellationToken cancellationToken) =>
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

    private sealed class FixedMetadataMappingStore : IMappingStateStore
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
            Task.FromResult<CommittedMapping?>(kind == DatasetKind.Gl
                ? new CommittedMapping(
                    DatasetKind.Gl,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    GlAmountModeNames.Signed,
                    "typed-workpaper-gl",
                    DateTimeOffset.UnixEpoch)
                : null);
    }

    private sealed class BuiltInAccountTaxonomyStore : IAccountTaxonomyStore
    {
        public Task<AccountTaxonomySnapshot> ReadAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            Task.FromResult(AccountTaxonomyCatalog.BuiltInSnapshot);

        public Task<AccountTaxonomySnapshot> SaveAsync(
            string projectId,
            int expectedRevision,
            IReadOnlyList<AccountTaxonomyCategory> categories,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    // 保存情境補算現在也驗證科目配對。明示此案例具備必要分類，不跳過產品檢查或放寬原有斷言。
    private sealed class ReadyAccountMappingStore : IAccountMappingStore
    {
        public Task<AccountMappingImportResult> ImportAsync(string projectId, ImportSourceDescriptor source,
            IReadOnlyList<string> columns, IAsyncEnumerable<StagingRow> rows, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<AccountMappingState?> FindStateAsync(string projectId, CancellationToken cancellationToken)
            => Task.FromResult<AccountMappingState?>(new("batch", 2, "synthetic.xlsx",
                DateTimeOffset.UnixEpoch, HasAnyCategory: true, HasRevenue: true, HasCounterpart: true));
    }
}
