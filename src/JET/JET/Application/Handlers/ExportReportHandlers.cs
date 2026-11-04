using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

public sealed class ExportValidationReportsHandler(
    IProjectStore projectStore,
    ProjectSession session,
    IJetEventPublisher eventPublisher) : IApplicationActionHandler
{
    public string Action => "export.validationReports";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var resultId = PayloadReader.GetOptionalString(payload, "resultId")
            ?? throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "result_id_missing");
        var (projectId, repositories) = session.RequireActive();
        var validationWriter = repositories.ValidationReportWriter;
        var infWriter = repositories.InfReportWriter;
        var resultStore = repositories.Results;
        var reportStore = repositories.ReportStore;
        var validationReportPlanningFactsPort = repositories.ValidationReportPlanningFacts;
        var fieldInfoFactsPort = repositories.FieldInfoFacts;
        var accountCategoryStore = repositories.AccountCategories;
        var mappingStateStore = repositories.MappingStates;
        var progressSession = new ExportProgressSession(eventPublisher, cancellationToken);
        var progressByKind = new Dictionary<ReportKind, ExportReportProgress>
        {
            [ReportKind.ValidationReport] = progressSession.Start(ReportKind.ValidationReport),
            [ReportKind.InfReport] = progressSession.Start(ReportKind.InfReport)
        };
        SupportOperationStage.Enter("run_read");
        var run = await ReportExportSupport.RequireCurrentRunAsync(
            resultStore, projectId, ResultKind.Validation, resultId, cancellationToken);
        SupportOperationStage.Enter("metadata_read");
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);
        var workbookMetadata = await ReportWorkbookMetadataFactory.LoadAsync(
            projectId,
            document,
            mappingStateStore,
            accountCategoryStore,
            cancellationToken);
        var allRdeFields = ReportWorkbookMetadataFactory.AllRdeFields(workbookMetadata);
        SupportOperationStage.Enter("report_plan");
        var plan = JetAuditProgram.Plan(new ReportExportRequest(
            Action,
            projectId,
            ValidationResultId: run.ResultId,
            WorkbookMetadata: workbookMetadata,
            CustomFields: allRdeFields));
        var project = ReportExportSupport.ProjectContext(document);
        var reportContext = new ValidationReportContext(
            project,
            run.ResultId,
            // 裁定 2026-11-02-03：編製日期寫按下匯出的那一天，用本機日期，不寫執行資料驗證的時間。
            DateOnly.FromDateTime(DateTime.Now),
            run.SummaryJson);
        var reportProjection = ValidationReportProjectionParser.Parse(run.SummaryJson);
        var unfinalizedReportPlan = JetAuditProgram.Plan(new ValidationReportRequest(
            projectId,
            document.PeriodStart,
            document.PeriodEnd,
            reportProjection.CompletenessDiffAccountCount,
            reportProjection.BlankAccountNumberCount,
            reportProjection.BlankVoucherNumberCount,
            reportProjection.BlankDescriptionCount,
            reportProjection.ApprovalDateNotInPeriodCount));
        var planningFacts = await validationReportPlanningFactsPort.ExecuteAsync(
            unfinalizedReportPlan,
            cancellationToken);
        var validationReportPlan = JetAuditProgram.Finalize(
            unfinalizedReportPlan,
            planningFacts);
        var targetTbDefinitions = await fieldInfoFactsPort.ReadAsync(
            projectId,
            DatasetKind.Tb,
            FieldInfoScope.Target,
            cancellationToken);
        var targetGlDefinitions = await fieldInfoFactsPort.ReadAsync(
            projectId,
            DatasetKind.Gl,
            FieldInfoScope.Target,
            cancellationToken);
        var fieldInfoProjection = JetAuditProgram.ProjectFieldInfo(
            targetTbDefinitions,
            targetGlDefinitions);
        var requests = new ReportWriteRequest[]
        {
            new(
                plan.ReportKinds[0],
                plan.SourceRef,
                async (stream, ct) =>
                {
                    var progress = progressByKind[ReportKind.ValidationReport];
                    await validationWriter.WriteAsync(
                        stream,
                        reportContext,
                        reportProjection,
                        validationReportPlan,
                        fieldInfoProjection,
                        plan.WorkbookMetadata!,
                        document.OperatorId,
                        ct,
                        progress.WriterProgress);
                    progress.FinalizingWorkbook();
                }, document.PeriodStart, document.PeriodEnd),
            new(
                plan.ReportKinds[1],
                plan.SourceRef,
                async (stream, ct) =>
                {
                    var progress = progressByKind[ReportKind.InfReport];
                    var infContext = new InfReportContext(project, run.ResultId, run.GeneratedUtc);
                    await infWriter.WriteAsync(
                        stream,
                        infContext,
                        plan.WorkbookMetadata!,
                        plan.CustomFields,
                        ct,
                        progress.WriterProgress);
                    progress.FinalizingWorkbook();
                }, document.PeriodStart, document.PeriodEnd)
        };

        var facts = await new ReportExecutionPort(reportStore, requests,
                kind => progressByKind[kind].PublishingReport())
            .ExecuteAsync(plan, cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);

        SupportOperationStage.Enter("catalog_refresh");
        var catalog = await WorkflowResultStateSupport.AfterPublicationAsync(projectId, repositories.Results, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportStore);
        return new
        {
            ok = true,
            publishedReports = result.Reports.Select(ReportExportSupport.ReportWire).ToArray(),
            reports = catalog.Reports,
            reportWarning = catalog.Warning,
            warnings = ReportExportSupport.Warnings(result.OperationLogWarning)
        };
    }

}

