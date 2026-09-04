using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

public sealed class ExportValidationArtifactsHandler(
    IValidationReportWriter validationWriter,
    IInfReportWriter infWriter,
    IRuleRunStore runStore,
    IProjectStore projectStore,
    IReportArtifactStore artifactStore,
    ProjectSession session,
    IJetEventPublisher eventPublisher) : IApplicationActionHandler
{
    private readonly IValidationReportPlanningFactsPort? _validationReportPlanningFactsPort;
    private readonly ILegacyFieldDefinitionFactsPort? _fieldDefinitionFactsPort;
    private readonly IAccountTaxonomyStore? _accountTaxonomyStore;
    private readonly IMappingStateStore? _mappingStateStore;

    internal ExportValidationArtifactsHandler(
        IValidationReportWriter validationWriter,
        IInfReportWriter infWriter,
        IRuleRunStore runStore,
        IProjectStore projectStore,
        IReportArtifactStore artifactStore,
        ProjectSession session,
        IJetEventPublisher eventPublisher,
        IValidationReportPlanningFactsPort validationReportPlanningFactsPort,
        ILegacyFieldDefinitionFactsPort fieldDefinitionFactsPort,
        IAccountTaxonomyStore accountTaxonomyStore)
        : this(
            validationWriter,
            infWriter,
            runStore,
            projectStore,
            artifactStore,
            session,
            eventPublisher,
            validationReportPlanningFactsPort,
            fieldDefinitionFactsPort)
    {
        _accountTaxonomyStore = accountTaxonomyStore
            ?? throw new ArgumentNullException(nameof(accountTaxonomyStore));
    }

    internal ExportValidationArtifactsHandler(
        IValidationReportWriter validationWriter,
        IInfReportWriter infWriter,
        IRuleRunStore runStore,
        IProjectStore projectStore,
        IReportArtifactStore artifactStore,
        ProjectSession session,
        IJetEventPublisher eventPublisher,
        IValidationReportPlanningFactsPort validationReportPlanningFactsPort,
        ILegacyFieldDefinitionFactsPort fieldDefinitionFactsPort)
        : this(
            validationWriter,
            infWriter,
            runStore,
            projectStore,
            artifactStore,
            session,
            eventPublisher)
    {
        _validationReportPlanningFactsPort = validationReportPlanningFactsPort
            ?? throw new ArgumentNullException(nameof(validationReportPlanningFactsPort));
        _fieldDefinitionFactsPort = fieldDefinitionFactsPort
            ?? throw new ArgumentNullException(nameof(fieldDefinitionFactsPort));
    }

    internal ExportValidationArtifactsHandler(
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
        : this(
            validationWriter,
            infWriter,
            runStore,
            projectStore,
            artifactStore,
            session,
            eventPublisher,
            validationReportPlanningFactsPort,
            fieldDefinitionFactsPort,
            accountTaxonomyStore)
    {
        _mappingStateStore = mappingStateStore
            ?? throw new ArgumentNullException(nameof(mappingStateStore));
    }

    public string Action => "export.validationArtifacts";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var runId = PayloadReader.GetOptionalString(payload, "runId")
            ?? throw new JetActionException(JetErrorCodes.InvalidPayload, "payload 缺少必填欄位 'runId'。");
        var projectId = session.RequireProjectId();
        var progressSession = new ExportProgressSession(eventPublisher, cancellationToken);
        var progressByKind = new Dictionary<ReportArtifactKind, ExportArtifactProgress>
        {
            [ReportArtifactKind.ValidationReport] = progressSession.Start(ReportArtifactKind.ValidationReport),
            [ReportArtifactKind.InfReport] = progressSession.Start(ReportArtifactKind.InfReport)
        };
        var run = await ReportExportSupport.RequireCurrentRunAsync(
            runStore, projectId, RuleRunKinds.Validate, runId, cancellationToken);
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");
        var requiresFormalMetadata = validationWriter is IFormalPlannedValidationReportWriter
            || infWriter is IFormalInfReportWriter;
        if (requiresFormalMetadata
            && (_accountTaxonomyStore is null || _mappingStateStore is null))
        {
            throw new InvalidOperationException(
                "Formal validation artifacts require mapping and taxonomy metadata stores.");
        }
        ReportWorkbookMetadata? workbookMetadata = null;
        IReadOnlyList<GlRdeFieldMetadata> allRdeFields = [];
        if (_accountTaxonomyStore is not null && _mappingStateStore is not null)
        {
            workbookMetadata = await ReportWorkbookMetadataFactory.LoadAsync(
                projectId,
                document,
                _mappingStateStore,
                _accountTaxonomyStore,
                cancellationToken);
            allRdeFields = ReportWorkbookMetadataFactory.AllRdeFields(workbookMetadata);
        }
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
        var plannedValidationWriter = validationWriter as IPlannedValidationReportWriter;
        var formalValidationWriter = validationWriter as IFormalPlannedValidationReportWriter;
        var typedValidationWriter = validationWriter as ITypedValidationReportWriter;
        ValidationReportProjection? reportProjection = null;
        ValidationReportPlan? validationReportPlan = null;
        FieldInfoProjection? fieldInfoProjection = null;

        if (plannedValidationWriter is not null
            && _validationReportPlanningFactsPort is not null
            && _fieldDefinitionFactsPort is not null)
        {
            reportProjection = ValidationReportProjectionParser.Parse(run.SummaryJson);
            var unfinalizedReportPlan = JetAuditProgram.Plan(new ValidationReportRequest(
                projectId,
                document.PeriodStart,
                document.PeriodEnd,
                reportProjection.CompletenessDiffAccountCount,
                reportProjection.NullAccountCount,
                reportProjection.NullDocumentCount,
                reportProjection.NullDescriptionCount,
                reportProjection.OutOfRangeDateCount));
            var planningFacts = await JetAuditProgram.ExecuteAsync(
                unfinalizedReportPlan,
                _validationReportPlanningFactsPort,
                cancellationToken);
            validationReportPlan = JetAuditProgram.Finalize(
                unfinalizedReportPlan,
                planningFacts);
            var targetTbDefinitions = await _fieldDefinitionFactsPort.ReadAsync(
                projectId,
                DatasetKind.Tb,
                LegacyFieldDefinitionScope.Target,
                cancellationToken);
            var targetGlDefinitions = await _fieldDefinitionFactsPort.ReadAsync(
                projectId,
                DatasetKind.Gl,
                LegacyFieldDefinitionScope.Target,
                cancellationToken);
            fieldInfoProjection = JetAuditProgram.ProjectFieldInfo(
                targetTbDefinitions,
                targetGlDefinitions);
        }
        else if (typedValidationWriter is not null)
        {
            reportProjection = ValidationReportProjectionParser.Parse(run.SummaryJson);
        }
        var requests = new ReportArtifactWriteRequest[]
        {
            new(
                plan.ArtifactKinds[0],
                plan.SourceRef,
                async (stream, ct) =>
                {
                    var progress = progressByKind[ReportArtifactKind.ValidationReport];
                    if (formalValidationWriter is not null
                        && validationReportPlan is not null
                        && fieldInfoProjection is not null)
                    {
                        await formalValidationWriter.WriteFormalPlannedAsync(
                            stream,
                            reportContext,
                            reportProjection!,
                            validationReportPlan,
                            fieldInfoProjection,
                            plan.WorkbookMetadata!,
                            document.OperatorId,
                            ct,
                            progress.WriterProgress);
                    }
                    else if (plannedValidationWriter is not null
                             && validationReportPlan is not null
                             && fieldInfoProjection is not null)
                    {
                        await plannedValidationWriter.WritePlannedAsync(
                            stream,
                            reportContext,
                            reportProjection!,
                            validationReportPlan,
                            fieldInfoProjection,
                            document.OperatorId,
                            ct,
                            progress.WriterProgress);
                    }
                    else if (typedValidationWriter is not null)
                    {
                        await typedValidationWriter.WriteTypedAsync(
                            stream,
                            reportContext,
                            reportProjection!,
                            document.OperatorId,
                            ct,
                            progress.WriterProgress);
                    }
                    else
                    {
                        // 外部自訂 writer 仍走既有 public contract；context 的 public
                        // constructor、deconstruction、equality 與 hash semantics 均未改。
                        await validationWriter.WriteAsync(
                            stream,
                            reportContext,
                            ct,
                            progress.WriterProgress);
                    }

                    progress.FinalizingWorkbook();
                }),
            new(
                plan.ArtifactKinds[1],
                plan.SourceRef,
                async (stream, ct) =>
                {
                    var progress = progressByKind[ReportArtifactKind.InfReport];
                    var infContext = new InfReportContext(project, run.RunId, run.GeneratedUtc);
                    if (infWriter is IFormalInfReportWriter formalWriter)
                    {
                        await formalWriter.WriteFormalAsync(
                            stream,
                            infContext,
                            plan.WorkbookMetadata!,
                            plan.CustomFields,
                            ct,
                            progress.WriterProgress);
                    }
                    else
                    {
                        await infWriter.WriteAsync(
                            stream,
                            infContext,
                            ct,
                            progress.WriterProgress);
                    }
                    progress.FinalizingWorkbook();
                })
        };

        var facts = await JetAuditProgram.ExecuteAsync(
            plan,
            new ReportArtifactExecutionPort(artifactStore, requests,
                kind => progressByKind[kind].PublishingArtifact()),
            cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);

        return new
        {
            ok = true,
            artifacts = result.Artifacts.Select(ReportExportSupport.ArtifactWire).ToArray()
        };
    }

}

