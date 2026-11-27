using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// import.authorizedPreparer.fromFile：匯入授權編製人員清單。
/// 只收 .xlsx；審計員要用 sourceColumn 指定清單裡和 GL 傳票建立人員意義相同的識別欄，程式不自己猜。
/// 匯入時直接轉成正式資料：匯入暫存表 pbc_authorized_preparer 與正式資料表 authorized_preparer 由 store 在同一個交易裡寫入。
/// 授權清單是整份替換的設定檔，只能整份取代；mode 不是 replace 就回 unsupported_mode。
/// </summary>
public sealed class ImportAuthorizedPreparerFromFileHandler : IApplicationActionHandler
{
    private readonly ITabularFileReader _reader;
    private readonly ProjectSession _session;
    private readonly ILogger? _logger;

    internal ImportAuthorizedPreparerFromFileHandler(
        ITabularFileReader reader,
        ProjectSession session,
        ILogger? logger = null)
    {
        this._reader = reader;
        this._session = session;
        this._logger = logger;
    }

    public string Action => "import.authorizedPreparer.fromFile";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = _session.RequireActive();

        var filePath = PayloadReader.GetRequiredString(payload, "filePath");
        var fileName = ImportSourceFileName.Resolve(
            filePath,
            PayloadReader.GetOptionalString(payload, "fileName"));

        var mode = PayloadReader.GetOptionalString(payload, "mode") ?? "replace";
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var plan = JetAuditProgram.Plan(
            new AuthorizedPreparerRequest(
                projectId,
                filePath,
                File.Exists(filePath),
                extension,
                mode,
                PayloadReader.GetOptionalString(payload, "sourceColumn")));

        // 有公式、卻沒有存計算結果的格數，匯入完成後寫進提醒。
        var request = TabularSourcePayload.Parse(payload, filePath)
            with { FormulasWithoutResult = new FormulaWithoutResultTally() };
        var source = new ImportSourceDescriptor(filePath, fileName, request.SheetName, null, null);
        // Excel 開著的檔案照樣匯入最後一次存檔的內容，匯入完成後提醒。
        var openElsewhereWarnings = ImportMessages.SourceFilesOpenElsewhere(_reader, [(filePath, fileName)]);

        var facts = await Task.Run(
            async () =>
            {
                var columns = await _reader.ReadColumnsAsync(request, cancellationToken);
                var rows = _reader.ReadRowsAsync(request, cancellationToken);
                var projection = JetAuditProgram.PrepareAuthorizedPreparerProjection(
                    columns,
                    plan.Request.SourceColumn);
                return await repositories.ReferenceDataFacts.ExecuteAsync(
                    plan,
                    source,
                    columns,
                    projection,
                    rows,
                    cancellationToken);
            },
            cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);
        string[] warnings =
        [
            .. openElsewhereWarnings,
            .. ImportMessages.FormulaWithoutResult(fileName, request.FormulasWithoutResult)
        ];

        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(projectId, repositories.Results, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportStore, plan.Effects, _logger);
        return new
        {
            batchId = result.Import.BatchId,
            rowCount = result.Import.RowCount,
            fileName = result.Import.FileName,
            importedUtc = result.Import.ImportedUtc,
            sourceColumn = result.Import.SourceColumn,
            sourceRowCount = result.Import.SourceRowCount,
            blankRowCount = result.Import.BlankRowCount,
            duplicateRowCount = result.Import.DuplicateRowCount,
            matchedPreparerCount = result.Import.MatchedPreparerCount,
            // 和其他匯入一樣：warnings 是全部提醒，開著的檔案那則另外列一份，畫面放在清單的匯入結果旁。
            warnings,
            openElsewhereWarnings,
            invalidatedResults = mutationState.InvalidatedResults,
            staleState = mutationState.StaleState,
            reports = mutationState.Reports,
            reportWarning = mutationState.ReportWarning
        };
    }
}

public sealed class ImportAuthorizedPreparerClearHandler(ProjectSession session, ILogger? logger = null)
    : IApplicationActionHandler
{
    public string Action => "import.authorizedPreparer.clear";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        await repositories.AuthorizedPreparers.ClearAsync(projectId, cancellationToken);
        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(
            projectId, repositories.Results, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportStore, AuditMutationEffects.For(AuditMutation.AuthorizedPreparer), logger);
        return new { cleared = true,
            invalidatedResults = mutationState.InvalidatedResults,
            staleState = mutationState.StaleState,
            reports = mutationState.Reports,
            reportWarning = mutationState.ReportWarning };
    }
}