public sealed class ExportPrescreenReportHandler(
    IProjectStore projectStore,
    ProjectSession session,
    IJetEventPublisher eventPublisher) : IApplicationActionHandler
{
    public string Action => "export.prescreenReport";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var resultId = PayloadReader.GetOptionalString(payload, "resultId")
            ?? throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "result_id_missing");
        var (projectId, repositories) = session.RequireActive();
        var writer = repositories.PrescreenReportWriter;
        var resultStore = repositories.Results;
        var reportStore = repositories.ReportStore;
        IPrescreenReportPlanningFactsPort prescreenReportPlanningFactsPort =
            repositories.PrescreenReportPlanningFacts;
        var mappingStateStore = repositories.MappingStates;
        var accountCategoryStore = repositories.AccountCategories;
        SupportOperationStage.Enter("run_read");
        await CompletenessEligibilitySupport.RequireCurrentAsync(
            resultStore,
            projectId,
            cancellationToken);
        var progressSession = new ExportProgressSession(eventPublisher, cancellationToken);
        var reportProgress = progressSession.Start(ReportKind.PrescreenReport);
        var run = await ReportExportSupport.RequireCurrentRunAsync(
            resultStore, projectId, ResultKind.Prescreen, resultId, cancellationToken);
        SupportOperationStage.Enter("metadata_read");
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);
        var workbookMetadata = await ReportWorkbookMetadataFactory.LoadAsync(
            projectId,
            document,
            mappingStateStore,
            accountCategoryStore,
            cancellationToken);
        SupportOperationStage.Enter("report_plan");
        var plan = JetAuditProgram.Plan(new ReportExportRequest(
            Action,
            projectId,
            PrescreenResultId: run.ResultId,
            WorkbookMetadata: workbookMetadata));
        var project = ReportExportSupport.ProjectContext(document);
        var reportContext = new PrescreenReportContext(
            project,
            run.ResultId,
            // 裁定 2026-11-02-03：編製日期寫按下匯出的那一天，用本機日期，不寫執行預篩選的時間。
            DateOnly.FromDateTime(DateTime.Now),
            run.SummaryJson);
        var reportProjection = PrescreenReportProjectionParser.Parse(run.SummaryJson);
        var ruleContext = new FilterConditionContext(
            project.MoneyScale,
            project.PrepStartDate,
            project.PeriodStart,
            project.PeriodEnd);
        var unfinalizedReportPlan = JetAuditProgram.Plan(
            new PrescreenReportRequest(
                projectId,
                ruleContext,
                reportProjection.PrepPeriodApproval.NaReason,
                reportProjection.SuspiciousKeywords.NaReason,
                reportProjection.UnexpectedAccountPair.NaReason,
                reportProjection.TrailingZeros.NaReason,
                reportProjection.BlankDescription.NaReason));
        var planningFacts = await prescreenReportPlanningFactsPort.ExecuteAsync(
            unfinalizedReportPlan,
            cancellationToken);
        var prescreenReportPlan = JetAuditProgram.Finalize(
            unfinalizedReportPlan,
            planningFacts);

        var request = new ReportWriteRequest(
                plan.ReportKinds[0],
                plan.SourceRef,
                async (stream, ct) =>
                {
                    await writer.WriteAsync(
                        stream,
                        reportContext,
                        reportProjection,
                        prescreenReportPlan,
                        plan.WorkbookMetadata!,
                        document.OperatorId,
                        ct,
                        reportProgress.WriterProgress);
                    reportProgress.FinalizingWorkbook();
                }, document.PeriodStart, document.PeriodEnd);
        var facts = await new ReportExecutionPort(reportStore, [request],
                _ => reportProgress.PublishingReport())
            .ExecuteAsync(plan, cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);
        var report = result.Reports.Single();

        SupportOperationStage.Enter("catalog_refresh");
        var catalog = await WorkflowResultStateSupport.AfterPublicationAsync(projectId, repositories.Results, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportStore);
        return new
        {
            ok = true, publishedReport = ReportExportSupport.ReportWire(report),
            reports = catalog.Reports,
            reportWarning = catalog.Warning,
            warnings = ReportExportSupport.Warnings(result.OperationLogWarning)
        };
    }
}

