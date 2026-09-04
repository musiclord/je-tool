using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 正式 WorkingPaper：固定完整工作表，只接受 validation／filter reference 與所選情境位置；
/// 產物由 project-local artifact store 原子發布，caller 不得指定工作表或路徑。
/// </summary>
public sealed class ExportWorkpaperStreamHandler : IApplicationActionHandler
{
    private readonly IWorkpaperPlanWriter planWriter;
    private readonly IWorkpaperPlanningFactsPort planningFactsPort;
    private readonly IFilterScenarioStore scenarioStore;
    private readonly FilterRunMaterializeService materializeService;
    private readonly IRuleRunStore runStore;
    private readonly IResultStaleStateStore resultStaleStateStore;
    private readonly IProjectStore projectStore;
    private readonly IReportArtifactStore artifactStore;
    private readonly ProjectSession session;
    private readonly IJetEventPublisher eventPublisher;
    private readonly IMappingStateStore mappingStore;
    private readonly IAccountTaxonomyStore accountTaxonomyStore;

    internal ExportWorkpaperStreamHandler(
        IWorkpaperPlanWriter planWriter,
        IWorkpaperPlanningFactsPort planningFactsPort,
        IFilterScenarioStore scenarioStore,
        FilterRunMaterializeService materializeService,
        IRuleRunStore runStore,
        IResultStaleStateStore resultStaleStateStore,
        IProjectStore projectStore,
        IReportArtifactStore artifactStore,
        ProjectSession session,
        IJetEventPublisher eventPublisher,
        IMappingStateStore mappingStore,
        IAccountTaxonomyStore accountTaxonomyStore)
    {
        this.planWriter = planWriter;
        this.planningFactsPort = planningFactsPort;
        this.scenarioStore = scenarioStore;
        this.materializeService = materializeService;
        this.runStore = runStore;
        this.resultStaleStateStore = resultStaleStateStore;
        this.projectStore = projectStore;
        this.artifactStore = artifactStore;
        this.session = session;
        this.eventPublisher = eventPublisher;
        this.mappingStore = mappingStore ?? throw new ArgumentNullException(nameof(mappingStore));
        this.accountTaxonomyStore = accountTaxonomyStore
            ?? throw new ArgumentNullException(nameof(accountTaxonomyStore));
    }

