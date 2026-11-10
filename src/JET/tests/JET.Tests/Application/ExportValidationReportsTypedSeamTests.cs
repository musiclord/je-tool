using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

// 2026-10-22 清理技術債：驗證報告寫出器只剩一個正式入口，handler 一定先規劃、投影欄位資訊並載入工作簿中繼資料，
// 原本依 writer 型別退回的 typed 與 public 路徑都刪除，對應的替身與「公開 writer 不必解析摘要」的測試一併移除。
// 第一次失敗是刪掉入口後測試專案建置失敗，那次沒有保存收據。
public sealed class ExportValidationReportsTypedSeamTests
{
    // 修正前 2 案失敗：Focused 20261110-044754420-f40bf4188a1e4bcfa8dfd8a65c55d2ee。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishedValidationReports_WhenStateDatabaseReadFails_KeepBothPublishedFiles(bool translated)
    {
        const string projectId = "synthetic-validation-publication";
        const string resultId = "validation-current";
        var time = DateTimeOffset.UnixEpoch;
        var published = false;
        var reports = new ExecutingReportStore(projectId, time, () => published = true);
        var runs = new FixedRunStore(new ResultRecord(resultId, ResultKind.Validation, time,
            CurrentValidationSummaryTestData.Create(resultId, time)))
        {
            ReadFailure = () => !published ? null : translated
                ? new JetActionException(JetErrorCodes.DatabaseCorrupt, "synthetic corrupted state")
                : new Microsoft.Data.Sqlite.SqliteException("synthetic locked state", 5)
        };
        var writer = new RecordingPlannedValidationWriter();
        var handler = CreateHandler(projectId, writer, new NoOpInfWriter(), runs,
            new FixedProjectStore(Project(projectId)), reports, new ProjectSession(), new NullEventPublisher(),
            new RecordingPlanningFactsPort(0), new RecordingFieldInfoFactsPort([], []),
            new BuiltInAccountCategoryStore(), new FixedMetadataMappingStore());

        var result = await handler.HandleAsync(JsonSerializer.SerializeToElement(new { resultId }), CancellationToken.None);
        var wire = JsonSerializer.SerializeToElement(result, JetJsonStorage.Options);

        Assert.True(wire.GetProperty("ok").GetBoolean());
        Assert.Equal(2, wire.GetProperty("publishedReports").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("reports").ValueKind);
        Assert.Equal("檔案已產生，但報告清單沒有更新；重新開啟案件就會更新，不必重新產生。",
            wire.GetProperty("reportWarning").GetString());
        Assert.Equal(1, reports.WriteBatchCalls);
        Assert.Equal(1, writer.Calls);
        Assert.Equal(new[] { "content-written", "published" }, reports.CatalogOrder);
    }

