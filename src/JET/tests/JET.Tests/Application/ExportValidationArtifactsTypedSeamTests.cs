using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

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
        session.Enter(projectId);
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
        var handler = new ExportValidationArtifactsHandler(
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
            fieldDefinitions);
        using var payload = JsonDocument.Parse($$"""{"runId":"{{runId}}"}""");

        var response = await handler.HandleAsync(payload.RootElement, CancellationToken.None);
        AssertPublishedCatalog(response, artifactStore);

        Assert.Equal(1, validationWriter.PlannedCalls);
        Assert.Equal(0, validationWriter.TypedCalls);
        Assert.Equal(0, validationWriter.PublicCalls);
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
        session.Enter(projectId);
        var validationWriter = new RecordingValidationWriter();
        using var cancellation = new CancellationTokenSource();
        var artifactStore = new ExecutingArtifactStore(projectId, generatedUtc, cancellation.Cancel);
        var handler = new ExportValidationArtifactsHandler(
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
            new NullEventPublisher());
        using var payload = JsonDocument.Parse($$"""{"runId":"{{runId}}"}""");

        var response = await handler.HandleAsync(payload.RootElement, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        AssertPublishedCatalog(response, artifactStore);

        Assert.Equal(1, validationWriter.TypedCalls);
        Assert.Equal(0, validationWriter.PublicCalls);
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
            SourceQualityFindingCount: 9L,
            SourceQualitySampleRows:
            [
                new SourceQualityFindingRow(
                    "nullPostDate", 12, "source.xlsx [GL]", "JV-1", "1000", null, "blank", 0)
            ]);
        var actualProjection = Assert.IsType<ValidationReportProjection>(validationWriter.Projection);
        Assert.Equal(
            expectedProjection with { SourceQualitySampleRows = actualProjection.SourceQualitySampleRows },
            actualProjection);
        Assert.Equal(
            expectedProjection.SourceQualitySampleRows.ToArray(),
            actualProjection.SourceQualitySampleRows.ToArray());
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
    public async Task PublicOnlyWriter_DoesNotRequireTypedProjectionSummaryShape()
    {
        const string projectId = "public-validation-export";
        const string runId = "validation-run";
        var generatedUtc = new DateTimeOffset(2025, 12, 31, 10, 30, 0, TimeSpan.Zero);
        var summaryJson = JsonSerializer.Serialize(
            new
            {
                opaquePublicWriterData = new { value = "preserved" },
                resultRef = new { logicVersion = RuleLogicVersions.Validation }
            },
            JetJsonStorage.Options);
        var document = Project(projectId);
        var session = new ProjectSession();
        session.Enter(projectId);
        var validationWriter = new RecordingPublicValidationWriter();
        var artifactStore = new ExecutingArtifactStore(projectId, generatedUtc);
        var planningFacts = new RecordingPlanningFactsPort(unbalancedDetailRowCount: 1L);
        var fieldDefinitions = new RecordingFieldDefinitionFactsPort([], []);
        var handler = new ExportValidationArtifactsHandler(
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
            fieldDefinitions);
        using var payload = JsonDocument.Parse($$"""{"runId":"{{runId}}"}""");

        var response = await handler.HandleAsync(payload.RootElement, CancellationToken.None);
        AssertPublishedCatalog(response, artifactStore);

        Assert.Equal(1, validationWriter.Calls);
        Assert.Equal(summaryJson, validationWriter.Context?.SummaryJson);
        Assert.Equal(runId, validationWriter.Context?.RunId);
        Assert.Equal(0, planningFacts.Calls);
        Assert.Empty(fieldDefinitions.Requests);
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

    private sealed class RecordingPlannedValidationWriter :
        IValidationReportWriter,
        ITypedValidationReportWriter,
        IPlannedValidationReportWriter
    {
        internal int PublicCalls { get; private set; }

        internal int TypedCalls { get; private set; }

        internal int PlannedCalls { get; private set; }

        internal ValidationReportPlan? Plan { get; private set; }

        internal FieldInfoProjection? FieldInfo { get; private set; }

        internal ValidationReportProjection? Projection { get; private set; }

        internal string? OperatorId { get; private set; }

        public Task<ExportStats> WriteAsync(
            Stream output,
            ValidationReportContext context,
            CancellationToken cancellationToken,
            Action<WorkpaperProgress>? progress = null)
        {
            PublicCalls++;
            throw new InvalidOperationException("Production export must use the planned seam.");
        }

        public Task<ExportStats> WriteTypedAsync(
            Stream output,
            ValidationReportContext context,
            ValidationReportProjection projection,
            string operatorId,
            CancellationToken cancellationToken,
            Action<WorkpaperProgress>? progress = null)
        {
            TypedCalls++;
            throw new InvalidOperationException("Production export must prefer the planned seam.");
        }

        public Task<ExportStats> WritePlannedAsync(
            Stream output,
            ValidationReportContext context,
            ValidationReportProjection projection,
            ValidationReportPlan plan,
            FieldInfoProjection fieldInfo,
            string operatorId,
            CancellationToken cancellationToken,
            Action<WorkpaperProgress>? progress = null)
        {
            PlannedCalls++;
            Plan = plan;
            FieldInfo = fieldInfo;
            Projection = projection;
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

    private sealed class RecordingValidationWriter :
        IValidationReportWriter,
        ITypedValidationReportWriter
    {
        internal int PublicCalls { get; private set; }

        internal int TypedCalls { get; private set; }

        internal ValidationReportContext? Context { get; private set; }

        internal ValidationReportProjection? Projection { get; private set; }

        internal string? OperatorId { get; private set; }

        public Task<ExportStats> WriteAsync(
            Stream output,
            ValidationReportContext context,
            CancellationToken cancellationToken,
            Action<WorkpaperProgress>? progress = null)
        {
            PublicCalls++;
            throw new InvalidOperationException(
                "Production export must use the typed validation writer seam.");
        }

        public Task<ExportStats> WriteTypedAsync(
            Stream output,
            ValidationReportContext context,
            ValidationReportProjection projection,
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
    }

    private sealed class RecordingPublicValidationWriter : IValidationReportWriter
    {
        internal int Calls { get; private set; }

        internal ValidationReportContext? Context { get; private set; }

        public Task<ExportStats> WriteAsync(
            Stream output,
            ValidationReportContext context,
            CancellationToken cancellationToken,
            Action<WorkpaperProgress>? progress = null)
        {
            Calls++;
            Context = context;
            return Task.FromResult(new ExportStats(0, []));
        }
    }

    private sealed class NoOpInfWriter : IInfReportWriter
    {
        public Task<ExportStats> WriteAsync(
            Stream output,
            InfReportContext context,
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
            Assert.Equal(CancellationToken.None, cancellationToken);
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
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IAsyncDisposable> AcquireProjectDeletionLeaseAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
