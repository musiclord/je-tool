using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

// 預篩選報告寫出器只有一個正式入口，handler 一定先規劃並載入工作簿中繼資料；
// 兩則測試各自守住投影與計畫的內容。
public sealed class ExportPrescreenReportTypedSeamTests
{
    [Fact]
    public async Task ReportExecutionPort_RejectsContentRequestsOutsidePlanBeforeStoreWrite()
    {
        var plan = JetAuditProgram.Plan(new ReportExportRequest(
            "export.prescreenReport",
            "typed-prescreen-export",
            PrescreenResultId: "prescreen-run"));
        IReadOnlyList<ReportWriteRequest>[] mismatches =
        [
            [],
            [
                new ReportWriteRequest(
                    ReportKind.ValidationReport,
                    plan.SourceRef,
                    static (_, _) => Task.CompletedTask)
            ],
            [
                new ReportWriteRequest(
                    ReportKind.PrescreenReport,
                    new ReportSourceRefs(PrescreenResultId: "other-run"),
                    static (_, _) => Task.CompletedTask)
            ]
        ];

        foreach (var requests in mismatches)
        {
            var store = new ExecutingReportStore(
                "typed-prescreen-export",
                new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new ReportExecutionPort(store, requests)
                    .ExecuteAsync(plan, CancellationToken.None));
            Assert.Empty(store.Requests);
        }
    }