public sealed class ExportPrescreenReportHandler(
    IPrescreenReportWriter writer,
    IRuleRunStore runStore,
    IProjectStore projectStore,
    IReportArtifactStore artifactStore,
    ProjectSession session,
    IJetEventPublisher eventPublisher) : IApplicationActionHandler
{
    private readonly IPrescreenReportPlanningFactsPort? _prescreenReportPlanningFactsPort;
    private readonly IMappingStateStore? _mappingStateStore;
    private readonly IAccountTaxonomyStore? _accountTaxonomyStore;

    internal ExportPrescreenReportHandler(
        IPrescreenReportWriter writer,
        IRuleRunStore runStore,
        IProjectStore projectStore,
        IReportArtifactStore artifactStore,
        ProjectSession session,
        IJetEventPublisher eventPublisher,
        IPrescreenReportPlanningFactsPort prescreenReportPlanningFactsPort)
        : this(
            writer,
            runStore,
            projectStore,
            artifactStore,
            session,
            eventPublisher)
    {
        _prescreenReportPlanningFactsPort = prescreenReportPlanningFactsPort
            ?? throw new ArgumentNullException(nameof(prescreenReportPlanningFactsPort));
    }

    internal ExportPrescreenReportHandler(
        IPrescreenReportWriter writer,
        IRuleRunStore runStore,
        IProjectStore projectStore,
        IReportArtifactStore artifactStore,
        ProjectSession session,
        IJetEventPublisher eventPublisher,
        IPrescreenReportPlanningFactsPort prescreenReportPlanningFactsPort,
        IMappingStateStore mappingStateStore,
        IAccountTaxonomyStore accountTaxonomyStore)
        : this(
            writer,
            runStore,
            projectStore,
            artifactStore,
            session,
            eventPublisher,
            prescreenReportPlanningFactsPort)
    {
        _mappingStateStore = mappingStateStore
            ?? throw new ArgumentNullException(nameof(mappingStateStore));
        _accountTaxonomyStore = accountTaxonomyStore
            ?? throw new ArgumentNullException(nameof(accountTaxonomyStore));
    }

    public string Action => "export.prescreenReport";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var runId = PayloadReader.GetOptionalString(payload, "runId")
            ?? throw new JetActionException(JetErrorCodes.InvalidPayload, "payload 缺少必填欄位 'runId'。");
        var projectId = session.RequireProjectId();
        await CompletenessEligibilitySupport.RequireCurrentAsync(
            runStore,
            projectId,
            cancellationToken);
        var progressSession = new ExportProgressSession(eventPublisher, cancellationToken);
        var artifactProgress = progressSession.Start(ReportArtifactKind.PrescreenReport);
        var run = await ReportExportSupport.RequireCurrentRunAsync(
            runStore, projectId, RuleRunKinds.Prescreen, runId, cancellationToken);
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");
        var formalWriter = writer as IFormalPlannedPrescreenReportWriter;
        if (formalWriter is not null
            && (_mappingStateStore is null || _accountTaxonomyStore is null))
        {
            throw new InvalidOperationException(
                "Formal prescreen report requires mapping and taxonomy metadata stores.");
        }
        ReportWorkbookMetadata? workbookMetadata = null;
        if (_mappingStateStore is not null && _accountTaxonomyStore is not null)
        {
            workbookMetadata = await ReportWorkbookMetadataFactory.LoadAsync(
                projectId,
                document,
                _mappingStateStore,
                _accountTaxonomyStore,
                cancellationToken);
        }
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
        var plannedWriter = writer as IPlannedPrescreenReportWriter;
        var typedWriter = writer as ITypedPrescreenReportWriter;
        var reportProjection = plannedWriter is null && typedWriter is null
            ? null
            : PrescreenReportProjectionParser.Parse(run.SummaryJson);
        PrescreenReportPlan? prescreenReportPlan = null;
        if (plannedWriter is not null
            && _prescreenReportPlanningFactsPort is not null)
        {
            var ruleContext = new FilterRuleContext(
                project.MoneyScale,
                project.LastPeriodStart,
                project.PeriodStart,
                project.PeriodEnd);
            var unfinalizedReportPlan = JetAuditProgram.Plan(
                new PrescreenReportRequest(
                    projectId,
                    ruleContext,
                    reportProjection!.PostPeriodApproval.NaReason,
                    reportProjection.SuspiciousKeywords.NaReason,
                    reportProjection.UnexpectedAccountPair.NaReason,
                    reportProjection.TrailingZeros.NaReason,
                    reportProjection.BlankDescription.NaReason));
            var planningFacts = await JetAuditProgram.ExecuteAsync(
                unfinalizedReportPlan,
                _prescreenReportPlanningFactsPort,
                cancellationToken);
            prescreenReportPlan = JetAuditProgram.Finalize(
                unfinalizedReportPlan,
                planningFacts);
        }

        var request = new ReportArtifactWriteRequest(
                plan.ArtifactKinds[0],
                plan.SourceRef,
                async (stream, ct) =>
                {
                    if (formalWriter is not null
                        && prescreenReportPlan is not null)
                    {
                        await formalWriter.WriteFormalPlannedAsync(
                            stream,
                            reportContext,
                            reportProjection!,
                            prescreenReportPlan,
                            plan.WorkbookMetadata!,
                            document.OperatorId,
                            ct,
                            artifactProgress.WriterProgress);
                    }
                    else if (plannedWriter is not null
                             && prescreenReportPlan is not null)
                    {
                        await plannedWriter.WritePlannedAsync(
                            stream,
                            reportContext,
                            reportProjection!,
                            prescreenReportPlan,
                            document.OperatorId,
                            ct,
                            artifactProgress.WriterProgress);
                    }
                    else if (typedWriter is not null)
                    {
                        await typedWriter.WriteTypedAsync(
                            stream,
                            reportContext,
                            reportProjection!,
                            document.OperatorId,
                            ct,
                            artifactProgress.WriterProgress);
                    }
                    else
                    {
                        // 外部自訂 writer 仍走既有 public contract；context 的 public
                        // constructor、deconstruction、equality 與 hash semantics 均未改。
                        await writer.WriteAsync(
                            stream,
                            reportContext,
                            ct,
                            artifactProgress.WriterProgress);
                    }

                    artifactProgress.FinalizingWorkbook();
                });
        var facts = await JetAuditProgram.ExecuteAsync(
            plan,
            new ReportArtifactExecutionPort(artifactStore, [request],
                _ => artifactProgress.PublishingArtifact()),
            cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);
        var artifact = result.Artifacts.Single();

        return new { ok = true, artifact = ReportExportSupport.ArtifactWire(artifact) };
    }
}