public sealed class ExportCriteriaSelectionReportHandler(
    IProjectStore projectStore,
    ProjectSession session,
    IJetEventPublisher eventPublisher) : IApplicationActionHandler
{
    public string Action => "export.criteriaSelectionReport";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var validationResultId = PayloadReader.GetOptionalString(payload, "validationResultId")
            ?? throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "validation_result_id_missing");
        var revision = PayloadReader.GetOptionalString(payload, "revision")
            ?? throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "revision_missing");
        var (projectId, repositories) = session.RequireActive();
        var writer = repositories.CriteriaSelectionReportWriter;
        var scenarioStore = repositories.FilterScenarios;
        var materializeService = repositories.FilterHitMaterializeService;
        var resultStore = repositories.Results;
        var reportStore = repositories.ReportStore;
        var accountCategoryStore = repositories.AccountCategories;
        var mappingStore = repositories.MappingStates;
        SupportOperationStage.Enter("run_read");
        var validationResult = await CompletenessEligibilitySupport.RequireCurrentAsync(
            resultStore,
            projectId,
            cancellationToken);
        ReportExportSupport.RequireRequestedValidationResult(validationResult, validationResultId);
        var progressSession = new ExportProgressSession(eventPublisher, cancellationToken);
        var reportProgress = progressSession.Start(ReportKind.CriteriaSelectionReport);
        SupportOperationStage.Enter("scenario_read");
        var scenarios = await scenarioStore.ListAsync(projectId, cancellationToken);
        var filterRevision = ReportExportSupport.RequireRevisionState(scenarios, revision);
        revision = filterRevision.Revision;
        SupportOperationStage.Enter("metadata_read");
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);

        // 儲存情境時已經存下每個情境的結果，報告直接用存好的結果，不重算；只照重算前的同一套檢查確認情境仍能套用。
        // 結果標成過期時（例如開案時整批改用新規則），才用目前規則重算全部情境。
        SupportOperationStage.Enter("filter_materialize");
        if ((await repositories.ResultStaleStates.ReadAsync(projectId, cancellationToken)).Filter)
        {
            await materializeService.MaterializeAllAsync(projectId, document, scenarios, cancellationToken);
        }
        else
        {
            await materializeService.RequireUsableAsync(projectId, document, scenarios, null, cancellationToken);
        }

        // 科目配對多選讀回要顯示目前科目分類的顯示名稱，因此條件邏輯以專案的科目分類渲染。
        SupportOperationStage.Enter("condition_render");
        var categorySnapshot = await accountCategoryStore.ReadAsync(projectId, cancellationToken);
        var categoryLabels = categorySnapshot.Categories.ToDictionary(
            static category => category.CategoryId,
            static category => category.Label,
            StringComparer.Ordinal);

        // typed 條件讀回用目前 committed RDE metadata 的顯示 label（凍結裁決：僅 label 改名時
        // renderer 直接反映新名稱；欄位不存在時 renderer 退回 fieldId 原字串）。
        var glMapping = await mappingStore.FindAsync(projectId, DatasetKind.Gl, cancellationToken);
        var rdeFieldLabels = (glMapping?.GlOptions?.RdeFields ?? []).ToDictionary(
            static field => field.FieldId,
            static field => field.Label,
            StringComparer.Ordinal);
        var conditionLogic = new Dictionary<int, string>();
        var wholeVoucherSummaryPositions = new HashSet<int>();
        foreach (var scenario in scenarios)
        {
            using var definition = JsonDocument.Parse(scenario.DefinitionJson);
            conditionLogic[scenario.Position] =
                FilterConditionRenderer.Render(definition.RootElement, categoryLabels, rdeFieldLabels, categorySnapshot.Categories, document.LastAccountingPeriodDate, document.PeriodStart, document.PeriodEnd, forDocument: true);
            var scenarioSpec = FilterScenarioPayloadParser.Parse(
                definition.RootElement,
                document.MoneyScale);
            if (JetAuditProgram.UsesLegacyWholeVoucherRows(scenarioSpec))
            {
                wholeVoucherSummaryPositions.Add(scenario.Position);
            }
        }

        SupportOperationStage.Enter("report_plan");
        var workbookMetadata = await ReportWorkbookMetadataFactory.LoadAsync(
            projectId,
            document,
            mappingStore,
            accountCategoryStore,
            cancellationToken);
        var customFields = FormalReportRdeFieldSelection.ForScenarios(
            scenarios,
            workbookMetadata,
            document.MoneyScale);

        var plan = JetAuditProgram.Plan(new ReportExportRequest(
            Action,
            projectId,
            ValidationResultId: validationResult.ResultId,
            ScenarioRevision: revision,
            ScenarioPositions: scenarios.Select(item => item.Position).ToArray(),
            WorkbookMetadata: workbookMetadata,
            CustomFields: customFields,
            FilterDataRevision: await repositories.ResultStaleStates.ReadFilterDataRevisionAsync(projectId, cancellationToken)));
        var reportContext = new CriteriaSelectionReportContext(
            ReportExportSupport.ProjectContext(document),
            revision,
            // 裁定 2026-11-01-04：編製日期寫按下匯出的那一天，用本機日期，不寫情境的儲存時間。
            DateOnly.FromDateTime(DateTime.Now),
            scenarios,
            conditionLogic,
            filterRevision.PopulationScope,
            wholeVoucherSummaryPositions);
        var request = new ReportWriteRequest(
                plan.ReportKinds[0],
                plan.SourceRef,
                async (stream, ct) =>
                {
                    await writer.WriteAsync(
                        stream,
                        reportContext,
                        plan.WorkbookMetadata!,
                        plan.CustomFields,
                        document.OperatorId,
                        ct,
                        reportProgress.WriterProgress);
                    reportProgress.FinalizingWorkbook();
                }, document.PeriodStart, document.PeriodEnd);
        var facts = await new ReportExecutionPort(reportStore, [request],
                _ => reportProgress.PublishingReport())
            .ExecuteAsync(plan, cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);
        var report = result.Reports.Single();

        SupportOperationStage.Enter("catalog_refresh");
        var catalog = await WorkflowResultStateSupport.AfterPublicationAsync(projectId, repositories.Results, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportStore);
        return new
        {
            ok = true, publishedReport = ReportExportSupport.ReportWire(report),
            reports = catalog.Reports,
            reportWarning = catalog.Warning,
            warnings = ReportExportSupport.Warnings(result.OperationLogWarning)
        };
    }
}