    [Fact]
    public async Task CurrentPrescreenResult_UsesTypedWriterWithNamedProjectionParsedFromSavedSummary()
    {
        const string projectId = "typed-prescreen-export";
        const string resultId = "prescreen-run";
        var generatedUtc = new DateTimeOffset(2025, 12, 31, 10, 30, 0, TimeSpan.Zero);
        // 已存摘要會做形狀檢查，所以夾具帶齊識別資訊與人員列欄位。
        var summaryJson = CompletePrescreenSummary(JsonSerializer.Serialize(
            new
            {
                prepPeriodApproval = new { status = "na", naReason = (string?)null, count = 0L },
                suspiciousKeywords = new { status = "V", count = 3L },
                unexpectedAccountPair = new
                {
                    status = "na",
                    naReason = "尚未完成科目配對或缺少必要分類",
                    count = 0L
                },
                trailingZeros = new { status = "V", count = 4L, zerosThreshold = 3 },
                // 預篩選結果一定帶 totalPreparerCount，沒有「改用清單列數」的退路；這份摘要照現在的形狀帶人員數。
                preparerSummary = new
                {
                    status = "V",
                    naReason = (string?)null,
                    totalPreparerCount = 2L,
                    preparers = new[]
                    {
                        new { createdBy = "A", entryCount = 1L, debitTotal = 0m, creditTotal = 0m, manualCount = 0L },
                        new { createdBy = "B", entryCount = 1L, debitTotal = 0m, creditTotal = 0m, manualCount = 0L }
                    }
                },
                rareAccounts = new
                {
                    status = "V",
                    distinctAccountCount = 11L,
                    accounts = Array.Empty<object>()
                },
                blankDescription = new { status = "na", count = 0L },
                resultRef = new { resultId, generatedUtc, logicVersion = RuleLogicVersions.Prescreen }
            },
            JetJsonStorage.Options));
        var session = new ProjectSession();
        var writer = new RecordingPrescreenWriter();
        using var cancellation = new CancellationTokenSource();
        var reportStore = new ExecutingReportStore(projectId, generatedUtc, cancellation.Cancel);
        var handler = CreateHandler(
            projectId,
            writer,
            new FixedRunStore(
                new ResultRecord(
                    resultId,
                    ResultKind.Prescreen,
                    generatedUtc,
                    summaryJson),
                EligibleValidationResult(generatedUtc)),
            new FixedProjectStore(Project(projectId)),
            reportStore,
            session,
            new NullEventPublisher(),
            new RecordingPlanningFactsPort());
        using var payload = JsonDocument.Parse($$"""{"resultId":"{{resultId}}"}""");

        var response = await handler.HandleAsync(payload.RootElement, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        AssertPublishedCatalog(response, reportStore);

        Assert.Equal(1, writer.Calls);
        Assert.NotNull(writer.Plan);
        Assert.NotNull(writer.WorkbookMetadata);
        Assert.Equal(
            new PrescreenReportProjection(
                PrepPeriodApproval: new("na", null),
                SuspiciousKeywords: new("V", null, 3),
                UnexpectedAccountPair: new(
                    "na",
                    "尚未完成科目配對或缺少必要分類"),
                TrailingZeros: new("V", null, 4),
                PreparerSummary: new("V", null, 2),
                RareAccounts: new("V", null, 11),
                BlankDescription: new("na", null))
            {
                // 0 的個數照 IDEA 版由 GL 算出，投影帶那次預篩選的個數，預期值取上面 summary 寫的 3。
                ZerosThreshold = 3
            },
            writer.Projection);
        Assert.Equal(summaryJson, writer.Context?.SummaryJson);
        Assert.Equal(resultId, writer.Context?.ResultId);
        Assert.Equal("tester", writer.OperatorId);
        var request = Assert.Single(reportStore.Requests);
        Assert.Equal(ReportKind.PrescreenReport, request.Kind);
        Assert.Equal(resultId, request.SourceRef.PrescreenResultId);
    }

    [Fact]
    public async Task CurrentPrescreenResult_FinalizesPlanningFactsBeforeCallingPlannedWriter()
    {
        const string projectId = "planned-prescreen-export";
        const string resultId = "prescreen-run";
        var generatedUtc = new DateTimeOffset(
            2025,
            12,
            31,
            10,
            30,
            0,
            TimeSpan.Zero);
        var summaryJson = CompletePrescreenSummary(JsonSerializer.Serialize(
            new
            {
                prepPeriodApproval = new
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
                // 保存的預篩選結果要記下那次用的 0 的個數，報告讀這裡；這則不看個數，帶 6。
                trailingZeros = new { status = "V", count = 10_000L, zerosThreshold = 6 },
                preparerSummary = new
                {
                    status = "V",
                    naReason = (string?)null,
                    totalPreparerCount = 0L,
                    preparers = Array.Empty<object>()
                },
                rareAccounts = new
                {
                    status = "V",
                    distinctAccountCount = 0L,
                    accounts = Array.Empty<object>()
                },
                blankDescription = new { status = "na", count = 0L },
                resultRef = new { resultId, generatedUtc, logicVersion = RuleLogicVersions.Prescreen }
            },
            JetJsonStorage.Options));
        var session = new ProjectSession();
        var writer = new RecordingPrescreenWriter();
        var factsPort = new RecordingPlanningFactsPort();
        var reportStore = new ExecutingReportStore(projectId, generatedUtc);
        var handler = CreateHandler(
            projectId,
            writer,
            new FixedRunStore(
                new ResultRecord(
                    resultId,
                    ResultKind.Prescreen,
                    generatedUtc,
                    summaryJson),
                EligibleValidationResult(generatedUtc)),
            new FixedProjectStore(Project(projectId)),
            reportStore,
            session,
            new NullEventPublisher(),
            factsPort);
        using var payload = JsonDocument.Parse(
            $$"""{"resultId":"{{resultId}}"}""");

        var response = await handler.HandleAsync(
            payload.RootElement,
            CancellationToken.None);
        AssertPublishedCatalog(response, reportStore);

        Assert.Equal(1, factsPort.Calls);
        Assert.Equal(1, writer.Calls);
        var plan = Assert.IsType<PrescreenReportPlan>(writer.Plan);
        Assert.True(plan.IsFinalized);
        Assert.Equal(
            PrescreenReportDetailDisposition.Omit,
            plan.RequireDetail(
                PrescreenReportDetailKind.PrepPeriodApproval).Disposition);
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

    private static string CompletePrescreenSummary(string scenarioJson)
    {
        // 共用夾具提供本測試不關心的完整零命中區塊；本測試自行指定的投影事實照原值覆蓋，預期值仍由上方固定答案決定。
        var summary = JsonNode.Parse(ReportWriterTestSupport.PrescreenSummaryJson())!.AsObject();
        foreach (var property in JsonNode.Parse(scenarioJson)!.AsObject()) summary[property.Key] = property.Value?.DeepClone();
        return summary.ToJsonString(JetJsonStorage.Options);
    }

    // handler 從作用中案件的資料庫組取 writer 與 repository，建構式只有案件 store、
    // session 與事件。沒有退回路徑，規劃替身與中繼資料用的 store 都必須提供。
    private static ExportPrescreenReportHandler CreateHandler(
        string projectId,
        IPrescreenReportWriter writer,
        IResultStore resultStore,
        IProjectStore projectStore,
        IReportStore reportStore,
        ProjectSession session,
        IJetEventPublisher eventPublisher,
        IPrescreenReportPlanningFactsPort prescreenReportPlanningFactsPort)
    {
        session.Enter(
            projectId,
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with
            {
                PrescreenReportWriter = writer,
                Results = resultStore,
                ReportStore = reportStore,
                ResultStaleStates = EmptyReportStateTestData.StaleStates,
                FilterScenarios = EmptyReportStateTestData.Scenarios,
                PrescreenReportPlanningFacts = prescreenReportPlanningFactsPort,
                MappingStates = new ReportWriterTestSupport.FixedMetadataMappingStore(),
                AccountCategories = new ReportWriterTestSupport.BuiltInAccountCategoryStore(),
            });
        return new ExportPrescreenReportHandler(projectStore, session, eventPublisher);
    }

    private static ProjectDocument Project(string projectId) => new(
        ProjectId: projectId,
        ProjectCode: "TYPED-PRESCREEN",
        ClientName: "Typed Prescreen Entity",
        OperatorId: "tester",
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        LastAccountingPeriodDate: "2024-12-31",
        MoneyScale: ProjectDocument.DefaultMoneyScale,
        RoundingMode: ProjectDocument.DefaultRoundingMode,
        CreatedUtc: DateTimeOffset.UnixEpoch,
        CurrentStep: 4,
        SchemaVersion: ProjectDocument.CurrentSchemaVersion);

    private static ResultRecord EligibleValidationResult(DateTimeOffset generatedUtc) =>
        new(
            "validation-run",
            ResultKind.Validation,
            generatedUtc,
            CurrentValidationSummaryTestData.Create(
                "validation-run",
                generatedUtc));

    private sealed class RecordingPrescreenWriter : IPrescreenReportWriter
    {
        internal int Calls { get; private set; }

        internal PrescreenReportContext? Context { get; private set; }

        internal PrescreenReportProjection? Projection { get; private set; }

        internal string? OperatorId { get; private set; }

        internal PrescreenReportPlan? Plan { get; private set; }

        internal ReportWorkbookMetadata? WorkbookMetadata { get; private set; }

        public Task<ExportStats> WriteAsync(
            Stream output,
            PrescreenReportContext context,
            PrescreenReportProjection projection,
            PrescreenReportPlan plan,
            ReportWorkbookMetadata workbookMetadata,
            string operatorId,
            CancellationToken cancellationToken,
            Action<WorkingPaperProgress>? progress = null)
        {
            Calls++;
            Context = context;
            Projection = projection;
            Plan = plan;
            WorkbookMetadata = workbookMetadata;
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
                    [PrescreenReportDetailKind.PrepPeriodApproval] =
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

    private sealed class FixedRunStore(params ResultRecord[] runs) : IResultStore
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
            Task.FromResult<ResultRecord?>(runs.SingleOrDefault(run =>
                run.Kind == kind));
    }

    private sealed class FixedProjectStore(ProjectDocument document) : IProjectStore
    {
        public Task CreateAsync(
            ProjectDocument document,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        // 測試替身沿用原本的正常清單，不在產品介面提供相容實作。
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

    private static void AssertPublishedCatalog(object? response, ExecutingReportStore store)
    {
        Assert.Equal(new[] { "content-written", "published", "catalog-read" }, store.CatalogOrder);
        var data = JsonSerializer.SerializeToElement(response, JetJsonStorage.Options);
        var published = Assert.Single(data.GetProperty("reports").EnumerateArray());
        Assert.Equal("report", published.GetProperty("reportId").GetString());
        Assert.Equal("prescreenReport", published.GetProperty("kind").GetString());
        Assert.Equal(data.GetProperty("publishedReport").GetRawText(), published.GetRawText());
    }

    private sealed class ExecutingReportStore(
        string expectedProjectId,
        DateTimeOffset generatedUtc,
        Action? afterPublication = null) : IReportStore
    {
        internal IReadOnlyList<ReportWriteRequest> Requests { get; private set; } = [];
        internal List<string> CatalogOrder { get; } = [];
        private IReadOnlyList<Report> _published = [];

        public async Task<Report> WriteAsync(
            string projectId,
            ReportWriteRequest request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(expectedProjectId, projectId);
            Requests = [request];
            await using var output = new MemoryStream();
            await request.WriteContentAsync(output, cancellationToken);
            CatalogOrder.Add("content-written");
            var report = new Report(
                "report",
                request.Kind,
                $"{ReportKindValues.ToValue(request.Kind)}.xlsx",
                request.SourceRef,
                generatedUtc,
                output.Length,
                LastWriteUtc: null,
                Stale: false);
            _published = [report];
            CatalogOrder.Add("published");
            afterPublication?.Invoke();
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
            // 發布後清單有自己的短期限；使用者的晚到取消不得傳入此刷新。
            Assert.True(cancellationToken.CanBeCanceled);
            Assert.False(cancellationToken.IsCancellationRequested);
            Assert.Equal(new[] { "content-written", "published" }, CatalogOrder);
            Assert.Single(_published);
            CatalogOrder.Add("catalog-read");
            return Task.FromResult<IReadOnlyList<Report>>(_published.ToArray());
        }

        public Task<string> ResolvePathAsync(
            string projectId,
            string reportId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> MarkStaleAsync(
            string projectId,
            ReportKind kind,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        // 正式匯出會重判索引；替身套用相同predicate。
        public Task<int> MarkStaleAsync(
            string projectId,
            Func<Report, bool> predicate,
            CancellationToken cancellationToken)
        {
            Assert.Equal(expectedProjectId, projectId);
            var changed = _published.Count(report => !report.Stale && predicate(report));
            _published = _published.Select(report => predicate(report) ? report with { Stale = true } : report).ToArray();
            return Task.FromResult(changed);
        }

        public Task<IAsyncDisposable> AcquireProjectDeletionLeaseAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
