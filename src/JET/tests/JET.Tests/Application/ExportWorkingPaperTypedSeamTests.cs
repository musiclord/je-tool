using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 正式工作底稿的唯一 constructor 必須走完整 typed lifecycle，沒有
/// rollback fallback；底稿只經由依計畫寫出的正式入口寫出，公開的相容入口已於 2026-10-22 刪除。此測試同時鎖定惰性
/// stale refresh 的既有 predicate 路徑。
/// </summary>
public sealed class ExportWorkingPaperTypedSeamTests
{
    [Fact]
    public Task SolePath_RunsTypedLifecycleAndWriterAfterLazyStaleRefresh() =>
        VerifyTypedLifecycleAsync(rejectUnusedPrepublicationCatalog: false);

    [Fact]
    public Task UnusedPrepublicationCatalogRead_CannotPreventWorkingPaperPublication() =>
        VerifyTypedLifecycleAsync(rejectUnusedPrepublicationCatalog: true);

    private static async Task VerifyTypedLifecycleAsync(bool rejectUnusedPrepublicationCatalog)
    {
        const string projectId = "typed-working-paper-export";
        const string validationResultId = "validation-current";
        const string prescreenResultId = "prescreen-current";
        var order = new List<string>();
        var savedUtc = new DateTimeOffset(2026, 7, 19, 3, 4, 5, TimeSpan.Zero);
        var scenario = Scenario(savedUtc);
        var scenarios = new RecordingScenarioStore([scenario]);
        var revision = FilterPopulationScopeParser
            .RequireCurrentRevision([scenario])
            .Revision;
        var runs = new CurrentRunStore(
            Run(validationResultId, ResultKind.Validation, RuleLogicVersions.Validation),
            Run(prescreenResultId, ResultKind.Prescreen, RuleLogicVersions.Prescreen));
        var project = Project(projectId);
        var projectStore = new FixedProjectStore(project);
        var materializer = new RecordingMaterializer(order);
        var materializeService = new FilterHitMaterializeService(
            materializer,
            projectStore,
            scenarios,
            new NullMappingStore(),
            new ActionExecutionGate(),
            accountMappingStore: new ReadyAccountMappingStore());
        var factsPort = new RecordingPlanningFactsPort(order);
        var writer = new RecordingPlanWriter(order);
        using var cancellation = new CancellationTokenSource();
        var reportStore = new ExecutingReportStore(
            projectId,
            CurrentCriteria(
                validationResultId,
                prescreenResultId,
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
                WorkingPaperPlanWriter = writer,
                WorkingPaperPlanningFacts = factsPort,
                FilterScenarios = scenarios,
                FilterHitMaterializeService = materializeService,
                Results = runs,
                ResultStaleStates = new FixedResultStaleStateStore(Filter: false),
                ReportStore = reportStore,
                MappingStates = new FixedMetadataMappingStore(),
                AccountCategories = new BuiltInAccountCategoryStore(),
            });
        var handler = new ExportWorkingPaperStreamHandler(projectStore, session, new NullEventPublisher());
        using var payload = JsonDocument.Parse(
            $$"""
            {
              "validationResultId": "{{validationResultId}}",
              "scenarioRevision": "{{revision}}",
              "scenarioPositions": [1]
            }
            """);

        var response = await handler.HandleAsync(payload.RootElement, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);

        Assert.Equal(1, writer.TypedCalls);
        // 原本另斷言公開入口沒被呼叫。2026-10-22 清理技術債時刪掉公開入口，替身不再計次；改由 CompatibilityPathOwnershipTests
        // 斷言它不存在。第一次失敗是刪掉介面後測試專案建置失敗，那次沒有保存收據。
        Assert.Equal(1, factsPort.Calls);
        Assert.False(factsPort.InitialPlan?.IsFinalized);
        Assert.True(writer.Plan?.IsFinalized);
        // Public首敗101806919-e8b6a1be3d8b4bd3b5bbe7349a92eddc：發布後須再重判全部索引。
        // 保留writer先完成、最後才刷新，以及發布前不讀清單的完整順序與次數斷言。
        // 原本斷言順序含 materialize：底稿匯出前一律重算勾稽結果。改成每個條件組儲存時就存好結果、匯出直接讀，
        // 結果沒有過期就不再重算；第一次失敗收據 20261031-015438711-e019d04ff2b64ad08bc34c916334bd83。
        Assert.Equal(
            new[] { "stale-refresh", "facts", "typed-writer", "stale-refresh" },
            order);

        Assert.Equal(2, reportStore.MarkStaleCalls);
        Assert.False(reportStore.CriteriaWasMarkedStale);
        Assert.True(reportStore.OldWorkingPaperWasMarkedStale);
        // 不再需要 Criteria 檔作為底稿前置，發布前的清單回傳也沒有消費者。
        // 改驗只在發布後刷新清單；過期標記、完整typed計算與已發布內容的斷言仍保留。
        Assert.Equal(1, reportStore.ListCalls);
        Assert.Equal(new[] { "published", "response-catalog" }, reportStore.CatalogOrder);
        Assert.Equal(new[] { "criteria-current", "working-paper-old", "working-paper-current" }, Assert.Single(reportStore.CatalogReportIds));
        var data = JsonSerializer.SerializeToElement(response, JetJsonStorage.Options);
        var catalog = data.GetProperty("reports");
        Assert.Equal(new[] { "criteria-current", "working-paper-old", "working-paper-current" }, catalog.EnumerateArray()
            .Select(report => report.GetProperty("reportId").GetString()));
        var old = Assert.Single(catalog.EnumerateArray(), report => report.GetProperty("reportId").GetString() == "working-paper-old");
        Assert.True(old.GetProperty("stale").GetBoolean());
        var published = Assert.Single(catalog.EnumerateArray(), report => report.GetProperty("reportId").GetString() == "working-paper-current");
        Assert.False(published.GetProperty("stale").GetBoolean());
        Assert.Equal(data.GetProperty("publishedReport").GetRawText(), published.GetRawText());

        Assert.Equal(projectId, writer.Context?.ProjectId);
        Assert.Equal(validationResultId, writer.Context?.ValidationResultId);
        Assert.Null(typeof(WorkingPaperContext).GetProperty("PrescreenResultId"));
        Assert.Equal(revision, writer.Context?.ScenarioRevision);
        Assert.Equal(new[] { 1 }, writer.Context?.ScenarioPositions);
        // 原本另斷言 ScenarioConditionLogic 是 null。2026-10-22 清理技術債時刪掉這個屬性，改由 CompatibilityPathOwnershipTests
        // 斷言它不存在。第一次失敗是刪掉屬性後測試專案建置失敗，那次沒有保存收據。

        var plan = Assert.IsType<WorkingPaperPlan>(writer.Plan);
        Assert.Equal(validationResultId, plan.Request.ValidationResultId);
        Assert.Null(typeof(WorkingPaperRequest).GetProperty("PrescreenResultId"));
        Assert.Equal(revision, plan.Request.ScenarioRevision);
        Assert.NotNull(plan.Request.WorkbookMetadata);
        Assert.Empty(plan.Request.CustomFields ?? []);
        var selected = Assert.Single(plan.Scenarios);
        Assert.Equal(1, selected.Position);
        Assert.Equal("未預期借貸組合", selected.Name);
        Assert.Equal("驗證舊 IDEA 工作底稿標記範圍", selected.Rationale);
        Assert.Equal(WorkingPaperScenarioTagScope.HitVoucherRows, selected.TagScope);
        // Every saved rule now carries its full explanation into the WorkingPaper, including prescreen rules.
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
            Sheet(plan, WorkingPaperSheetCatalog.Step4).ScenarioPositions);
        Assert.Equal(
            new[] { 1 },
            Sheet(plan, WorkingPaperSheetCatalog.Step41).ScenarioPositions);
        Assert.Equal(
            "基於上述程序，查核團隊對於JE測試母體之完整性，尚需於Step1-3說明以取得足夠的查核證據。",
            Sheet(plan, WorkingPaperSheetCatalog.Step1).Conclusion);

