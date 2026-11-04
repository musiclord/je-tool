using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 正式 WorkingPaper：固定完整工作表，只接受 validation／filter reference 與所選情境位置；
/// 產物由案件資料夾內的報告 store 原子發布，caller 不得指定工作表或路徑。
/// </summary>
public sealed class ExportWorkingPaperStreamHandler : IApplicationActionHandler
{
    private readonly IProjectStore projectStore;
    private readonly ProjectSession session;
    private readonly IJetEventPublisher eventPublisher;

    internal ExportWorkingPaperStreamHandler(
        IProjectStore projectStore,
        ProjectSession session,
        IJetEventPublisher eventPublisher)
    {
        this.projectStore = projectStore;
        this.session = session;
        this.eventPublisher = eventPublisher;
    }

    public string Action => "export.workingPaperStream";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "working_paper_payload_not_object");
        }

        if (payload.TryGetProperty("sheets", out _) || payload.TryGetProperty("outputPath", out _))
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "working_paper_sheets_or_output_path");
        }

        var validationResultId = PayloadReader.GetOptionalString(payload, "validationResultId")
            ?? throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "validation_result_id_missing");
        // 還沒有儲存篩選情境時，畫面送 null 或不送篩選版本。
        var scenarioRevision = PayloadReader.GetOptionalString(payload, "scenarioRevision");
        var (projectId, repositories) = session.RequireActive();
        var planWriter = repositories.WorkingPaperPlanWriter;
        var planningFactsPort = repositories.WorkingPaperPlanningFacts;
        var scenarioStore = repositories.FilterScenarios;
        var materializeService = repositories.FilterHitMaterializeService;
        var resultStore = repositories.Results;
        var resultStaleStateStore = repositories.ResultStaleStates;
        var reportStore = repositories.ReportStore;
        var mappingStore = repositories.MappingStates;
        var accountCategoryStore = repositories.AccountCategories;
        SupportOperationStage.Enter("run_read");
        var validationResult = await CompletenessEligibilitySupport.RequireCurrentAsync(
            resultStore,
            projectId,
            cancellationToken);
        ReportExportSupport.RequireRequestedValidationResult(validationResult, validationResultId);
        var progressSession = new ExportProgressSession(eventPublisher, cancellationToken);
        var reportProgress = progressSession.Start(ReportKind.WorkingPaper);

        SupportOperationStage.Enter("scenario_read");
        var latestPrescreen = await resultStore.FindLatestAsync(
            projectId,
            ResultKind.Prescreen,
            cancellationToken);
        latestPrescreen = RuleLogicVersions.IsCurrent(latestPrescreen) ? latestPrescreen : null;
        var staleState = await resultStaleStateStore.ReadAsync(projectId, cancellationToken);
        var filterDataRevision = await resultStaleStateStore.ReadFilterDataRevisionAsync(projectId, cancellationToken);
        var scenarios = await scenarioStore.ListAsync(projectId, cancellationToken);
        var filterRevision = ReportExportSupport.RequireWorkingPaperRevisionState(scenarios, scenarioRevision);
        scenarioRevision = filterRevision.Revision;
        var selectedPositions = ReportExportSupport.ReadScenarioPositions(payload, scenarios);
        var allScenarioPositions = scenarios
            .Select(scenario => scenario.Position)
            .Order()
            .ToArray();
        await ReportExportSupport.RefreshReportsAsync(
            projectId,
            validationResult,
            latestPrescreen,
            staleState.Filter,
            scenarioRevision,
            allScenarioPositions,
            reportStore,
            cancellationToken,
            filterDataRevision);
        SupportOperationStage.Enter("metadata_read");
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);

        // 儲存情境時已經存下每個情境的結果，底稿直接用存好的結果，不重算；只照重算前的同一套檢查確認選定的情境仍能套用。
        // 結果標成過期時，才只重算選定的情境；未選情境仍保存，且不因此取得「全案已更新」狀態。
        SupportOperationStage.Enter("filter_materialize");
        if (selectedPositions.Count > 0)
        {
            if (staleState.Filter)
            {
                await materializeService.MaterializeSelectedAsync(projectId, document, scenarios, selectedPositions, cancellationToken);
            }
            else
            {
                await materializeService.RequireUsableAsync(projectId, document, scenarios, selectedPositions, cancellationToken);
            }
        }

        var filterResultsCurrent = !(await resultStaleStateStore.ReadAsync(projectId, cancellationToken)).Filter;

        var selected = selectedPositions.ToHashSet();
        var selectedScenarios = scenarios
            .Where(item => selected.Contains(item.Position))
            .ToArray();
        var scenarioSelections = new List<WorkingPaperScenarioSelection>(selectedPositions.Count);
        IReadOnlyDictionary<string, string>? conditionCategoryLabels = null;
        IReadOnlyDictionary<string, string>? conditionFieldLabels = null;
        IReadOnlyList<AccountCategory>? conditionCategories = null;
        SupportOperationStage.Enter("condition_render");
        foreach (var scenario in selectedScenarios)
        {
            using var definition = JsonDocument.Parse(scenario.DefinitionJson);
            var scenarioSpec = FilterScenarioPayloadParser.Parse(
                definition.RootElement,
                document.MoneyScale);
            if (conditionCategoryLabels is null)
            {
                var categorySnapshot = await accountCategoryStore.ReadAsync(projectId, cancellationToken);
                conditionCategories = categorySnapshot.Categories;
                conditionCategoryLabels = categorySnapshot.Categories.ToDictionary(category => category.CategoryId, category => category.Label);
                var mapping = await mappingStore.FindAsync(projectId, DatasetKind.Gl, cancellationToken);
                conditionFieldLabels = (mapping?.GlOptions?.RdeFields ?? []).ToDictionary(field => field.FieldId, field => field.Label);
            }
            var conditionLogic = FilterConditionRenderer.Render(definition.RootElement, conditionCategoryLabels, conditionFieldLabels, conditionCategories, document.LastAccountingPeriodDate, document.PeriodStart, document.PeriodEnd, forDocument: true);
            scenarioSelections.Add(new WorkingPaperScenarioSelection(
                scenario.Position,
                scenario.Name,
                scenario.Rationale,
                JetAuditProgram.ResolveWorkingPaperTagScope(scenarioSpec),
                conditionLogic));
        }

        var context = new WorkingPaperContext(
            ProjectId: projectId,
            CompanyName: ReportExportSupport.CompanyName(document),
            PeriodStart: document.PeriodStart,
            PeriodEnd: document.PeriodEnd,
            PrepStartDate: document.LastAccountingPeriodDate,
            MoneyScale: document.MoneyScale,
            ValidationResultId: validationResult.ResultId,
            ScenarioRevision: scenarioRevision,
            ScenarioPositions: selectedPositions,
            PopulationScope: filterRevision.PopulationScope);

        SupportOperationStage.Enter("report_plan");
        var workbookMetadata = await ReportWorkbookMetadataFactory.LoadAsync(
            projectId,
            document,
            mappingStore,
            accountCategoryStore,
            cancellationToken);
        var customFields = FormalReportRdeFieldSelection.ForScenarios(
            selectedScenarios,
            workbookMetadata,
            document.MoneyScale);

        var plan = JetAuditProgram.Plan(new WorkingPaperRequest(
            ProjectId: projectId,
            PeriodStart: document.PeriodStart,
            PeriodEnd: document.PeriodEnd,
            PrepStartDate: document.LastAccountingPeriodDate,
            MoneyScale: document.MoneyScale,
            ValidationResultId: validationResult.ResultId,
            ScenarioRevision: scenarioRevision,
            Scenarios: scenarioSelections,
            PopulationScope: filterRevision.PopulationScope,
            WorkbookMetadata: workbookMetadata,
            CustomFields: customFields,
            CompletenessNaReason: ReportExportSupport.CompletenessNaReason(validationResult)));
        var facts = await planningFactsPort.ExecuteAsync(
            plan,
            cancellationToken);
        var finalizedPlan = JetAuditProgram.Finalize(plan, facts);

        ExportStats? exportStats = null;
        var request = new ReportWriteRequest(
                ReportKind.WorkingPaper,
                new ReportSourceRefs(
                    ValidationResultId: validationResult.ResultId,
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
                        reportProgress.WriterProgress);
                    reportProgress.FinalizingWorkbook();
                }, document.PeriodStart, document.PeriodEnd);
        // 正式組裝的報告清單會寫操作紀錄；經這個介面發布才拿得到寫不進操作紀錄時的提醒（裁定 2026-10-19-20）。
        Report report;
        string? operationLogWarning = null;
        if (reportStore is IAuditedReportPublishingStore auditedStore)
        {
            var publication = await auditedStore.WriteAuditedAsync(
                projectId,
                request,
                _ => reportProgress.PublishingReport(),
                cancellationToken);
            report = publication.Reports[0];
            operationLogWarning = publication.OperationLogWarning;
        }
        else
        {
            report = reportStore is IReportPublishingStore publishingStore
                ? await publishingStore.WriteWithPublishingAsync(
                    projectId,
                    request,
                    _ => reportProgress.PublishingReport(),
                    cancellationToken)
                : await reportStore.WriteAsync(projectId, request, cancellationToken);
        }

        SupportOperationStage.Enter("catalog_refresh");
        var catalog = await WorkflowResultStateSupport.AfterPublicationAsync(projectId, repositories.Results, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportStore);
        return new
        {
            ok = true,
            filterResultsCurrent,
            publishedReport = ReportExportSupport.ReportWire(report),
            reports = catalog.Reports,
            reportWarning = catalog.Warning,
            warnings = ReportExportSupport.Warnings(operationLogWarning),
            sheetStats = (exportStats?.SheetStats ?? Array.Empty<SheetStat>())
                .Select(item => (object)new { sheetName = item.SheetName, rowsWritten = item.RowsWritten })
                .ToArray()
        };
    }
}