    [Fact]
    public async Task ProductionConstructor_FinalizesPlanAndProjectsTargetFieldInfoForPlannedWriter()
    {
        const string projectId = "planned-validation-export";
        const string resultId = "validation-run";
        var generatedUtc = new DateTimeOffset(2025, 12, 31, 10, 30, 0, TimeSpan.Zero);
        var summaryJson = CurrentValidationSummaryTestData.Create(
            resultId,
            generatedUtc,
            completenessDifferenceCount: 3L,
            unbalancedVoucherCount: 4L,
            blankAccountNumberCount: 10_001L,
            blankVoucherNumberCount: 10_000L,
            blankDescriptionCount: 0L,
            approvalDateNotInPeriodCount: 8L,
            sourceQualityFindingCount: 10_001L);
        var document = Project(projectId);
        var session = new ProjectSession();
        var validationWriter = new RecordingPlannedValidationWriter();
        var reportStore = new ExecutingReportStore(projectId, generatedUtc);
        var planningFacts = new RecordingPlanningFactsPort(unbalancedDetailRowCount: 9_999L);
        var fieldInfos = new RecordingFieldInfoFactsPort(
            tb:
            [
                // 欄位事實多了來歷與對應的 JET 欄位（裁定 2026-11-03-11），手工建的事實照投影器的寫法補上；
                // 改名欄位的「配對前欄位名稱」加【】（裁定 2026-11-03-17）。第一次失敗：Focused 收據 20261103-070040356-7ea024157104489698958ad37a9ef5d4。
                new FieldInfo(2, "期末餘額", null, LegacyFieldKind.Number, null, 0),
                new FieldInfo(1, "會計科目編號_TB", "客戶科目", LegacyFieldKind.Text, 20, null, FieldInfoOrigin.Renamed, JetFieldCatalog.TbAccountNumber)
            ],
            gl:
            [
                new FieldInfo(2, "傳票金額_JE", "來源金額", LegacyFieldKind.Number, null, 4, FieldInfoOrigin.Generated, JetFieldCatalog.GlAmount),
                new FieldInfo(1, "來源日期", null, LegacyFieldKind.Date, null, null)
            ]);
        var handler = CreateHandler(
            projectId,
            validationWriter,
            new NoOpInfWriter(),
            new FixedRunStore(new ResultRecord(
                resultId,
                ResultKind.Validation,
                generatedUtc,
                summaryJson)),
            new FixedProjectStore(document),
            reportStore,
            session,
            new NullEventPublisher(),
            planningFacts,
            fieldInfos,
            new BuiltInAccountCategoryStore(),
            new FixedMetadataMappingStore());
        using var payload = JsonDocument.Parse($$"""{"resultId":"{{resultId}}"}""");

        var response = await handler.HandleAsync(payload.RootElement, CancellationToken.None);
        AssertPublishedCatalog(response, reportStore);

        Assert.Equal(1, validationWriter.Calls);
        Assert.NotNull(validationWriter.WorkbookMetadata);
        Assert.Equal(1, planningFacts.Calls);
        var unfinalizedPlan = Assert.IsType<ValidationReportPlan>(planningFacts.Plan);
        Assert.False(unfinalizedPlan.IsFinalized);
        Assert.Equal(projectId, unfinalizedPlan.Request.ProjectId);
        Assert.Equal("2025-01-01", unfinalizedPlan.Request.PeriodStart);
        Assert.Equal("2025-12-31", unfinalizedPlan.Request.PeriodEnd);
        Assert.Equal(3L, unfinalizedPlan.Request.CompletenessDiffAccountCount);

        var finalizedPlan = Assert.IsType<ValidationReportPlan>(validationWriter.Plan);
        Assert.True(finalizedPlan.IsFinalized);
        Assert.Equal(
            ValidationReportDetailDisposition.SummaryOnly,
            finalizedPlan.RequireDetail(ValidationReportDetailKind.BlankAccountNumber).Disposition);
        Assert.Equal(
            ValidationReportDetailDisposition.Emit,
            finalizedPlan.RequireDetail(ValidationReportDetailKind.BlankVoucherNumber).Disposition);
        Assert.Equal(
            ValidationReportDetailDisposition.Omit,
            finalizedPlan.RequireDetail(ValidationReportDetailKind.BlankDescription).Disposition);
        Assert.Equal(
            ValidationReportDetailDisposition.Emit,
            finalizedPlan.RequireDetail(ValidationReportDetailKind.UnbalancedVoucherEntry).Disposition);
        Assert.Equal(
            9_999L,
            finalizedPlan.RequireDetail(ValidationReportDetailKind.UnbalancedVoucherEntry).Count);
        Assert.Equal(6, finalizedPlan.Details.Count);
        Assert.True(finalizedPlan.EmitCompletenessExplanation);

        Assert.Equal(10_001L, validationWriter.Projection?.BlankAccountNumberCount);
        Assert.Equal(4L, validationWriter.Projection?.UnbalancedVoucherCount);
        var fieldInfo = Assert.IsType<FieldInfoProjection>(validationWriter.FieldInfo);
        Assert.Equal(
            new[]
            {
                new FieldInfoRow("【客戶科目】", "文字型態", 20, null, "會計科目編號_TB"),
                new FieldInfoRow("期末餘額", "數字型態", null, 0, null)
            },
            fieldInfo.TbRows);
        Assert.Equal(
            new[]
            {
                new FieldInfoRow("來源日期", "日期型態", null, null, null),
                new FieldInfoRow("來源金額", "數字型態", null, 4, "傳票金額_JE")
            },
            fieldInfo.GlRows);
        Assert.Equal(
            new[]
            {
                (DatasetKind.Tb, FieldInfoScope.Target),
                (DatasetKind.Gl, FieldInfoScope.Target)
            },
            fieldInfos.Requests.Select(request => (request.Kind, request.Scope)));
        Assert.All(fieldInfos.Requests, request => Assert.Equal(projectId, request.ProjectId));
        Assert.Equal("tester", validationWriter.OperatorId);
    }

