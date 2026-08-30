using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>Validation batch 的 AccountMapping 單檔重試入口；只發布到目前專案 artifact store。</summary>
public sealed class ExportAccountMappingTemplateHandler(
    IAccountMappingExportRepository repository,
    IAccountMappingTemplateWriter writer,
    IRuleRunStore runStore,
    IProjectStore projectStore,
    IReportArtifactStore artifactStore,
    ProjectSession session,
    IJetEventPublisher eventPublisher) : IApplicationActionHandler
{
    private readonly IAccountTaxonomyStore? taxonomyStore;
    private readonly IMappingStateStore? mappingStore;

    internal ExportAccountMappingTemplateHandler(
        IAccountMappingExportRepository repository,
        IAccountMappingTemplateWriter writer,
        IRuleRunStore runStore,
        IProjectStore projectStore,
        IReportArtifactStore artifactStore,
        ProjectSession session,
        IJetEventPublisher eventPublisher,
        IAccountTaxonomyStore taxonomyStore)
        : this(repository, writer, runStore, projectStore, artifactStore, session, eventPublisher)
    {
        this.taxonomyStore = taxonomyStore ?? throw new ArgumentNullException(nameof(taxonomyStore));
    }

    internal ExportAccountMappingTemplateHandler(
        IAccountMappingExportRepository repository,
        IAccountMappingTemplateWriter writer,
        IRuleRunStore runStore,
        IProjectStore projectStore,
        IReportArtifactStore artifactStore,
        ProjectSession session,
        IJetEventPublisher eventPublisher,
        IAccountTaxonomyStore taxonomyStore,
        IMappingStateStore mappingStore)
        : this(
            repository,
            writer,
            runStore,
            projectStore,
            artifactStore,
            session,
            eventPublisher,
            taxonomyStore)
    {
        this.mappingStore = mappingStore ?? throw new ArgumentNullException(nameof(mappingStore));
    }

    public ExportAccountMappingTemplateHandler(
        IAccountMappingExportRepository repository,
        IAccountMappingTemplateWriter writer,
        IRuleRunStore runStore,
        IProjectStore projectStore,
        IReportArtifactStore artifactStore,
        ProjectSession session)
        : this(
            repository,
            writer,
            runStore,
            projectStore,
            artifactStore,
            session,
            new NullEventPublisher())
    {
    }

    public string Action => "export.accountMappingTemplate";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var runId = PayloadReader.GetOptionalString(payload, "runId")
            ?? throw new JetActionException(JetErrorCodes.InvalidPayload, "payload 缺少必填欄位 'runId'。");
        var projectId = session.RequireProjectId();
        var progressSession = new ExportProgressSession(eventPublisher, cancellationToken);
        var artifactProgress = progressSession.Start(ReportArtifactKind.AccountMapping);
        var run = await ReportExportSupport.RequireCurrentRunAsync(
            runStore, projectId, RuleRunKinds.Validate, runId, cancellationToken);
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");
        var formalWriter = writer as IFormalAccountMappingTemplateWriter;
        if (formalWriter is not null && (taxonomyStore is null || mappingStore is null))
        {
            throw new InvalidOperationException(
                "Formal account mapping export requires mapping and taxonomy metadata stores.");
        }
        var taxonomy = taxonomyStore is null
            ? AccountTaxonomyCatalog.BuiltInSnapshot
            : await taxonomyStore.ReadAsync(projectId, cancellationToken);
        ReportWorkbookMetadata? workbookMetadata = null;
        if (taxonomyStore is not null && mappingStore is not null)
        {
            workbookMetadata = await ReportWorkbookMetadataFactory.LoadAsync(
                projectId,
                document,
                mappingStore,
                taxonomyStore,
                cancellationToken);
        }

        var rows = await repository.FetchTemplateRowsAsync(
            projectId, document.PeriodStart, document.PeriodEnd, cancellationToken);
        if (rows.Count == 0)
        {
            throw new JetActionException(
                JetErrorCodes.NoTargetData,
                "尚無可產生科目配對報告的 GL／TB 科目母體。");
        }

        var plan = JetAuditProgram.Plan(new ReportExportRequest(
            Action,
            projectId,
            ValidationRunId: run.RunId,
            WorkbookMetadata: workbookMetadata));
        var request = new ReportArtifactWriteRequest(
            plan.ArtifactKinds[0],
            plan.SourceRef,
            async (stream, ct) =>
            {
                if (formalWriter is not null)
                {
                    await formalWriter.WriteFormalAsync(
                        stream,
                        rows,
                        taxonomy.Categories,
                        plan.WorkbookMetadata!,
                        ct,
                        artifactProgress.WriterProgress);
                }
                else if (writer is IAccountMappingTaxonomyTemplateWriter taxonomyWriter)
                {
                    await taxonomyWriter.WriteAsync(
                        stream,
                        rows,
                        taxonomy.Categories,
                        ct,
                        artifactProgress.WriterProgress);
                }
                else if (writer is IAccountMappingTemplateProgressWriter progressWriter)
                {
                    await progressWriter.WriteAsync(stream, rows, ct, artifactProgress.WriterProgress);
                }
                else
                {
                    await writer.WriteAsync(stream, rows, ct);
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

        return new
        {
            ok = true,
            artifact = ReportExportSupport.ArtifactWire(artifact),
            rowCount = rows.Count
        };
    }
}
