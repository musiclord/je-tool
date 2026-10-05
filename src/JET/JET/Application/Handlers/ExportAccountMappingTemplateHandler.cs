using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// export.accountMappingTemplate：把 GL∪TB 科目母體寫成給審計員填分類的範本工作檔。固定檔名、直接寫進
/// 案件資料夾、每次覆蓋；它不是報告，不進報告清單，也不核對內容。審計員用 Excel 開它填 C 欄、存回
/// 原檔，再用 import.accountMapping.fromFile 匯回。
/// </summary>
public sealed class ExportAccountMappingTemplateHandler(
    IAccountMappingTemplateWriter writer,
    IProjectStore projectStore,
    IProjectExportLocator projectLocator,
    ProjectSession session,
    IJetEventPublisher? eventPublisher = null) : IApplicationActionHandler
{
    private readonly IJetEventPublisher _eventPublisher = eventPublisher ?? new NullEventPublisher();

    public string Action => "export.accountMappingTemplate";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        // Older callers still send runId; this working file uses mapped accounts, not validation output.
        _ = PayloadReader.GetOptionalString(payload, "runId");
        var onlyIfMissing = false;
        if (payload.TryGetProperty("onlyIfMissing", out var mode))
        {
            if (mode.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new JetActionException(JetErrorCodes.InvalidPayload, "onlyIfMissing 必須是布林值。");
            }
            onlyIfMissing = mode.GetBoolean();
        }
        var (projectId, repositories) = session.RequireActive();
        var repository = repositories.AccountMappingExport;
        var runStore = repositories.RuleRuns;
        // 正式組裝的資料庫組一定有這兩個 store；直接建構 handler 的測試可以不放（維持 null），沿用內建分類。
        IAccountTaxonomyStore? taxonomyStore = repositories.AccountTaxonomy;
        IMappingStateStore? mappingStore = repositories.MappingStates;
        var progressSession = new ExportProgressSession(_eventPublisher, cancellationToken);
        var progress = progressSession.Start(ReportArtifactKind.AccountMapping);
        var run = await runStore.FindLatestAsync(projectId, RuleRunKinds.Validate, cancellationToken);
        if (!RuleLogicVersions.IsCurrent(run)) { run = null; }
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

        int? rowCount = null;
        var fileName = ProjectFileNames.AccountMappingTemplate(projectId);
        var result = await ProjectWorkFileWriter.WriteAsync(
            projectLocator,
            projectId,
            fileName,
            async (stream, ct) =>
            {
                var rows = await repository.FetchTemplateRowsAsync(
                    projectId, document.PeriodStart, document.PeriodEnd, ct);
                if (rows.Count == 0)
                {
                    throw new JetActionException(
                        JetErrorCodes.NoTargetData,
                        "尚無可產生配對檔的科目；請先匯入資料並確認欄位配對。");
                }
                rowCount = rows.Count;
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
            cancellationToken,
            progress.PublishingArtifact,
            onlyIfMissing);

        return new
        {
            ok = true,
            filePath = result.FilePath,
            fileName,
            rowCount = result.Created ? rowCount : null,
            disposition = result.Created ? "created" : "kept",
            validationRunId = run?.RunId
        };
    }
}
