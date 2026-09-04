using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// export.accountMappingTemplate：把 GL∪TB 科目母體寫成給審計員填分類的範本工作檔。固定檔名、直接寫進
/// 案件資料夾、每次覆蓋；它不是報告，不進報告清單，也不核對內容。審計員用 Excel 開它填 C 欄、存回
/// 原檔，再用 import.accountMapping.fromFile 匯回。
/// </summary>
public sealed class ExportAccountMappingTemplateHandler(
    IAccountMappingExportRepository repository,
    IAccountMappingTemplateWriter writer,
    IRuleRunStore runStore,
    IProjectStore projectStore,
    IProjectExportLocator projectLocator,
    ProjectSession session,
    IJetEventPublisher? eventPublisher = null,
    IAccountTaxonomyStore? taxonomyStore = null,
    IMappingStateStore? mappingStore = null) : IApplicationActionHandler
{
    private readonly IJetEventPublisher _eventPublisher = eventPublisher ?? new NullEventPublisher();

    public string Action => "export.accountMappingTemplate";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var runId = PayloadReader.GetOptionalString(payload, "runId")
            ?? throw new JetActionException(JetErrorCodes.InvalidPayload, "payload 缺少必填欄位 'runId'。");
        var projectId = session.RequireProjectId();
        var progressSession = new ExportProgressSession(_eventPublisher, cancellationToken);
        var progress = progressSession.Start(ReportArtifactKind.AccountMapping);
        var run = await ReportExportSupport.RequireCurrentRunAsync(
            runStore, projectId, RuleRunKinds.Validate, runId, cancellationToken);
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        var formalWriter = writer as IFormalAccountMappingTemplateWriter;
        if (formalWriter is not null && (taxonomyStore is null || mappingStore is null))
        {
            throw new InvalidOperationException(
                "Formal account mapping template requires mapping and taxonomy metadata stores.");
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
                "尚無可產生科目配對範本的 GL／TB 科目母體；先匯入 GL 與 TB 並執行資料驗證。");
        }

        var fileName = ProjectFileNames.AccountMappingTemplate(projectId);
        var filePath = await ProjectWorkFileWriter.WriteAsync(
            projectLocator,
            projectId,
            fileName,
            async (stream, ct) =>
            {
                if (formalWriter is not null)
                {
                    await formalWriter.WriteFormalAsync(
                        stream,
                        rows,
                        taxonomy.Categories,
                        workbookMetadata!,
                        ct,
                        progress.WriterProgress);
                }
                else if (writer is IAccountMappingTaxonomyTemplateWriter taxonomyWriter)
                {
                    await taxonomyWriter.WriteAsync(
                        stream,
                        rows,
                        taxonomy.Categories,
                        ct,
                        progress.WriterProgress);
                }
                else if (writer is IAccountMappingTemplateProgressWriter progressWriter)
                {
                    await progressWriter.WriteAsync(stream, rows, ct, progress.WriterProgress);
                }
                else
                {
                    await writer.WriteAsync(stream, rows, ct);
                }

                progress.FinalizingWorkbook();
            },
            cancellationToken);
        progress.PublishingArtifact();

        return new
        {
            ok = true,
            filePath,
            fileName,
            rowCount = rows.Count,
            validationRunId = run.RunId
        };
    }
}