        var source = Assert.IsType<ReportSourceRefs>(
            reportStore.WorkingPaperRequest?.SourceRef);
        Assert.Equal(validationResultId, source.ValidationResultId);
        Assert.Null(source.PrescreenResultId);
        Assert.Equal(revision, source.ScenarioRevision);
        Assert.Equal(new[] { 1 }, source.ScenarioPositions);
    }

    private static WorkingPaperSheetPlan Sheet(WorkingPaperPlan plan, string name) =>
        plan.Sheets.Single(sheet =>
            string.Equals(sheet.SheetName, name, StringComparison.Ordinal));

    private static ResultRecord Run(string id, ResultKind kind, string logicVersion) => new(
        id,
        kind,
        new DateTimeOffset(2026, 7, 19, 3, 0, 0, TimeSpan.Zero),
        kind == ResultKind.Validation
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
                  "conditions": [
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
        ProjectCode: "TYPED-WORKING_PAPER",
        ClientName: "Typed WorkingPaper Entity",
        OperatorId: "tester",
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        LastAccountingPeriodDate: "2024-12-31",
        MoneyScale: 10_000,
        RoundingMode: ProjectDocument.DefaultRoundingMode,
        CreatedUtc: DateTimeOffset.UnixEpoch,
        CurrentStep: 5,
        SchemaVersion: ProjectDocument.CurrentSchemaVersion);

    private static Report CurrentCriteria(
        string validationResultId,
        string prescreenResultId,
        string revision,
        DateTimeOffset generatedUtc) => new(
            "criteria-current",
            ReportKind.CriteriaSelectionReport,
            "criteria-current.xlsx",
            new ReportSourceRefs(
                validationResultId,
                prescreenResultId,
                revision,
                // 這是刻意保持current的fixture，明示與FixedResultStaleStateStore相同來源版本；舊報告缺版本另有固定stale測試。
                [1], FilterDataRevision: "synthetic-filter-data-revision"),
            generatedUtc,
            1,
            LastWriteUtc: null,
            Stale: false);

    private static Report StaleWorkingPaper(DateTimeOffset generatedUtc) => new(
        "working-paper-old",
        ReportKind.WorkingPaper,
        "working-paper-old.xlsx",
        new ReportSourceRefs(
            "validation-old",
            "prescreen-current",
            "old-revision",
            [1]),
        generatedUtc.AddMinutes(-1),
        1,
        LastWriteUtc: null,
        Stale: false);

    private sealed class RecordingPlanWriter(List<string> order) : IWorkingPaperPlanWriter
    {
        public int TypedCalls { get; private set; }

        public WorkingPaperContext? Context { get; private set; }

        public WorkingPaperPlan? Plan { get; private set; }

        public Task<ExportStats> WriteAsync(
            Stream output,
            WorkingPaperContext context,
            WorkingPaperPlan plan,
            CancellationToken cancellationToken,
            Action<WorkingPaperProgress>? progress = null)
        {
            order.Add("typed-writer");
            TypedCalls++;
            Context = context;
            Plan = plan;
            return Task.FromResult(new ExportStats(
                0,
                [new SheetStat(WorkingPaperSheetCatalog.Cover, 0)]));
        }
    }

    private sealed class RecordingPlanningFactsPort(List<string> order)
        : IWorkingPaperPlanningFactsPort
    {
        public int Calls { get; private set; }

        public WorkingPaperPlan? InitialPlan { get; private set; }

        public Task<WorkingPaperPlanningFacts> ExecuteAsync(
            WorkingPaperPlan plan,
            CancellationToken cancellationToken)
        {
            order.Add("facts");
            Calls++;
            InitialPlan = plan;
            return Task.FromResult(new WorkingPaperPlanningFacts(
                HasCompletenessDifferences: true,
                HasUnbalancedVouchers: true,
                ScenarioHitCounts: new Dictionary<
                    int,
                    (long VoucherHitCount, long RowHitCount)>
                {
                    [1] = (7, 3)
                },
                TargetTbDefinitions:
                [
                    new FieldInfo(
                        1,
                        "會計科目編號_TB",
                        "來源科目",
                        LegacyFieldKind.Text,
                        18,
                        null)
                ],
                TargetGlDefinitions:
                [
                    new FieldInfo(
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
        : IFilterHitMaterializer
    {
        public Task MaterializeAsync(
            string projectId,
            IReadOnlyList<MaterializableScenario> scenarios,
            FilterConditionContext context,
            CancellationToken cancellationToken,
            bool replaceAll = true)
        {
            order.Add("materialize");
            Assert.Equal("typed-working-paper-export", projectId);
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

        public Task RemoveAsync(
            string projectId,
            SavedFilterScenario scenario,
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
        ResultRecord validation,
        ResultRecord prescreen) : IResultStore
    {
        public Task SaveAsync(
            string projectId,
            ResultRecord record,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ResultRecord?> FindLatestAsync(
            string projectId,
            ResultKind kind,
            CancellationToken cancellationToken) =>
            Task.FromResult<ResultRecord?>(
                kind == ResultKind.Validation ? validation : prescreen);
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

        // 測試替身沿用原本的正常清單，不在產品介面提供相容實作。
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

    private sealed class ExecutingReportStore(
        string expectedProjectId,
        Report criteria,
        Report oldWorkingPaper,
        List<string> order,
        CancellationTokenSource operationCancellation,
        bool rejectUnusedPrepublicationCatalog) : IReportStore
    {
        private Report[] reports = [criteria, oldWorkingPaper];

        public int MarkStaleCalls { get; private set; }

        public int ListCalls { get; private set; }
        public List<string> CatalogOrder { get; } = [];
        public List<string[]> CatalogReportIds { get; } = [];
        private bool _published;

        public bool CriteriaWasMarkedStale { get; private set; }

        public bool OldWorkingPaperWasMarkedStale { get; private set; }

        public ReportWriteRequest? WorkingPaperRequest { get; private set; }

        public async Task<Report> WriteAsync(
            string projectId,
            ReportWriteRequest request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(expectedProjectId, projectId);
            Assert.Equal(operationCancellation.Token, cancellationToken);
            Assert.Empty(CatalogOrder);
            WorkingPaperRequest = request;
            await using var output = new MemoryStream();
            await request.WriteContentAsync(output, cancellationToken);
            var report = new Report(
                "working-paper-current",
                request.Kind,
                "working-paper-current.xlsx",
                request.SourceRef,
                new DateTimeOffset(2026, 7, 19, 3, 10, 0, TimeSpan.Zero),
                output.Length,
                LastWriteUtc: null,
                Stale: false);
            reports = [.. reports, report];
            _published = true;
            CatalogOrder.Add("published");
            operationCancellation.Cancel();
            return report;
        }

        public Task<IReadOnlyList<Report>> WriteBatchAsync(
            string projectId,
            IReadOnlyList<ReportWriteRequest> requests,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Report>> ListAsync(
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
                // 發布後清單要在第二次來源重判之後；其餘取消與發布順序斷言保留。
                // 結果沒有過期時匯出不再重算，順序少了 materialize；收據同上方說明。
                Assert.Equal(new[] { "stale-refresh", "facts", "typed-writer", "stale-refresh" }, order);
                Assert.Equal(new[] { "published" }, CatalogOrder);
                CatalogOrder.Add("response-catalog");
            }
            CatalogReportIds.Add(reports.Select(report => report.ReportId).ToArray());
            return Task.FromResult<IReadOnlyList<Report>>(reports.ToArray());
        }

        public Task<string> ResolvePathAsync(
            string projectId,
            string reportId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(criteria.ReportId, reportId);
            return Task.FromResult(criteria.RelativeFileName);
        }

        public Task<int> MarkStaleAsync(
            string projectId,
            ReportKind kind,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> MarkStaleAsync(
            string projectId,
            Func<Report, bool> predicate,
            CancellationToken cancellationToken)
        {
            order.Add("stale-refresh");
            MarkStaleCalls++;
            CriteriaWasMarkedStale = predicate(criteria);
            OldWorkingPaperWasMarkedStale = predicate(oldWorkingPaper);
            reports = reports
                .Select(report =>
                    !report.Stale && predicate(report)
                        ? report with { Stale = true }
                        : report)
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
                    "typed-working-paper-gl",
                    DateTimeOffset.UnixEpoch)
                : null);
    }

    private sealed class BuiltInAccountCategoryStore : IAccountCategoryStore
    {
        public Task<AccountCategorySnapshot> ReadAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            Task.FromResult(AccountCategoryCatalog.BuiltInSnapshot);

        public Task<AccountCategorySnapshot> SaveAsync(
            string projectId,
            int expectedRevision,
            IReadOnlyList<AccountCategory> categories,
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
