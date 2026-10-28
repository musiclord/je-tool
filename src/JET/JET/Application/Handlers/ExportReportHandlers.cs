using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

public sealed class ExportValidationArtifactsHandler(
    IProjectStore projectStore,
    ProjectSession session,
    IJetEventPublisher eventPublisher) : IApplicationActionHandler
{
    public string Action => "export.validationArtifacts";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var runId = PayloadReader.GetOptionalString(payload, "runId")
            ?? throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "run_id_missing");
        var (projectId, repositories) = session.RequireActive();
        var validationWriter = repositories.ValidationReportWriter;
        var infWriter = repositories.InfReportWriter;
        var runStore = repositories.RuleRuns;
        var artifactStore = repositories.ReportArtifactStore;
        var validationReportPlanningFactsPort = repositories.ValidationReportPlanningFacts;
        var fieldDefinitionFactsPort = repositories.FieldDefinitionFacts;
        var accountTaxonomyStore = repositories.AccountTaxonomy;
        var mappingStateStore = repositories.MappingStates;
        var progressSession = new ExportProgressSession(eventPublisher, cancellationToken);
        var progressByKind = new Dictionary<ReportArtifactKind, ExportArtifactProgress>
        {
            [ReportArtifactKind.ValidationReport] = progressSession.Start(ReportArtifactKind.ValidationReport),
            [ReportArtifactKind.InfReport] = progressSession.Start(ReportArtifactKind.InfReport)
        };
        SupportOperationStage.Enter("run_read");
        var run = await ReportExportSupport.RequireCurrentRunAsync(
            runStore, projectId, RuleRunKinds.Validate, runId, cancellationToken);
        SupportOperationStage.Enter("metadata_read");
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);
        var workbookMetadata = await ReportWorkbookMetadataFactory.LoadAsync(
            projectId,
            document,
            mappingStateStore,
            accountTaxonomyStore,
            cancellationToken);
        var allRdeFields = ReportWorkbookMetadataFactory.AllRdeFields(workbookMetadata);
        SupportOperationStage.Enter("report_plan");
        var plan = JetAuditProgram.Plan(new ReportExportRequest(
            Action,
            projectId,
            ValidationRunId: run.RunId,
            WorkbookMetadata: workbookMetadata,
            CustomFields: allRdeFields));
        var project = ReportExportSupport.ProjectContext(document);
        var reportContext = new ValidationReportContext(
            project,
            run.RunId,
            run.GeneratedUtc,
            run.SummaryJson);
        var reportProjection = ValidationReportProjectionParser.Parse(run.SummaryJson);
        var unfinalizedReportPlan = JetAuditProgram.Plan(new ValidationReportRequest(
            projectId,
            document.PeriodStart,
            document.PeriodEnd,
            reportProjection.CompletenessDiffAccountCount,
            reportProjection.NullAccountCount,
            reportProjection.NullDocumentCount,
            reportProjection.NullDescriptionCount,
            reportProjection.OutOfRangeDateCount));
        var planningFacts = await validationReportPlanningFactsPort.ExecuteAsync(
            unfinalizedReportPlan,
            cancellationToken);
        var validationReportPlan = JetAuditProgram.Finalize(
            unfinalizedReportPlan,
            planningFacts);
        var targetTbDefinitions = await fieldDefinitionFactsPort.ReadAsync(
            projectId,
            DatasetKind.Tb,
            LegacyFieldDefinitionScope.Target,
            cancellationToken);
        var targetGlDefinitions = await fieldDefinitionFactsPort.ReadAsync(
            projectId,
            DatasetKind.Gl,
            LegacyFieldDefinitionScope.Target,
            cancellationToken);
        var fieldInfoProjection = JetAuditProgram.ProjectFieldInfo(
            targetTbDefinitions,
            targetGlDefinitions);
        var requests = new ReportArtifactWriteRequest[]
        {
            new(
                plan.ArtifactKinds[0],
                plan.SourceRef,
                async (stream, ct) =>
                {
                    var progress = progressByKind[ReportArtifactKind.ValidationReport];
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
                plan.ArtifactKinds[1],
                plan.SourceRef,
                async (stream, ct) =>
                {
                    var progress = progressByKind[ReportArtifactKind.InfReport];
                    var infContext = new InfReportContext(project, run.RunId, run.GeneratedUtc);
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

        var facts = await new ReportArtifactExecutionPort(artifactStore, requests,
                kind => progressByKind[kind].PublishingArtifact())
            .ExecuteAsync(plan, cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);

        SupportOperationStage.Enter("catalog_refresh");
        var catalog = await WorkflowResultStateSupport.AfterPublicationAsync(projectId, repositories.RuleRuns, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportArtifactStore);
        return new
        {
            ok = true,
            artifacts = result.Artifacts.Select(ReportExportSupport.ArtifactWire).ToArray(),
            reportArtifacts = catalog.Artifacts,
            reportArtifactWarning = catalog.Warning,
            warnings = ReportExportSupport.Warnings(result.AuditLogWarning)
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
        var runId = PayloadReader.GetOptionalString(payload, "runId")
            ?? throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "run_id_missing");
        var (projectId, repositories) = session.RequireActive();
        var writer = repositories.PrescreenReportWriter;
        var runStore = repositories.RuleRuns;
        var artifactStore = repositories.ReportArtifactStore;
        IPrescreenReportPlanningFactsPort prescreenReportPlanningFactsPort =
            repositories.PrescreenReportPlanningFacts;
        var mappingStateStore = repositories.MappingStates;
        var accountTaxonomyStore = repositories.AccountTaxonomy;
        SupportOperationStage.Enter("run_read");
        await CompletenessEligibilitySupport.RequireCurrentAsync(
            runStore,
            projectId,
            cancellationToken);
        var progressSession = new ExportProgressSession(eventPublisher, cancellationToken);
        var artifactProgress = progressSession.Start(ReportArtifactKind.PrescreenReport);
        var run = await ReportExportSupport.RequireCurrentRunAsync(
            runStore, projectId, RuleRunKinds.Prescreen, runId, cancellationToken);
        SupportOperationStage.Enter("metadata_read");
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);
        var workbookMetadata = await ReportWorkbookMetadataFactory.LoadAsync(
            projectId,
            document,
            mappingStateStore,
            accountTaxonomyStore,
            cancellationToken);
        SupportOperationStage.Enter("report_plan");
        var plan = JetAuditProgram.Plan(new ReportExportRequest(
            Action,
            projectId,
            PrescreenRunId: run.RunId,
            WorkbookMetadata: workbookMetadata));
        var project = ReportExportSupport.ProjectContext(document);
        var reportContext = new PrescreenReportContext(
            project,
            run.RunId,
            run.GeneratedUtc,
            run.SummaryJson);
        var reportProjection = PrescreenReportProjectionParser.Parse(run.SummaryJson);
        var ruleContext = new FilterRuleContext(
            project.MoneyScale,
            project.LastPeriodStart,
            project.PeriodStart,
            project.PeriodEnd);
        var unfinalizedReportPlan = JetAuditProgram.Plan(
            new PrescreenReportRequest(
                projectId,
                ruleContext,
                reportProjection.PostPeriodApproval.NaReason,
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

        var request = new ReportArtifactWriteRequest(
                plan.ArtifactKinds[0],
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
                        artifactProgress.WriterProgress);
                    artifactProgress.FinalizingWorkbook();
                }, document.PeriodStart, document.PeriodEnd);
        var facts = await new ReportArtifactExecutionPort(artifactStore, [request],
                _ => artifactProgress.PublishingArtifact())
            .ExecuteAsync(plan, cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);
        var artifact = result.Artifacts.Single();

        SupportOperationStage.Enter("catalog_refresh");
        var catalog = await WorkflowResultStateSupport.AfterPublicationAsync(projectId, repositories.RuleRuns, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportArtifactStore);
        return new
        {
            ok = true, artifact = ReportExportSupport.ArtifactWire(artifact),
            reportArtifacts = catalog.Artifacts,
            reportArtifactWarning = catalog.Warning,
            warnings = ReportExportSupport.Warnings(result.AuditLogWarning)
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
        var validationRunId = PayloadReader.GetOptionalString(payload, "validationRunId")
            ?? throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "validation_run_id_missing");
        var revision = PayloadReader.GetOptionalString(payload, "revision")
            ?? throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "revision_missing");
        var (projectId, repositories) = session.RequireActive();
        var writer = repositories.CriteriaSelectionReportWriter;
        var scenarioStore = repositories.FilterScenarios;
        var materializeService = repositories.FilterRunMaterializeService;
        var runStore = repositories.RuleRuns;
        var artifactStore = repositories.ReportArtifactStore;
        var accountTaxonomyStore = repositories.AccountTaxonomy;
        var mappingStore = repositories.MappingStates;
        SupportOperationStage.Enter("run_read");
        var validationRun = await CompletenessEligibilitySupport.RequireCurrentAsync(
            runStore,
            projectId,
            cancellationToken);
        ReportExportSupport.RequireRequestedValidationRun(validationRun, validationRunId);
        var progressSession = new ExportProgressSession(eventPublisher, cancellationToken);
        var artifactProgress = progressSession.Start(ReportArtifactKind.CriteriaSelectionReport);
        SupportOperationStage.Enter("scenario_read");
        var scenarios = await scenarioStore.ListAsync(projectId, cancellationToken);
        var filterRevision = ReportExportSupport.RequireRevisionState(scenarios, revision);
        revision = filterRevision.Revision;
        SupportOperationStage.Enter("metadata_read");
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);

        // filter.commit 會在同一交易清除上一版命中。正式 Criteria 報告必須先以
        // 目前 revision 的全部情境重新物化，writer 才能輸出可追溯的明細母體。
        SupportOperationStage.Enter("filter_materialize");
        await materializeService.MaterializeAllAsync(projectId, document, scenarios, cancellationToken);

        // 科目配對多選讀回要顯示目前 taxonomy 的顯示名稱，因此條件邏輯以專案 taxonomy 渲染。
        SupportOperationStage.Enter("condition_render");
        var taxonomy = await accountTaxonomyStore.ReadAsync(projectId, cancellationToken);
        var categoryLabels = taxonomy.Categories.ToDictionary(
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
                FilterConditionRenderer.Render(definition.RootElement, categoryLabels, rdeFieldLabels, taxonomy.Categories, document.LastAccountingPeriodDate, document.PeriodStart, document.PeriodEnd);
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
            accountTaxonomyStore,
            cancellationToken);
        var customFields = FormalReportRdeFieldSelection.ForScenarios(
            scenarios,
            workbookMetadata,
            document.MoneyScale);

        var plan = JetAuditProgram.Plan(new ReportExportRequest(
            Action,
            projectId,
            ValidationRunId: validationRun.RunId,
            ScenarioRevision: revision,
            ScenarioPositions: scenarios.Select(item => item.Position).ToArray(),
            WorkbookMetadata: workbookMetadata,
            CustomFields: customFields,
            FilterDataRevision: await repositories.ResultStaleStates.ReadFilterDataRevisionAsync(projectId, cancellationToken)));
        var reportContext = new CriteriaSelectionReportContext(
            ReportExportSupport.ProjectContext(document),
            revision,
            scenarios[0].SavedUtc,
            scenarios,
            conditionLogic,
            filterRevision.PopulationScope,
            wholeVoucherSummaryPositions);
        var request = new ReportArtifactWriteRequest(
                plan.ArtifactKinds[0],
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
                        artifactProgress.WriterProgress);
                    artifactProgress.FinalizingWorkbook();
                }, document.PeriodStart, document.PeriodEnd);
        var facts = await new ReportArtifactExecutionPort(artifactStore, [request],
                _ => artifactProgress.PublishingArtifact())
            .ExecuteAsync(plan, cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);
        var artifact = result.Artifacts.Single();

        SupportOperationStage.Enter("catalog_refresh");
        var catalog = await WorkflowResultStateSupport.AfterPublicationAsync(projectId, repositories.RuleRuns, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportArtifactStore);
        return new
        {
            ok = true, artifact = ReportExportSupport.ArtifactWire(artifact),
            reportArtifacts = catalog.Artifacts,
            reportArtifactWarning = catalog.Warning,
            warnings = ReportExportSupport.Warnings(result.AuditLogWarning)
        };
    }
}
