using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class ExportPrescreenReportTypedSeamTests
{
    [Fact]
    public async Task ArtifactExecutionPort_RejectsContentRequestsOutsidePlanBeforeStoreWrite()
    {
        var plan = JetAuditProgram.Plan(new ReportExportRequest(
            "export.prescreenReport",
            "typed-prescreen-export",
            PrescreenRunId: "prescreen-run"));
        IReadOnlyList<ReportArtifactWriteRequest>[] mismatches =
        [
            [],
            [
                new ReportArtifactWriteRequest(
                    ReportArtifactKind.ValidationReport,
                    plan.SourceRef,
                    static (_, _) => Task.CompletedTask)
            ],
            [
                new ReportArtifactWriteRequest(
                    ReportArtifactKind.PrescreenReport,
                    new ReportArtifactSourceRefs(PrescreenRunId: "other-run"),
                    static (_, _) => Task.CompletedTask)
            ]
        ];

        foreach (var requests in mismatches)
        {
            var store = new ExecutingArtifactStore(
                "typed-prescreen-export",
                new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new ReportArtifactExecutionPort(store, requests)
                    .ExecuteAsync(plan, CancellationToken.None));
            Assert.Empty(store.Requests);
        }
    }

    [Fact]
    public async Task CurrentPrescreenRun_UsesTypedWriterWithNamedProjectionParsedFromSavedSummary()
    {
        const string projectId = "typed-prescreen-export";
        const string runId = "prescreen-run";
        var generatedUtc = new DateTimeOffset(2025, 12, 31, 10, 30, 0, TimeSpan.Zero);
        var summaryJson = JsonSerializer.Serialize(
            new
            {
                postPeriodApproval = new { status = "na", naReason = (string?)null, count = 0L },
                suspiciousKeywords = new { status = "V", count = 3L },
                unexpectedAccountPair = new
                {
                    status = "na",
                    naReason = "尚未完成科目配對或缺少必要分類",
                    count = 0L
                },
                trailingZeros = new { status = "V", count = 4L, zerosThreshold = 3 },
                creatorSummary = new
                {
                    status = "V",
                    naReason = (string?)null,
                    creators = new[]
                    {
                        new { createdBy = "A" },
                        new { createdBy = "B" }
                    }
                },
                rareAccounts = new
                {
                    status = "V",
                    distinctAccountCount = 11L,
                    accounts = Array.Empty<object>()
                },
                blankDescription = new { status = "na", count = 0L },
                resultRef = new { logicVersion = RuleLogicVersions.Prescreen }
            },
            JetJsonStorage.Options);
        var session = new ProjectSession();
        var writer = new RecordingPrescreenWriter();
        using var cancellation = new CancellationTokenSource();
        var artifactStore = new ExecutingArtifactStore(projectId, generatedUtc, cancellation.Cancel);
        var handler = CreateHandler(
            projectId,
            writer,
            new FixedRunStore(
                new RuleRunRecord(
                    runId,
                    RuleRunKinds.Prescreen,
                    generatedUtc,
                    summaryJson),
                EligibleValidationRun(generatedUtc)),
            new FixedProjectStore(Project(projectId)),
            artifactStore,
            session,
            new NullEventPublisher());
        using var payload = JsonDocument.Parse($$"""{"runId":"{{runId}}"}""");

        var response = await handler.HandleAsync(payload.RootElement, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        AssertPublishedCatalog(response, artifactStore);

        Assert.Equal(1, writer.TypedCalls);
        Assert.Equal(0, writer.PlannedCalls);
        Assert.Equal(0, writer.PublicCalls);
        Assert.Equal(
            new PrescreenReportProjection(
                PostPeriodApproval: new("na", null),
                SuspiciousKeywords: new("V", null, 3),
                UnexpectedAccountPair: new(
                    "na",
                    "尚未完成科目配對或缺少必要分類"),
                TrailingZeros: new("V", null, 4),
                CreatorSummary: new("V", null, 2),
                RareAccounts: new("V", null, 11),
                BlankDescription: new("na", null)),
            writer.Projection);
        Assert.Equal(summaryJson, writer.Context?.SummaryJson);
        Assert.Equal(runId, writer.Context?.RunId);
        Assert.Equal("tester", writer.OperatorId);
        var request = Assert.Single(artifactStore.Requests);
        Assert.Equal(ReportArtifactKind.PrescreenReport, request.Kind);
        Assert.Equal(runId, request.SourceRef.PrescreenRunId);
    }

    [Fact]
    public async Task CurrentPrescreenRun_FinalizesPlanningFactsBeforeCallingPlannedWriter()
    {
        const string projectId = "planned-prescreen-export";
        const string runId = "prescreen-run";
        var generatedUtc = new DateTimeOffset(
            2025,
            12,
            31,
            10,
            30,
            0,
            TimeSpan.Zero);
        var summaryJson = JsonSerializer.Serialize(
            new
            {
                postPeriodApproval = new
                {
                    status = "na",
                    naReason = (string?)null,
                    count = 0L
                },
                suspiciousKeywords = new { status = "V", count = 3L },
                unexpectedAccountPair = new
                {
                    status = "na",
                    naReason = "尚未完成科目配對",
                    count = 0L
                },
                trailingZeros = new { status = "V", count = 10_000L },
                creatorSummary = new
                {
                    status = "V",
                    creators = Array.Empty<object>()
                },
                rareAccounts = new
                {
                    status = "V",
                    distinctAccountCount = 0L
                },
                blankDescription = new { status = "na", count = 0L },
                resultRef = new { logicVersion = RuleLogicVersions.Prescreen }
            },
            JetJsonStorage.Options);
        var session = new ProjectSession();
        var writer = new RecordingPrescreenWriter();
        var factsPort = new RecordingPlanningFactsPort();
        var artifactStore = new ExecutingArtifactStore(projectId, generatedUtc);
        var handler = CreateHandler(
            projectId,
            writer,
            new FixedRunStore(
                new RuleRunRecord(
                    runId,
                    RuleRunKinds.Prescreen,
                    generatedUtc,
                    summaryJson),
                EligibleValidationRun(generatedUtc)),
            new FixedProjectStore(Project(projectId)),
            artifactStore,
            session,
            new NullEventPublisher(),
            factsPort);
        using var payload = JsonDocument.Parse(
            $$"""{"runId":"{{runId}}"}""");

        var response = await handler.HandleAsync(
            payload.RootElement,
            CancellationToken.None);
        AssertPublishedCatalog(response, artifactStore);

        Assert.Equal(1, factsPort.Calls);
        Assert.Equal(1, writer.PlannedCalls);
        Assert.Equal(0, writer.TypedCalls);
        Assert.Equal(0, writer.PublicCalls);
        var plan = Assert.IsType<PrescreenReportPlan>(writer.Plan);
        Assert.True(plan.IsFinalized);
        Assert.Equal(
            PrescreenReportDetailDisposition.Omit,
            plan.RequireDetail(
                PrescreenReportDetailKind.PostPeriodApproval).Disposition);
        Assert.Equal(
            PrescreenReportDetailDisposition.Emit,
            plan.RequireDetail(
                PrescreenReportDetailKind.SuspiciousKeywords).Disposition);
        Assert.Equal(
            PrescreenReportDetailDisposition.NotApplicable,
            plan.RequireDetail(
                PrescreenReportDetailKind.UnexpectedAccountPair).Disposition);
        Assert.Equal(
            PrescreenReportDetailDisposition.SummaryOnly,
            plan.RequireDetail(
                PrescreenReportDetailKind.TrailingZeros).Disposition);
        Assert.Equal(
            2,
            plan.RequireDetail(
                PrescreenReportDetailKind.SuspiciousKeywords)
                .Counts!
                .VoucherHitCount);
    }

    // 2026-10-02 資料庫分流簡化：handler 改從作用中案件的資料庫組取 writer 與 repository，建構式只剩案件 store、
    // session 與事件。原本逐一傳給建構式的替身改放進資料庫組；沒傳的 typed 依賴維持 null，handler 照舊走退回路徑。
    private static ExportPrescreenReportHandler CreateHandler(
        string projectId,
        IPrescreenReportWriter writer,
        IRuleRunStore runStore,
        IProjectStore projectStore,
        IReportArtifactStore artifactStore,
        ProjectSession session,
        IJetEventPublisher eventPublisher,
        IPrescreenReportPlanningFactsPort? prescreenReportPlanningFactsPort = null)
    {
        session.Enter(
            projectId,
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with
            {
                PrescreenReportWriter = writer,
                RuleRuns = runStore,
                ReportArtifactStore = artifactStore,
                // 第9批高3；Public首敗100911120後補目前來源的明示空替身，維持原planned/typed/public writer斷言。
                ResultStaleStates = EmptyReportStateTestData.StaleStates,
                FilterScenarios = EmptyReportStateTestData.Scenarios,
                PrescreenReportPlanningFacts = prescreenReportPlanningFactsPort!,
            });
        return new ExportPrescreenReportHandler(projectStore, session, eventPublisher);
    }

    private static ProjectDocument Project(string projectId) => new(
        ProjectId: projectId,
        ProjectCode: "TYPED-PRESCREEN",
        EntityName: "Typed Prescreen Entity",
        OperatorId: "tester",
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        LastAccountingPeriodDate: "2024-12-31",
        MoneyScale: ProjectDocument.DefaultMoneyScale,
        RoundingMode: ProjectDocument.DefaultRoundingMode,
        CreatedUtc: DateTimeOffset.UnixEpoch,
        CurrentStep: 4,
        SchemaVersion: ProjectDocument.CurrentSchemaVersion);

    private static RuleRunRecord EligibleValidationRun(DateTimeOffset generatedUtc) =>
        new(
            "validation-run",
            RuleRunKinds.Validate,
            generatedUtc,
            CurrentValidationSummaryTestData.Create(
                "validation-run",
                generatedUtc));

    private sealed class RecordingPrescreenWriter :
        IPrescreenReportWriter,
        ITypedPrescreenReportWriter,
        IPlannedPrescreenReportWriter
    {
        internal int PublicCalls { get; private set; }

        internal int TypedCalls { get; private set; }

        internal int PlannedCalls { get; private set; }

        internal PrescreenReportContext? Context { get; private set; }

        internal PrescreenReportProjection? Projection { get; private set; }

        internal string? OperatorId { get; private set; }

        internal PrescreenReportPlan? Plan { get; private set; }

        public Task<ExportStats> WriteAsync(
            Stream output,
            PrescreenReportContext context,
            CancellationToken cancellationToken,
            Action<WorkpaperProgress>? progress = null)
        {
            PublicCalls++;
            throw new InvalidOperationException(
                "Production export must use the typed prescreen writer seam.");
        }

        public Task<ExportStats> WriteTypedAsync(
            Stream output,
            PrescreenReportContext context,
            PrescreenReportProjection projection,
            string operatorId,
            CancellationToken cancellationToken,
            Action<WorkpaperProgress>? progress = null)
        {
            TypedCalls++;
            Context = context;
            Projection = projection;
            OperatorId = operatorId;
            return Task.FromResult(new ExportStats(0, []));
        }

        public Task<ExportStats> WritePlannedAsync(
            Stream output,
            PrescreenReportContext context,
            PrescreenReportProjection projection,
            PrescreenReportPlan plan,
            string operatorId,
            CancellationToken cancellationToken,
            Action<WorkpaperProgress>? progress = null)
        {
            PlannedCalls++;
            Context = context;
            Projection = projection;
            Plan = plan;
            OperatorId = operatorId;
            return Task.FromResult(new ExportStats(0, []));
        }
    }

    private sealed class RecordingPlanningFactsPort
        : IPrescreenReportPlanningFactsPort
    {
        internal int Calls { get; private set; }

        public Task<PrescreenReportPlanningFacts> ExecuteAsync(
            PrescreenReportPlan plan,
            CancellationToken cancellationToken)
        {
            Calls++;
            Assert.False(plan.IsFinalized);
            Assert.DoesNotContain(
                plan.Details,
                detail => detail.Kind
                          == PrescreenReportDetailKind.UnexpectedAccountPair
                          && detail.IsApplicable);
            return Task.FromResult(new PrescreenReportPlanningFacts(
                new Dictionary<
                    PrescreenReportDetailKind,
                    PrescreenHitCounts>
                {
                    [PrescreenReportDetailKind.PostPeriodApproval] =
                        new(0, 0),
                    [PrescreenReportDetailKind.SuspiciousKeywords] =
                        new(2, 3),
                    [PrescreenReportDetailKind.TrailingZeros] =
                        new(5, 10_000),
                    [PrescreenReportDetailKind.BlankDescription] =
                        new(0, 0)
                }));
        }
    }

    private sealed class FixedRunStore(params RuleRunRecord[] runs) : IRuleRunStore
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
            Task.FromResult<RuleRunRecord?>(runs.SingleOrDefault(run =>
                string.Equals(run.RunKind, runKind, StringComparison.Ordinal)));
    }

    private sealed class FixedProjectStore(ProjectDocument document) : IProjectStore
    {
        public Task CreateAsync(
            ProjectDocument document,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        // 第 9 批中低 12：測試替身沿用原本的正常清單，不在產品介面提供相容實作。
        public Task<IReadOnlyList<ProjectStoreEntry>> ListEntriesAsync(CancellationToken cancellationToken) =>
            ProjectStoreTestEntries.FromAsync(ListAsync(cancellationToken));

        public Task<IReadOnlyList<ProjectDocument>> ListAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProjectDocument?> FindAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            Task.FromResult<ProjectDocument?>(document);

        public Task SaveAsync(
            ProjectDocument document,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static void AssertPublishedCatalog(object? response, ExecutingArtifactStore store)
    {
        Assert.Equal(new[] { "content-written", "published", "catalog-read" }, store.CatalogOrder);
        var data = JsonSerializer.SerializeToElement(response, JetJsonStorage.Options);
        var published = Assert.Single(data.GetProperty("reportArtifacts").EnumerateArray());
        Assert.Equal("artifact", published.GetProperty("artifactId").GetString());
        Assert.Equal("prescreenReport", published.GetProperty("kind").GetString());
        Assert.Equal(data.GetProperty("artifact").GetRawText(), published.GetRawText());
    }

    private sealed class ExecutingArtifactStore(
        string expectedProjectId,
        DateTimeOffset generatedUtc,
        Action? afterPublication = null) : IReportArtifactStore
    {
        internal IReadOnlyList<ReportArtifactWriteRequest> Requests { get; private set; } = [];
        internal List<string> CatalogOrder { get; } = [];
        private IReadOnlyList<ReportArtifact> _published = [];

        public async Task<ReportArtifact> WriteAsync(
            string projectId,
            ReportArtifactWriteRequest request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(expectedProjectId, projectId);
            Requests = [request];
            await using var output = new MemoryStream();
            await request.WriteContentAsync(output, cancellationToken);
            CatalogOrder.Add("content-written");
            var artifact = new ReportArtifact(
                "artifact",
                request.Kind,
                $"{ReportArtifactKindValues.ToValue(request.Kind)}.xlsx",
                request.SourceRef,
                generatedUtc,
                output.Length,
                LastWriteUtc: null,
                Stale: false);
            _published = [artifact];
            CatalogOrder.Add("published");
            afterPublication?.Invoke();
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
            // 發布後清單有自己的短期限；使用者的晚到取消不得傳入此刷新。
            Assert.True(cancellationToken.CanBeCanceled);
            Assert.False(cancellationToken.IsCancellationRequested);
            Assert.Equal(new[] { "content-written", "published" }, CatalogOrder);
            Assert.Single(_published);
            CatalogOrder.Add("catalog-read");
            return Task.FromResult<IReadOnlyList<ReportArtifact>>(_published.ToArray());
        }

        public Task<string> ResolvePathAsync(
            string projectId,
            string artifactId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> MarkStaleAsync(
            string projectId,
            ReportArtifactKind kind,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        // 第9批高3：正式匯出會重判索引；替身套用相同predicate，不省略任何原writer、發布順序或catalog斷言。
        public Task<int> MarkStaleAsync(
            string projectId,
            Func<ReportArtifact, bool> predicate,
            CancellationToken cancellationToken)
        {
            Assert.Equal(expectedProjectId, projectId);
            var changed = _published.Count(artifact => !artifact.Stale && predicate(artifact));
            _published = _published.Select(artifact => predicate(artifact) ? artifact with { Stale = true } : artifact).ToArray();
            return Task.FromResult(changed);
        }

        public Task<IAsyncDisposable> AcquireProjectDeletionLeaseAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
