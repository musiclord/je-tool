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
    private readonly IProjectStore projectStore;
    private readonly ProjectSession session;
    private readonly IJetEventPublisher eventPublisher;

    internal ExportWorkpaperStreamHandler(
        IProjectStore projectStore,
        ProjectSession session,
        IJetEventPublisher eventPublisher)
    {
        this.projectStore = projectStore;
        this.session = session;
        this.eventPublisher = eventPublisher;
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
        var (projectId, repositories) = session.RequireActive();
        var planWriter = repositories.WorkpaperPlanWriter;
        var planningFactsPort = repositories.WorkpaperPlanningFacts;
        var scenarioStore = repositories.FilterScenarios;
        var materializeService = repositories.FilterRunMaterializeService;
        var runStore = repositories.RuleRuns;
        var resultStaleStateStore = repositories.ResultStaleStates;
        var artifactStore = repositories.ReportArtifactStore;
        var mappingStore = repositories.MappingStates;
        var accountTaxonomyStore = repositories.AccountTaxonomy;
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
        var filterDataRevision = await resultStaleStateStore.ReadFilterDataRevisionAsync(projectId, cancellationToken);
        var scenarios = await scenarioStore.ListAsync(projectId, cancellationToken);
        var filterRevision = ReportExportSupport.RequireRevisionState(scenarios, scenarioRevision);
        scenarioRevision = filterRevision.Revision;
        var selectedPositions = ReportExportSupport.ReadScenarioPositions(payload, scenarios);
        var allScenarioPositions = scenarios
            .Select(scenario => scenario.Position)
            .Order()
            .ToArray();
        await ReportExportSupport.RefreshArtifactsAsync(
            projectId,
            validationRun,
            latestPrescreen,
            staleState.Filter,
            scenarioRevision,
            allScenarioPositions,
            artifactStore,
            cancellationToken,
            filterDataRevision);
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        // 本次只重算使用者選定的情境；未選情境仍保存，且不因此取得「全案已更新」狀態。
        await materializeService.MaterializeSelectedAsync(projectId, document, scenarios, selectedPositions, cancellationToken);
        var filterResultsCurrent = !(await resultStaleStateStore.ReadAsync(projectId, cancellationToken)).Filter;

        var selected = selectedPositions.ToHashSet();
        var selectedScenarios = scenarios
            .Where(item => selected.Contains(item.Position))
            .ToArray();
        var scenarioSelections = new List<WorkpaperScenarioSelection>(selectedPositions.Count);
        IReadOnlyDictionary<string, string>? conditionCategoryLabels = null;
        IReadOnlyDictionary<string, string>? conditionFieldLabels = null;
        IReadOnlyList<AccountTaxonomyCategory>? conditionCategories = null;
        foreach (var scenario in selectedScenarios)
        {
            using var definition = JsonDocument.Parse(scenario.DefinitionJson);
            var scenarioSpec = FilterScenarioPayloadParser.Parse(
                definition.RootElement,
                document.MoneyScale);
            if (conditionCategoryLabels is null)
            {
                var taxonomy = await accountTaxonomyStore.ReadAsync(projectId, cancellationToken);
                conditionCategories = taxonomy.Categories;
                conditionCategoryLabels = taxonomy.Categories.ToDictionary(category => category.CategoryId, category => category.Label);
                var mapping = await mappingStore.FindAsync(projectId, DatasetKind.Gl, cancellationToken);
                conditionFieldLabels = (mapping?.GlOptions?.RdeFields ?? []).ToDictionary(field => field.FieldId, field => field.Label);
            }
            var conditionLogic = FilterConditionRenderer.Render(definition.RootElement, conditionCategoryLabels, conditionFieldLabels, conditionCategories, document.LastAccountingPeriodDate);
            scenarioSelections.Add(new WorkpaperScenarioSelection(
                scenario.Position,
                scenario.Name,
                scenario.Rationale,
                JetAuditProgram.ResolveWorkpaperTagScope(scenarioSpec),
                conditionLogic));
        }

        var context = new WorkpaperContext(
            ProjectId: projectId,
            CompanyName: ReportExportSupport.CompanyName(document),
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
        var facts = await planningFactsPort.ExecuteAsync(
            plan,
            cancellationToken);
        var finalizedPlan = JetAuditProgram.Finalize(plan, facts);

        ExportStats? exportStats = null;
        var request = new ReportArtifactWriteRequest(
                ReportArtifactKind.WorkingPaper,
                new ReportArtifactSourceRefs(
                    ValidationRunId: validationRun.RunId,
                    ScenarioRevision: scenarioRevision,
                    ScenarioPositions: selectedPositions,
                    FilterDataRevision: filterDataRevision),
                async (stream, ct) =>
                {
                    exportStats = await planWriter.WriteAsync(
                        stream,
                        context,
                        finalizedPlan,
                        ct,
                        artifactProgress.WriterProgress);
                    artifactProgress.FinalizingWorkbook();
                }, document.PeriodStart, document.PeriodEnd);
        var artifact = artifactStore is IReportArtifactPublishingStore publishingStore
            ? await publishingStore.WriteWithPublishingAsync(
                projectId,
                request,
                _ => artifactProgress.PublishingArtifact(),
                cancellationToken)
            : await artifactStore.WriteAsync(projectId, request, cancellationToken);

        var catalog = await WorkflowResultStateSupport.AfterPublicationAsync(projectId, repositories.RuleRuns, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportArtifactStore);
        return new
        {
            ok = true,
            filterResultsCurrent,
            artifact = ReportExportSupport.ArtifactWire(artifact),
            reportArtifacts = catalog.Artifacts,
            reportArtifactWarning = catalog.Warning,
            sheetStats = (exportStats?.SheetStats ?? Array.Empty<SheetStat>())
                .Select(item => (object)new { sheetName = item.SheetName, rowsWritten = item.RowsWritten })
                .ToArray()
        };
    }
}
