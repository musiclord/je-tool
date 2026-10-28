using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

// 2026-10-22 技術債一輪：驗證報告寫出器只剩一個正式入口，handler 一定先規劃、投影欄位資訊並載入工作簿中繼資料，
// 原本依 writer 型別退回的 typed 與 public 路徑都刪除，對應的替身與「公開 writer 不必解析摘要」的測試一併移除。
// 第一次失敗是刪掉入口後測試專案建置失敗，那次沒有保存收據。
public sealed class ExportValidationArtifactsTypedSeamTests
{
    [Fact]
    public async Task ProductionConstructor_FinalizesPlanAndProjectsTargetFieldInfoForPlannedWriter()
    {
        const string projectId = "planned-validation-export";
        const string runId = "validation-run";
        var generatedUtc = new DateTimeOffset(2025, 12, 31, 10, 30, 0, TimeSpan.Zero);
        var summaryJson = CurrentValidationSummaryTestData.Create(
            runId,
            generatedUtc,
            completenessDifferenceCount: 3L,
            unbalancedDocumentCount: 4L,
            nullAccountCount: 10_001L,
            nullDocumentCount: 10_000L,
            nullDescriptionCount: 0L,
            outOfRangeDateCount: 8L,
            sourceQualityFindingCount: 10_001L);
        var document = Project(projectId);
        var session = new ProjectSession();
        var validationWriter = new RecordingPlannedValidationWriter();
        var artifactStore = new ExecutingArtifactStore(projectId, generatedUtc);
        var planningFacts = new RecordingPlanningFactsPort(unbalancedDetailRowCount: 9_999L);
        var fieldDefinitions = new RecordingFieldDefinitionFactsPort(
            tb:
            [
                new LegacyFieldDefinition(2, "期末餘額", null, LegacyFieldKind.Number, null, 0),
                new LegacyFieldDefinition(1, "會計科目編號_TB", "客戶科目", LegacyFieldKind.Text, 20, null)
            ],
            gl:
            [
                new LegacyFieldDefinition(2, "傳票金額_JE", "來源金額", LegacyFieldKind.Number, null, 4),
                new LegacyFieldDefinition(1, "來源日期", null, LegacyFieldKind.Date, null, null)
            ]);
        var handler = CreateHandler(
            projectId,
            validationWriter,
            new NoOpInfWriter(),
            new FixedRunStore(new RuleRunRecord(
                runId,
                RuleRunKinds.Validate,
                generatedUtc,
                summaryJson)),
            new FixedProjectStore(document),
            artifactStore,
            session,
            new NullEventPublisher(),
            planningFacts,
            fieldDefinitions,
            new BuiltInAccountTaxonomyStore(),
            new FixedMetadataMappingStore());
        using var payload = JsonDocument.Parse($$"""{"runId":"{{runId}}"}""");

        var response = await handler.HandleAsync(payload.RootElement, CancellationToken.None);
        AssertPublishedCatalog(response, artifactStore);

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
            finalizedPlan.RequireDetail(ValidationReportDetailKind.NullAccount).Disposition);
        Assert.Equal(
            ValidationReportDetailDisposition.Emit,
            finalizedPlan.RequireDetail(ValidationReportDetailKind.NullDocument).Disposition);
        Assert.Equal(
            ValidationReportDetailDisposition.Omit,
            finalizedPlan.RequireDetail(ValidationReportDetailKind.NullDescription).Disposition);
        Assert.Equal(
            ValidationReportDetailDisposition.Emit,
            finalizedPlan.RequireDetail(ValidationReportDetailKind.UnbalancedGlEntry).Disposition);
        Assert.Equal(
            9_999L,
            finalizedPlan.RequireDetail(ValidationReportDetailKind.UnbalancedGlEntry).Count);
        Assert.Equal(6, finalizedPlan.Details.Count);
        Assert.True(finalizedPlan.EmitCompletenessExplanation);