public sealed class ExportCriteriaSelectionReportHandler(
    ICriteriaSelectionReportWriter writer,
    IFilterScenarioStore scenarioStore,
    FilterRunMaterializeService materializeService,
    IRuleRunStore runStore,
    IProjectStore projectStore,
    IReportArtifactStore artifactStore,
    ProjectSession session,
    IJetEventPublisher eventPublisher,
    IAccountTaxonomyStore accountTaxonomyStore,
    IMappingStateStore mappingStore) : IApplicationActionHandler
{
    public string Action => "export.criteriaSelectionReport";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var validationRunId = PayloadReader.GetOptionalString(payload, "validationRunId")
            ?? throw new JetActionException(JetErrorCodes.InvalidPayload, "payload 缺少必填欄位 'validationRunId'。");
        var revision = PayloadReader.GetOptionalString(payload, "revision")
            ?? throw new JetActionException(JetErrorCodes.InvalidPayload, "payload 缺少必填欄位 'revision'。");
        var projectId = session.RequireProjectId();
        var validationRun = await CompletenessEligibilitySupport.RequireCurrentAsync(
            runStore,
            projectId,
            cancellationToken);
        ReportExportSupport.RequireRequestedValidationRun(validationRun, validationRunId);
        var progressSession = new ExportProgressSession(eventPublisher, cancellationToken);
        var artifactProgress = progressSession.Start(ReportArtifactKind.CriteriaSelectionReport);
        var scenarios = await scenarioStore.ListAsync(projectId, cancellationToken);
        var filterRevision = ReportExportSupport.RequireRevisionState(scenarios, revision);
        revision = filterRevision.Revision;
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        // filter.commit 會在同一交易清除上一版命中。正式 Criteria 報告必須先以
        // 目前 revision 的全部情境重新物化，writer 才能輸出可追溯的明細母體。
        await materializeService.MaterializeAllAsync(projectId, document, scenarios, cancellationToken);

        // 科目配對多選讀回要顯示目前 taxonomy 的顯示名稱，因此條件邏輯以專案 taxonomy 渲染。
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
                FilterConditionRenderer.Render(definition.RootElement, categoryLabels, rdeFieldLabels);
            var scenarioSpec = FilterScenarioPayloadParser.Parse(
                definition.RootElement,
                document.MoneyScale);
            if (JetAuditProgram.UsesLegacyWholeVoucherRows(scenarioSpec))
            {
                wholeVoucherSummaryPositions.Add(scenario.Position);
            }
        }

        var formalWriter = writer as IFormalCriteriaSelectionReportWriter;
        ReportWorkbookMetadata? workbookMetadata = null;
        IReadOnlyList<GlRdeFieldMetadata> customFields = [];
        if (formalWriter is not null)
        {
            workbookMetadata = await ReportWorkbookMetadataFactory.LoadAsync(
                projectId,
                document,
                mappingStore,
                accountTaxonomyStore,
                cancellationToken);
            customFields = FormalReportRdeFieldSelection.ForScenarios(
                scenarios,
                workbookMetadata,
                document.MoneyScale);
        }

        var plan = JetAuditProgram.Plan(new ReportExportRequest(
            Action,
            projectId,
            ValidationRunId: validationRun.RunId,
            ScenarioRevision: revision,
            ScenarioPositions: scenarios.Select(item => item.Position).ToArray(),
            WorkbookMetadata: workbookMetadata,
            CustomFields: customFields));
        var reportContext = new CriteriaSelectionReportContext(
            ReportExportSupport.ProjectContext(document),
            revision,
            scenarios[0].SavedUtc,
            scenarios,
            conditionLogic,
            filterRevision.PopulationScope,
            wholeVoucherSummaryPositions);
        var typedWriter = writer as ITypedCriteriaSelectionReportWriter;
        var request = new ReportArtifactWriteRequest(
                plan.ArtifactKinds[0],
                plan.SourceRef,
                async (stream, ct) =>
                {
                    if (formalWriter is not null)
                    {
                        await formalWriter.WriteFormalAsync(
                            stream,
                            reportContext,
                            plan.WorkbookMetadata!,
                            plan.CustomFields,
                            document.OperatorId,
                            ct,
                            artifactProgress.WriterProgress);
                    }
                    else if (typedWriter is not null)
                    {
                        await typedWriter.WriteTypedAsync(
                            stream,
                            reportContext,
                            document.OperatorId,
                            ct,
                            artifactProgress.WriterProgress);
                    }
                    else
                    {
                        await writer.WriteAsync(
                            stream,
                            reportContext,
                            ct,
                            artifactProgress.WriterProgress);
                    }

                    artifactProgress.FinalizingWorkbook();
                });
        var facts = await JetAuditProgram.ExecuteAsync(
            plan,
            new ReportArtifactExecutionPort(artifactStore, [request],
                _ => artifactProgress.PublishingArtifact()),
            cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);
        var artifact = result.Artifacts.Single();

        return new { ok = true, artifact = ReportExportSupport.ArtifactWire(artifact) };
    }
}