    public string Action => "export.workpaperStream";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
                throw new JetActionException(JetErrorCodes.InvalidPayload, "export.workpaperStream 的 payload 必須是物件。");
        }

        if (payload.TryGetProperty("sheets", out _) || payload.TryGetProperty("outputPath", out _))
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "正式底稿不接受 'sheets' 或 'outputPath'；工作表固定且產物只能位於專案目錄。");
        }

        var validationRunId = PayloadReader.GetOptionalString(payload, "validationRunId")
            ?? throw new JetActionException(JetErrorCodes.InvalidPayload, "payload 缺少 'validationRunId'。");
        var scenarioRevision = PayloadReader.GetOptionalString(payload, "scenarioRevision")
            ?? throw new JetActionException(JetErrorCodes.InvalidPayload, "payload 缺少 'scenarioRevision'。");
        var projectId = session.RequireProjectId();
        var validationRun = await CompletenessEligibilitySupport.RequireCurrentAsync(
            runStore,
            projectId,
            cancellationToken);
        ReportExportSupport.RequireRequestedValidationRun(validationRun, validationRunId);
        var progressSession = new ExportProgressSession(eventPublisher, cancellationToken);
        var artifactProgress = progressSession.Start(ReportArtifactKind.WorkingPaper);

        var latestPrescreen = await runStore.FindLatestAsync(
            projectId,
            RuleRunKinds.Prescreen,
            cancellationToken);
        latestPrescreen = RuleLogicVersions.IsCurrent(latestPrescreen) ? latestPrescreen : null;
        var staleState = await resultStaleStateStore.ReadAsync(projectId, cancellationToken);
        var scenarios = await scenarioStore.ListAsync(projectId, cancellationToken);
        var filterRevision = ReportExportSupport.RequireRevisionState(scenarios, scenarioRevision);
        scenarioRevision = filterRevision.Revision;
        var selectedPositions = ReportExportSupport.ReadScenarioPositions(payload, scenarios);
        var allScenarioPositions = scenarios
            .Select(scenario => scenario.Position)
            .Order()
            .ToArray();
        var refreshedArtifacts = await ReportExportSupport.RefreshArtifactsAsync(
            projectId,
            validationRun,
            latestPrescreen,
            staleState.Filter,
            scenarioRevision,
            allScenarioPositions,
            artifactStore,
            cancellationToken);
        await ReportExportSupport.RequireCurrentCriteriaSelectionReportAsync(
            artifactStore,
            projectId,
            refreshedArtifacts,
            cancellationToken);

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        // materializer 的 replace-all 形狀要求全部已存情境；writer 再依 selectedPositions 投影欄位。
        await materializeService.MaterializeAllAsync(projectId, document, scenarios, cancellationToken);

        var selected = selectedPositions.ToHashSet();
        var selectedScenarios = scenarios
            .Where(item => selected.Contains(item.Position))
            .ToArray();
        var scenarioSelections = new List<WorkpaperScenarioSelection>(selectedPositions.Count);
        foreach (var scenario in selectedScenarios)
        {
            using var definition = JsonDocument.Parse(scenario.DefinitionJson);
            var scenarioSpec = FilterScenarioPayloadParser.Parse(
                definition.RootElement,
                document.MoneyScale);
            scenarioSelections.Add(new WorkpaperScenarioSelection(
                scenario.Position,
                scenario.Name,
                scenario.Rationale,
                JetAuditProgram.ResolveWorkpaperTagScope(scenarioSpec)));
        }

        var context = new WorkpaperContext(
            ProjectId: projectId,
            CompanyName: document.EntityName,
            PeriodStart: document.PeriodStart,
            PeriodEnd: document.PeriodEnd,
            LastPeriodStart: document.LastAccountingPeriodDate,
            MoneyScale: document.MoneyScale,
            ValidationRunId: validationRun.RunId,
            ScenarioRevision: scenarioRevision,
            ScenarioPositions: selectedPositions,
            PopulationScope: filterRevision.PopulationScope);

        var workbookMetadata = await ReportWorkbookMetadataFactory.LoadAsync(
            projectId,
            document,
            mappingStore,
            accountTaxonomyStore,
            cancellationToken);
        var customFields = FormalReportRdeFieldSelection.ForScenarios(
            selectedScenarios,
            workbookMetadata,
            document.MoneyScale);

        var plan = JetAuditProgram.Plan(new WorkpaperRequest(
            ProjectId: projectId,
            PeriodStart: document.PeriodStart,
            PeriodEnd: document.PeriodEnd,
            LastPeriodStart: document.LastAccountingPeriodDate,
            MoneyScale: document.MoneyScale,
            ValidationRunId: validationRun.RunId,
            ScenarioRevision: scenarioRevision,
            Scenarios: scenarioSelections,
            PopulationScope: filterRevision.PopulationScope,
            WorkbookMetadata: workbookMetadata,
            CustomFields: customFields));
        var facts = await JetAuditProgram.ExecuteAsync(
            plan,
            planningFactsPort,
            cancellationToken);
        var finalizedPlan = JetAuditProgram.Finalize(plan, facts);

        ExportStats? exportStats = null;
        var request = new ReportArtifactWriteRequest(
                ReportArtifactKind.WorkingPaper,
                new ReportArtifactSourceRefs(
                    ValidationRunId: validationRun.RunId,
                    ScenarioRevision: scenarioRevision,
                    ScenarioPositions: selectedPositions),
                async (stream, ct) =>
                {
                    exportStats = await planWriter.WriteAsync(
                        stream,
                        context,
                        finalizedPlan,
                        ct,
                        artifactProgress.WriterProgress);
                    artifactProgress.FinalizingWorkbook();
                });
        var artifact = artifactStore is IReportArtifactPublishingStore publishingStore
            ? await publishingStore.WriteWithPublishingAsync(
                projectId,
                request,
                _ => artifactProgress.PublishingArtifact(),
                cancellationToken)
            : await artifactStore.WriteAsync(projectId, request, cancellationToken);

        return new
        {
            ok = true,
            artifact = ReportExportSupport.ArtifactWire(artifact),
            sheetStats = (exportStats?.SheetStats ?? Array.Empty<SheetStat>())
                .Select(item => (object)new { sheetName = item.SheetName, rowsWritten = item.RowsWritten })
                .ToArray()
        };
    }
}