        Assert.Equal(10_001L, validationWriter.Projection?.NullAccountCount);
        Assert.Equal(4L, validationWriter.Projection?.UnbalancedDocumentCount);
        var fieldInfo = Assert.IsType<FieldInfoProjection>(validationWriter.FieldInfo);
        Assert.Equal(
            new[]
            {
                new FieldInfoRow("客戶科目", "文字型態", 20, null, "會計科目編號_TB"),
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
                (DatasetKind.Tb, LegacyFieldDefinitionScope.Target),
                (DatasetKind.Gl, LegacyFieldDefinitionScope.Target)
            },
            fieldDefinitions.Requests.Select(request => (request.Kind, request.Scope)));
        Assert.All(fieldDefinitions.Requests, request => Assert.Equal(projectId, request.ProjectId));
        Assert.Equal("tester", validationWriter.OperatorId);
    }

    [Fact]
    public async Task CurrentValidationRun_UsesTypedWriterWithProjectionParsedFromSavedSummary()
    {
        const string projectId = "typed-validation-export";
        const string runId = "validation-run";
        var generatedUtc = new DateTimeOffset(2025, 12, 31, 10, 30, 0, TimeSpan.Zero);
        var summaryJson = CurrentValidationSummaryTestData.Create(
            runId,
            generatedUtc,
            completenessDifferenceCount: 3L,
            unbalancedDocumentCount: 4L,
            nullAccountCount: 5L,
            nullDocumentCount: 6L,
            nullDescriptionCount: 7L,
            outOfRangeDateCount: 8L,
            sourceQualityFindingCount: 9L,
            sourceQualitySampleRows:
            [
                new SourceQualityFindingRow(
                    "nullPostDate",
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
        var artifactStore = new ExecutingArtifactStore(projectId, generatedUtc, cancellation.Cancel);
        var handler = CreateHandler(
            projectId,
            validationWriter,
            new NoOpInfWriter(),
            new FixedRunStore(new RuleRunRecord(
                runId,
                RuleRunKinds.Validate,
                generatedUtc,
                summaryJson)),
            new FixedProjectStore(document),
            artifactStore,
            session,
            new NullEventPublisher(),
            new RecordingPlanningFactsPort(unbalancedDetailRowCount: 4L),
            new RecordingFieldDefinitionFactsPort([], []),
            new BuiltInAccountTaxonomyStore(),
            new FixedMetadataMappingStore());
        using var payload = JsonDocument.Parse($$"""{"runId":"{{runId}}"}""");

        var response = await handler.HandleAsync(payload.RootElement, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        AssertPublishedCatalog(response, artifactStore);

        Assert.Equal(1, validationWriter.Calls);
        var expectedProjection = new ValidationReportProjection(
            Net: 1.25m,
            TotalDebit: 10.50m,
            TotalCredit: 9.25m,
            GlRowCount: 17L,
            CompletenessDiffAccountCount: 3L,
            UnbalancedDocumentCount: 4L,
            NullAccountCount: 5L,
            NullDocumentCount: 6L,
            NullDescriptionCount: 7L,
            OutOfRangeDateCount: 8L,
            SourceQualityFindingCount: 9L);
        // 2026-10-22 使用者裁定刪掉投影裡沒人讀的來源品質樣本列：投影只留計數，摘要 JSON 的 sampleRows 照舊留給前端，
        // 上面仍把樣本列寫進摘要，確認解析器會略過它。第一次失敗：Focused 收據 20261022-084228771-7b2316c36ab54315bf1eb067bdad7939（建置失敗）。
        var actualProjection = Assert.IsType<ValidationReportProjection>(validationWriter.Projection);
        Assert.Equal(expectedProjection, actualProjection);
        Assert.Equal(summaryJson, validationWriter.Context?.SummaryJson);
        Assert.Equal(runId, validationWriter.Context?.RunId);
        Assert.Equal("tester", validationWriter.OperatorId);
        Assert.Equal(0, artifactStore.WriteCalls);
        Assert.Equal(1, artifactStore.WriteBatchCalls);
        Assert.Equal(
            new[]
            {
                ReportArtifactKind.ValidationReport,
                ReportArtifactKind.InfReport
            },
            artifactStore.Requests.Select(request => request.Kind).ToArray());
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
                    runId = "malformed-validation-run",
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
    // session 與事件。2026-10-22 技術債一輪：退回路徑已刪除，規劃、欄位定義、科目分類與欄位配對的替身都必須提供。
    private static ExportValidationArtifactsHandler CreateHandler(
        string projectId,
        IValidationReportWriter validationWriter,
        IInfReportWriter infWriter,
        IRuleRunStore runStore,
        IProjectStore projectStore,
        IReportArtifactStore artifactStore,
        ProjectSession session,
        IJetEventPublisher eventPublisher,
        IValidationReportPlanningFactsPort validationReportPlanningFactsPort,
        ILegacyFieldDefinitionFactsPort fieldDefinitionFactsPort,
        IAccountTaxonomyStore accountTaxonomyStore,
        IMappingStateStore mappingStateStore)
    {
        session.Enter(
            projectId,
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with
            {
                ValidationReportWriter = validationWriter,
                InfReportWriter = infWriter,
                RuleRuns = runStore,
                ReportArtifactStore = artifactStore,
                // 第9批高3；Public首敗100911120的null是Unconfigured省略新必需依賴，非可恢復IO；原斷言保留。
                ResultStaleStates = EmptyReportStateTestData.StaleStates,
                FilterScenarios = EmptyReportStateTestData.Scenarios,
                ValidationReportPlanningFacts = validationReportPlanningFactsPort,
                FieldDefinitionFacts = fieldDefinitionFactsPort,
                AccountTaxonomy = accountTaxonomyStore,
                MappingStates = mappingStateStore,
            });
        return new ExportValidationArtifactsHandler(projectStore, session, eventPublisher);
    }

    private static ProjectDocument Project(string projectId) => new(
        ProjectId: projectId,
        ProjectCode: "TYPED-EXPORT",
        EntityName: "Typed Export Entity",
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
            Action<WorkpaperProgress>? progress = null)
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

    private sealed class RecordingFieldDefinitionFactsPort(
        IReadOnlyList<LegacyFieldDefinition> tb,
        IReadOnlyList<LegacyFieldDefinition> gl) : ILegacyFieldDefinitionFactsPort
    {
        internal List<(string ProjectId, DatasetKind Kind, LegacyFieldDefinitionScope Scope)> Requests
            { get; } = [];

        public Task<IReadOnlyList<LegacyFieldDefinition>> ReadAsync(
            string projectId,
            DatasetKind kind,
            LegacyFieldDefinitionScope scope,
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

    private sealed class NoOpInfWriter : IInfReportWriter
    {
        public Task<ExportStats> WriteAsync(
            Stream output,
            InfReportContext context,
            ReportWorkbookMetadata workbookMetadata,
            IReadOnlyList<GlRdeFieldMetadata> customFields,
            CancellationToken cancellationToken,
            Action<WorkpaperProgress>? progress = null) =>
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

    private sealed class FixedRunStore(RuleRunRecord run) : IRuleRunStore
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
            Task.FromResult<RuleRunRecord?>(run);
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
        var catalog = data.GetProperty("reportArtifacts");
        Assert.Equal(new[] { "artifact-0", "artifact-1" }, catalog.EnumerateArray()
            .Select(artifact => artifact.GetProperty("artifactId").GetString()));
        Assert.Equal(new[] { "infReport", "validationReport" }, catalog.EnumerateArray()
            .Select(artifact => artifact.GetProperty("kind").GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(data.GetProperty("artifacts").GetRawText(), catalog.GetRawText());
    }

    private sealed class ExecutingArtifactStore(
        string expectedProjectId,
        DateTimeOffset generatedUtc,
        Action? afterPublication = null) : IReportArtifactStore, IReportArtifactPublishingStore
    {
        internal IReadOnlyList<ReportArtifactWriteRequest> Requests { get; private set; } = [];
        internal List<string> CatalogOrder { get; } = [];
        private IReadOnlyList<ReportArtifact> _published = [];

        internal int WriteCalls { get; private set; }

        internal int WriteBatchCalls { get; private set; }

        public async Task<ReportArtifact> WriteAsync(
            string projectId,
            ReportArtifactWriteRequest request,
            CancellationToken cancellationToken)
        {
            WriteCalls++;
            var artifacts = await WriteCoreAsync(projectId, [request], cancellationToken);
            return artifacts[0];
        }

        public async Task<IReadOnlyList<ReportArtifact>> WriteBatchAsync(
            string projectId,
            IReadOnlyList<ReportArtifactWriteRequest> requests,
            CancellationToken cancellationToken)
        {
            WriteBatchCalls++;
            return await WriteCoreAsync(projectId, requests, cancellationToken);
        }

        async Task<ReportArtifact> IReportArtifactPublishingStore.WriteWithPublishingAsync(
            string projectId,
            ReportArtifactWriteRequest request,
            Action<ReportArtifactKind> publishingArtifact,
            CancellationToken cancellationToken)
        {
            var artifact = await WriteAsync(projectId, request, cancellationToken);
            publishingArtifact(request.Kind);
            CatalogOrder.Add("published");
            afterPublication?.Invoke();
            return artifact;
        }

        async Task<IReadOnlyList<ReportArtifact>> IReportArtifactPublishingStore.WriteBatchWithPublishingAsync(
            string projectId,
            IReadOnlyList<ReportArtifactWriteRequest> requests,
            Action<ReportArtifactKind> publishingArtifact,
            CancellationToken cancellationToken)
        {
            var artifacts = await WriteBatchAsync(projectId, requests, cancellationToken);
            foreach (var request in requests)
            {
                publishingArtifact(request.Kind);
            }
            CatalogOrder.Add("published");
            afterPublication?.Invoke();
            return artifacts;
        }

        private async Task<IReadOnlyList<ReportArtifact>> WriteCoreAsync(
            string projectId,
            IReadOnlyList<ReportArtifactWriteRequest> requests,
            CancellationToken cancellationToken)
        {
            Assert.Equal(expectedProjectId, projectId);
            Requests = requests;
            var artifacts = new List<ReportArtifact>(requests.Count);
            for (var index = 0; index < requests.Count; index++)
            {
                var request = requests[index];
                await using var output = new MemoryStream();
                await request.WriteContentAsync(output, cancellationToken);
                artifacts.Add(new ReportArtifact(
                    $"artifact-{index}",
                    request.Kind,
                    $"{ReportArtifactKindValues.ToValue(request.Kind)}.xlsx",
                    request.SourceRef,
                    generatedUtc,
                    output.Length,
                    LastWriteUtc: null,
                    Stale: false));
            }

            _published = artifacts.ToArray();
            CatalogOrder.Add("content-written");
            return _published;
        }

        public Task<IReadOnlyList<ReportArtifact>> ListAsync(
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

        public Task<int> MarkStaleAsync(
            string projectId,
            Func<ReportArtifact, bool> predicate,
            CancellationToken cancellationToken)
        {
            // 第9批高3：套用新增的來源重判，不省略writer或發布順序的任何原斷言。
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
