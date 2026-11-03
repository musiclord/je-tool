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
        // 前端照舊附上目前驗證結果的 resultId；這份工作檔讀的是已配對的科目，不讀驗證結果，所以讀了不用。
        _ = PayloadReader.GetOptionalString(payload, "resultId");
        var onlyIfMissing = false;
        if (payload.TryGetProperty("onlyIfMissing", out var mode))
        {
            if (mode.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "only_if_missing_not_boolean");
            }
            onlyIfMissing = mode.GetBoolean();
        }
        var (projectId, repositories) = session.RequireActive();
        var repository = repositories.AccountMappingExport;
        var resultStore = repositories.Results;
        // 正式組裝的資料庫組一定有這兩個 store；直接建構 handler 的測試可以不放（維持 null），沿用內建分類。
        IAccountCategoryStore? categoryStore = repositories.AccountCategories;
        IMappingStateStore? mappingStore = repositories.MappingStates;
        var progressSession = new ExportProgressSession(_eventPublisher, cancellationToken);
        var progress = progressSession.Start(ReportKind.AccountMapping);
        SupportOperationStage.Enter("run_read");
        var run = await resultStore.FindLatestAsync(projectId, ResultKind.Validation, cancellationToken);
        if (!RuleLogicVersions.IsCurrent(run)) { run = null; }
        SupportOperationStage.Enter("metadata_read");
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);

        var formalWriter = writer as IFormalAccountMappingTemplateWriter;
        if (formalWriter is not null && (categoryStore is null || mappingStore is null))
        {
            throw new InvalidOperationException(
                "Formal account mapping template requires mapping and account category metadata stores.");
        }

        var categorySnapshot = categoryStore is null
            ? AccountCategoryCatalog.BuiltInSnapshot
            : await categoryStore.ReadAsync(projectId, cancellationToken);
        ReportWorkbookMetadata? workbookMetadata = null;
        if (categoryStore is not null && mappingStore is not null)
        {
            workbookMetadata = await ReportWorkbookMetadataFactory.LoadAsync(
                projectId,
                document,
                mappingStore,
                categoryStore,
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
                        JetErrorCodes.GlMappingNotConfirmed,
                        "尚無可產生配對檔的科目；請先匯入資料並確認欄位配對。");
                }
                rowCount = rows.Count;
                if (formalWriter is not null)
                {
                    await formalWriter.WriteFormalAsync(
                        stream,
                        rows,
                        categorySnapshot.Categories,
                        workbookMetadata!,
                        ct,
                        progress.WriterProgress);
                }
                else if (writer is IAccountMappingCategoryTemplateWriter categoryWriter)
                {
                    await categoryWriter.WriteAsync(
                        stream,
                        rows,
                        categorySnapshot.Categories,
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
            progress.PublishingReport,
            onlyIfMissing);

        return new
        {
            ok = true,
            filePath = result.FilePath,
            fileName,
            rowCount = result.Created ? rowCount : null,
            disposition = result.Created ? "created" : "kept",
            validationResultId = run?.ResultId
        };
    }
}