    [Fact]
    public async Task CurrentValidationResult_UsesTypedWriterWithProjectionParsedFromSavedSummary()
    {
        const string projectId = "typed-validation-export";
        const string resultId = "validation-run";
        var generatedUtc = new DateTimeOffset(2025, 12, 31, 10, 30, 0, TimeSpan.Zero);
        var summaryJson = CurrentValidationSummaryTestData.Create(
            resultId,
            generatedUtc,
            completenessDifferenceCount: 3L,
            unbalancedVoucherCount: 4L,
            blankAccountNumberCount: 5L,
            blankVoucherNumberCount: 6L,
            blankDescriptionCount: 7L,
            approvalDateNotInPeriodCount: 8L,
            sourceQualityFindingCount: 9L,
            sourceQualitySampleRows:
            [
                new SourceQualityFindingRow(
                    "blankGlDate",
                    12,
                    "source.xlsx [GL]",
                    "JV-1",
                    "1000",
                    null,
                    "blank",
                    EntryId: 0)
            ]);
        var document = Project(projectId);
        var session = new ProjectSession();
        var validationWriter = new RecordingPlannedValidationWriter();
        using var cancellation = new CancellationTokenSource();
        var reportStore = new ExecutingReportStore(projectId, generatedUtc, cancellation.Cancel);
        var handler = CreateHandler(
            projectId,
            validationWriter,
            new NoOpInfWriter(),
            new FixedRunStore(new ResultRecord(
                resultId,
                ResultKind.Validation,
                generatedUtc,
                summaryJson)),
            new FixedProjectStore(document),
            reportStore,
            session,
            new NullEventPublisher(),
            new RecordingPlanningFactsPort(unbalancedDetailRowCount: 4L),
            new RecordingFieldInfoFactsPort([], []),
            new BuiltInAccountCategoryStore(),
            new FixedMetadataMappingStore());
        using var payload = JsonDocument.Parse($$"""{"resultId":"{{resultId}}"}""");

        var response = await handler.HandleAsync(payload.RootElement, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        AssertPublishedCatalog(response, reportStore);

        Assert.Equal(1, validationWriter.Calls);
        var expectedProjection = new ValidationReportProjection(
            Net: 1.25m,
            TotalDebit: 10.50m,
            TotalCredit: 9.25m,
            GlRowCount: 17L,
            CompletenessDiffAccountCount: 3L,
            UnbalancedVoucherCount: 4L,
            BlankAccountNumberCount: 5L,
            BlankVoucherNumberCount: 6L,
            BlankDescriptionCount: 7L,
            ApprovalDateNotInPeriodCount: 8L,
            SourceQualityFindingCount: 9L);
        // 裁定 2026-10-22-20 刪掉投影裡沒人讀的總帳日期空白樣本列：投影只留計數，摘要 JSON 的 sampleRows 照舊留給前端，
        // 上面仍把樣本列寫進摘要，確認解析器會略過它。第一次失敗：Focused 收據 20261022-084228771-7b2316c36ab54315bf1eb067bdad7939（建置失敗）。
        var actualProjection = Assert.IsType<ValidationReportProjection>(validationWriter.Projection);
        Assert.Equal(expectedProjection, actualProjection);
        Assert.Equal(summaryJson, validationWriter.Context?.SummaryJson);
        Assert.Equal(resultId, validationWriter.Context?.ResultId);
        Assert.Equal("tester", validationWriter.OperatorId);
        Assert.Equal(0, reportStore.WriteCalls);
        Assert.Equal(1, reportStore.WriteBatchCalls);
        Assert.Equal(
            new[]
            {
                ReportKind.ValidationReport,
                ReportKind.InfReport
            },
            reportStore.Requests.Select(request => request.Kind).ToArray());
    }

    [Fact]
    public void CurrentValidationSummary_MissingRequiredBlocks_FailsClosed()
    {
        var malformed = JsonSerializer.Serialize(
            new
            {
                stats = new
                {
                    net = 1.25m,
                    totalDebit = 10.50m,
                    totalCredit = 9.25m,
                    glRowCount = 17L
                },
                resultRef = new
                {
                    resultId = "malformed-validation-run",
                    generatedUtc = DateTimeOffset.UnixEpoch,
                    logicVersion = RuleLogicVersions.Validation
                }
            },
            JetJsonStorage.Options);

        var exception = Assert.Throws<JetActionException>(
            () => ValidationReportProjectionParser.Parse(malformed));

        Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
    }

    // 2026-10-02 資料庫分流簡化：handler 改從作用中案件的資料庫組取 writer 與 repository，建構式只剩案件 store、
    // session 與事件。2026-10-22 清理技術債：退回路徑已刪除，規劃、欄位定義、科目分類與欄位配對的替身都必須提供。
    private static ExportValidationReportsHandler CreateHandler(
        string projectId,
        IValidationReportWriter validationWriter,
        IInfReportWriter infWriter,
        IResultStore resultStore,
        IProjectStore projectStore,
        IReportStore reportStore,
        ProjectSession session,
        IJetEventPublisher eventPublisher,
        IValidationReportPlanningFactsPort validationReportPlanningFactsPort,
        IFieldInfoFactsPort fieldInfoFactsPort,
        IAccountCategoryStore accountCategoryStore,
        IMappingStateStore mappingStateStore)
    {
        session.Enter(
            projectId,
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with
            {
                ValidationReportWriter = validationWriter,
                InfReportWriter = infWriter,
                Results = resultStore,
                ReportStore = reportStore,
                // Public首敗100911120的null是Unconfigured省略新必需依賴，非可恢復IO；原斷言保留。
                ResultStaleStates = EmptyReportStateTestData.StaleStates,
                FilterScenarios = EmptyReportStateTestData.Scenarios,
                ValidationReportPlanningFacts = validationReportPlanningFactsPort,
                FieldInfoFacts = fieldInfoFactsPort,
                AccountCategories = accountCategoryStore,
                MappingStates = mappingStateStore,
            });
        return new ExportValidationReportsHandler(projectStore, session, eventPublisher);
    }

    private static ProjectDocument Project(string projectId) => new(
        ProjectId: projectId,
        ProjectCode: "TYPED-EXPORT",
        ClientName: "Typed Export Entity",
        OperatorId: "tester",
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        LastAccountingPeriodDate: "2024-12-31",
        MoneyScale: ProjectDocument.DefaultMoneyScale,
        RoundingMode: ProjectDocument.DefaultRoundingMode,
        CreatedUtc: DateTimeOffset.UnixEpoch,
        CurrentStep: 4,
        SchemaVersion: ProjectDocument.CurrentSchemaVersion);

    private sealed class RecordingPlannedValidationWriter : IValidationReportWriter
    {
        internal int Calls { get; private set; }

        internal ValidationReportContext? Context { get; private set; }

        internal ValidationReportPlan? Plan { get; private set; }

        internal FieldInfoProjection? FieldInfo { get; private set; }

        internal ValidationReportProjection? Projection { get; private set; }

        internal ReportWorkbookMetadata? WorkbookMetadata { get; private set; }

        internal string? OperatorId { get; private set; }

        public Task<ExportStats> WriteAsync(
            Stream output,
            ValidationReportContext context,
            ValidationReportProjection projection,
            ValidationReportPlan plan,
            FieldInfoProjection fieldInfo,
            ReportWorkbookMetadata workbookMetadata,
            string operatorId,
            CancellationToken cancellationToken,
            Action<WorkingPaperProgress>? progress = null)
        {
            Calls++;
            Context = context;
            Plan = plan;
            FieldInfo = fieldInfo;
            Projection = projection;
            WorkbookMetadata = workbookMetadata;
            OperatorId = operatorId;
            return Task.FromResult(new ExportStats(0, []));
        }
    }

    private sealed class RecordingPlanningFactsPort(long unbalancedDetailRowCount)
        : IValidationReportPlanningFactsPort
    {
        internal int Calls { get; private set; }

        internal ValidationReportPlan? Plan { get; private set; }

        public Task<ValidationReportPlanningFacts> ExecuteAsync(
            ValidationReportPlan plan,
            CancellationToken cancellationToken)
        {
            Calls++;
            Plan = plan;
            return Task.FromResult(new ValidationReportPlanningFacts(unbalancedDetailRowCount));
        }
    }

    private sealed class RecordingFieldInfoFactsPort(
        IReadOnlyList<FieldInfo> tb,
        IReadOnlyList<FieldInfo> gl) : IFieldInfoFactsPort
    {
        internal List<(string ProjectId, DatasetKind Kind, FieldInfoScope Scope)> Requests
            { get; } = [];

        public Task<IReadOnlyList<FieldInfo>> ReadAsync(
            string projectId,
            DatasetKind kind,
            FieldInfoScope scope,
            CancellationToken cancellationToken)
        {
            Requests.Add((projectId, kind, scope));
            return Task.FromResult(kind == DatasetKind.Tb ? tb : gl);
        }
    }

    // 正式組裝一定帶入科目分類與欄位配對 store；這裡提供已確認的 GL 欄位配對，讓報表中繼資料可以建立。
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
                    "typed-validation-gl",
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

    private sealed class NoOpInfWriter : IInfReportWriter
    {
        public Task<ExportStats> WriteAsync(
            Stream output,
            InfReportContext context,
            ReportWorkbookMetadata workbookMetadata,
            IReadOnlyList<GlRdeFieldMetadata> customFields,
            CancellationToken cancellationToken,
            Action<WorkingPaperProgress>? progress = null) =>
            Task.FromResult(new ExportStats(0, []));
    }

    private sealed class NoOpAccountMappingWriter : IAccountMappingTemplateWriter
    {
        public Task WriteAsync(
            Stream output,
            IReadOnlyList<AccountMappingTemplateRow> rows,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class OneRowAccountMappingRepository : IAccountMappingExportRepository
    {
        public Task<IReadOnlyList<AccountMappingExportRow>> FetchAllAsync(
            string projectId,
            string periodStart,
            string periodEnd,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AccountMappingTemplateRow>> FetchTemplateRowsAsync(
            string projectId,
            string periodStart,
            string periodEnd,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AccountMappingTemplateRow>>(
                [new AccountMappingTemplateRow("1101", "Cash")]);
    }

    private sealed class FixedRunStore(ResultRecord run) : IResultStore
    {
        internal Func<Exception?>? ReadFailure { get; set; }
        public Task SaveAsync(
            string projectId,
            ResultRecord record,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ResultRecord?> FindLatestAsync(
            string projectId,
            ResultKind kind,
            CancellationToken cancellationToken) =>
            ReadFailure?.Invoke() is { } error ? Task.FromException<ResultRecord?>(error) : Task.FromResult<ResultRecord?>(run);
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
        var catalog = data.GetProperty("reports");
        Assert.Equal(new[] { "report-0", "report-1" }, catalog.EnumerateArray()
            .Select(report => report.GetProperty("reportId").GetString()));
        Assert.Equal(new[] { "infReport", "validationReport" }, catalog.EnumerateArray()
            .Select(report => report.GetProperty("kind").GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(data.GetProperty("publishedReports").GetRawText(), catalog.GetRawText());
    }

    private sealed class ExecutingReportStore(
        string expectedProjectId,
        DateTimeOffset generatedUtc,
        Action? afterPublication = null) : IReportStore, IReportPublishingStore
    {
        internal IReadOnlyList<ReportWriteRequest> Requests { get; private set; } = [];
        internal List<string> CatalogOrder { get; } = [];
        private IReadOnlyList<Report> _published = [];

        internal int WriteCalls { get; private set; }

        internal int WriteBatchCalls { get; private set; }

        public async Task<Report> WriteAsync(
            string projectId,
            ReportWriteRequest request,
            CancellationToken cancellationToken)
        {
            WriteCalls++;
            var reports = await WriteCoreAsync(projectId, [request], cancellationToken);
            return reports[0];
        }

        public async Task<IReadOnlyList<Report>> WriteBatchAsync(
            string projectId,
            IReadOnlyList<ReportWriteRequest> requests,
            CancellationToken cancellationToken)
        {
            WriteBatchCalls++;
            return await WriteCoreAsync(projectId, requests, cancellationToken);
        }

        async Task<Report> IReportPublishingStore.WriteWithPublishingAsync(
            string projectId,
            ReportWriteRequest request,
            Action<ReportKind> publishingReport,
            CancellationToken cancellationToken)
        {
            var report = await WriteAsync(projectId, request, cancellationToken);
            publishingReport(request.Kind);
            CatalogOrder.Add("published");
            afterPublication?.Invoke();
            return report;
        }

        async Task<IReadOnlyList<Report>> IReportPublishingStore.WriteBatchWithPublishingAsync(
            string projectId,
            IReadOnlyList<ReportWriteRequest> requests,
            Action<ReportKind> publishingReport,
            CancellationToken cancellationToken)
        {
            var reports = await WriteBatchAsync(projectId, requests, cancellationToken);
            foreach (var request in requests)
            {
                publishingReport(request.Kind);
            }
            CatalogOrder.Add("published");
            afterPublication?.Invoke();
            return reports;
        }

        private async Task<IReadOnlyList<Report>> WriteCoreAsync(
            string projectId,
            IReadOnlyList<ReportWriteRequest> requests,
            CancellationToken cancellationToken)
        {
            Assert.Equal(expectedProjectId, projectId);
            Requests = requests;
            var reports = new List<Report>(requests.Count);
            for (var index = 0; index < requests.Count; index++)
            {
                var request = requests[index];
                await using var output = new MemoryStream();
                await request.WriteContentAsync(output, cancellationToken);
                reports.Add(new Report(
                    $"report-{index}",
                    request.Kind,
                    $"{ReportKindValues.ToValue(request.Kind)}.xlsx",
                    request.SourceRef,
                    generatedUtc,
                    output.Length,
                    LastWriteUtc: null,
                    Stale: false));
            }

            _published = reports.ToArray();
            CatalogOrder.Add("content-written");
            return _published;
        }

        public Task<IReadOnlyList<Report>> ListAsync(
            string projectId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(expectedProjectId, projectId);
            // 發布後清單有自己的短期限；使用者的晚到取消不得傳入此刷新。
            Assert.True(cancellationToken.CanBeCanceled);
            Assert.False(cancellationToken.IsCancellationRequested);
            Assert.Equal(new[] { "content-written", "published" }, CatalogOrder);
            Assert.Equal(2, _published.Count);
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

        public Task<int> MarkStaleAsync(
            string projectId,
            Func<Report, bool> predicate,
            CancellationToken cancellationToken)
        {
            // 套用新增的來源重判，不省略writer或發布順序的任何原斷言。
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
