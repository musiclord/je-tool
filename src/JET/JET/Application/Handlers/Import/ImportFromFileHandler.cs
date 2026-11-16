using System.Runtime.CompilerServices;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// import.gl.fromFile 與 import.tb.fromFile 的共同流程。
/// payload 只帶一個 filePath，或一份至少有一個來源的 sources；資料量可能很大，所以 payload 不帶資料列。
/// reader 邊讀檔邊把資料列寫進資料庫，全部來源都在同一個資料庫交易裡。
/// </summary>
public abstract class ImportFromFileHandler : IApplicationActionHandler
{
    private readonly ITabularFileReader reader;
    private readonly IProjectStore projectStore;
    private readonly ProjectSession session;
    private readonly IJetEventPublisher eventPublisher;
    private readonly ILogger? logger;

    internal ImportFromFileHandler(
        ITabularFileReader reader,
        IProjectStore projectStore,
        ProjectSession session,
        IJetEventPublisher eventPublisher, ILogger? logger = null)
    {
        this.reader = reader;
        this.projectStore = projectStore;
        this.session = session;
        this.eventPublisher = eventPublisher;
        this.logger = logger;
    }

    public abstract string Action { get; }

    /// <summary>匯入成功後，案件至少推進到第二步。推進只增不減，由 <see cref="AdvanceStepAsync"/> 決定要不要存檔。</summary>
    internal const int WorkflowStep = 2;

    protected abstract DatasetKind Kind { get; }

    /// <summary>import.progress 事件節奏：每讀滿 20,000 列推播一次。</summary>
    internal const int ProgressRowInterval = 20_000;

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var importPayload = ImportFromFilePayload.Parse(payload);
        var sourceCount = importPayload.Sources.Count;
        var plan = JetAuditProgram.Plan(
            new IntakeRequest(
                Action,
                projectId,
                Kind,
                importPayload.Sources.Select(source =>
                {
                    var fileExists = File.Exists(source.FilePath);
                    return new IntakeSourceRequest(
                        source.FilePath,
                        fileExists,
                        fileExists && reader.Supports(source.FilePath));
                }).ToList(),
                importPayload.Mode));
        var previous = await repositories.Imports.GetLatestBatchAsync(projectId, Kind, cancellationToken);
        var replacesExistingBatch = plan.Operation == IntakeOperation.Replace && previous is not null;

        var sourceRequests = new List<(ImportFileSourcePayload Payload, TabularSourceRequest Request)>(sourceCount);
        for (var index = 0; index < sourceCount; index++)
        {
            var sourcePayload = importPayload.Sources[index];
            try
            {
                sourceRequests.Add((sourcePayload, TabularSourcePayload.Parse(
                    sourcePayload.Payload,
                    sourcePayload.FilePath)));
            }
            catch (JetActionException error)
            {
                ImportFailureDiagnostics.Attach(error, Context(ImportFailureStage.Options, sourceRequests.Count + 1, sourceCount, sourcePayload.FilePath));
                throw AddSourceContext(error, sourcePayload, index + 1, sourceCount);
            }
        }

        // 每個來源各記一份「欄位內含換行」的筆數與前幾個列號，匯入完成後寫進提醒與支援日誌。
        var lineBreakTallies = sourceRequests.Select(static _ => new QuotedLineBreakTally()).ToArray();
        // Excel 開著的來源檔照樣匯入最後一次存檔的內容，匯入完成後提醒。
        var openElsewhereWarnings = ImportMessages.SourceFilesOpenElsewhere(
            reader,
            sourceRequests.Select(item => (item.Payload.FilePath, item.Payload.FileName)));

        // 讀檔與大量寫入都移到 UI thread 以外執行，匯入期間畫面才不會凍結。
        var facts = await Task.Run(
            async () =>
            {
                // 先把每個來源的標頭都讀完，才把資料列交給 repository；讀標頭要讀的內容有上限，不會讀完整個檔。
                // 這樣後面的來源標頭或選項有問題時，資料庫不會白白寫入前面來源的匯入暫存表。
                var columnsBySource = new List<IReadOnlyList<string>>(sourceCount);
                for (var index = 0; index < sourceCount; index++)
                {
                    var item = sourceRequests[index];
                    try
                    {
                        columnsBySource.Add(await reader.ReadColumnsAsync(item.Request, cancellationToken));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (JetActionException error)
                    {
                        ImportFailureDiagnostics.Attach(error, Context(ImportFailureStage.Header, index + 1, sourceCount, item.Payload.FilePath, item.Request));
                        throw AddSourceContext(error, item.Payload, index + 1, sourceCount);
                    }
                    catch (Exception error)
                    {
                        ImportFailureDiagnostics.Attach(error, Context(ImportFailureStage.Header, index + 1, sourceCount, item.Payload.FilePath, item.Request));
                        throw FileReadErrorWithSourceContext(error, item.Payload, index + 1, sourceCount);
                    }
                }

                var inputs = new List<ImportSourceInput>(sourceCount);
                for (var index = 0; index < sourceCount; index++)
                {
                    var sourceNo = index + 1;
                    var item = sourceRequests[index];
                    var descriptor = new ImportSourceDescriptor(
                        item.Payload.FilePath,
                        item.Payload.FileName,
                        item.Request.SheetName,
                        item.Request.EncodingName,
                        item.Request.Delimiter?.ToString(),
                        // 欄名在第幾列：只有審計員指定第 2 列以後才記下來；第 1 列就是預設的讀法，不另外記。
                        HeaderRow: item.Request.LeadingRowsToSkip > 0 ? item.Request.LeadingRowsToSkip + 1 : null);

                    // 讀檔途中推播 import.progress；沒有完成事件，是否完成看這個 action 的回應。
                    // sourceNo 與 sourceCount 是這次 action 裡的第幾個來源與來源總數，不是這一批匯入累計的來源序號。
                    var rows = WithSourceContext(
                        WithProgress(
                            reader.ReadRowsAsync(item.Request, cancellationToken),
                            ProgressRowInterval,
                            rowsRead => eventPublisher.Publish("import.progress", new
                            {
                                kind = Kind.ToStorageName(),
                                sourceNo,
                                sourceCount,
                                fileName = item.Payload.FileName,
                                sheetName = item.Request.SheetName,
                                rowsRead
                            }),
                            cancellationToken),
                        item.Payload,
                        sourceNo,
                        sourceCount,
                        item.Request,
                        lineBreakTallies[index],
                        cancellationToken);
                    inputs.Add(new ImportSourceInput(descriptor, columnsBySource[index], rows));
                }

                return await repositories.IntakeFacts.ExecuteAsync(
                    plan,
                    inputs,
                    cancellationToken);
            },
            cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);

        // 資料庫交易已經提交，之後不再理會呼叫端的取消要求：案件的流程步驟一定要更新，
        // 不能因為晚到的取消，讓資料已經匯入而案件還停在舊的步驟。
        await AdvanceStepAsync(projectId, WorkflowStep, CancellationToken.None);

        var batch = result.Data.Batch;
        string? operationLogWarning = null;
        if (replacesExistingBatch)
        {
            operationLogWarning = await CommittedOperationLog.AppendAsync(
                repositories.ProjectOperationLog,
                projectId,
                ProjectOperationEvent.Create(
                    ProjectOperations.DataReimport,
                    ProjectOperationTargetTypes.Dataset,
                    Kind.ToStorageName(),
                    batch.RowCount,
                    replacedCount: 1),
                logger);
        }

        string[] warnings =
        [
            .. openElsewhereWarnings,
            .. DuplicateSourceWarnings(plan.Operation, previous, batch),
            .. QuotedLineBreakWarnings(sourceRequests, lineBreakTallies),
            .. operationLogWarning is null ? Array.Empty<string>() : [operationLogWarning]
        ];

        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(projectId, repositories.Results, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportStore, plan.Effects, logger);
        return new
        {
            batchId = batch.BatchId,
            rowCount = batch.RowCount,
            addedRowCount = result.Data.AddedRowCount,
            columns = batch.Columns,
            sources = ImportStateShapes.ToSourceList(batch.Sources),
            // warnings 是這次全部的提醒，畫面記進訊息面板；開著的來源檔那幾則另外列一份，
            // 畫面放在匯入結果旁，審計員不必打開訊息面板就看得到。
            warnings,
            openElsewhereWarnings,
            invalidatedResults = mutationState.InvalidatedResults,
            staleState = mutationState.StaleState,
            reports = mutationState.Reports,
            reportWarning = mutationState.ReportWarning
        };
    }

    /// <summary>每個有引號內換行的來源各寫一則提醒與一筆支援日誌，統計與句子見 <see cref="QuotedLineBreakTally"/>。</summary>
    private string[] QuotedLineBreakWarnings(
        IReadOnlyList<(ImportFileSourcePayload Payload, TabularSourceRequest Request)> sources,
        IReadOnlyList<QuotedLineBreakTally> tallies)
    {
        var warnings = new List<string>();
        for (var index = 0; index < tallies.Count; index++)
        {
            var warning = tallies[index].Warning(sources[index].Payload.FileName, index + 1, logger);
            if (warning is not null)
            {
                warnings.Add(warning);
            }
        }

        return [.. warnings];
    }

    private static string[] DuplicateSourceWarnings(IntakeOperation operation, ImportBatchInfo? previous, ImportBatchInfo current)
    {
        if (operation != IntakeOperation.Append || previous is null || previous.Sources.Count == 0) return [];
        var lastSourceNo = previous.Sources.Max(source => source.SourceNo);
        var existing = previous.Sources.Select(source => (source.FileName, source.SheetName, source.RowCount)).ToHashSet();
        return current.Sources.Where(source => source.SourceNo > lastSourceNo
                && existing.Contains((source.FileName, source.SheetName, source.RowCount)))
            .Select(source =>
            {
                var label = source.SheetName is null ? $"「{source.FileName}」" : $"「{source.FileName}」的工作表「{source.SheetName}」";
                // 寫實際比對的結果，不寫「可能」。比對的是來源檔的列數，
                // 來源檔用列，分錄用筆，所以寫成「列數」。
                return $"{label}與已匯入的來源檔名、工作表及列數皆相同，已照常加入；請確認是否重複匯入。";
            }).ToArray();
    }

    /// <summary>每讀滿 interval 列，回報一次累計列數。宣告成 public static，不經 WebView 也能直接呼叫來核對回報的間隔。</summary>
    public static async IAsyncEnumerable<StagingRow> WithProgress(
        IAsyncEnumerable<StagingRow> rows,
        int interval,
        Action<int> report,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var count = 0;

        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            yield return row;
            count++;

            if (count % interval == 0)
            {
                try { report(count); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    ImportFailureDiagnostics.Attach(error, new ImportFailureContext(ImportFailureStage.Progress,
                        LastCompletedRow: row.SourceRowNumber));
                    throw new JetActionException(JetErrorCodes.ImportProgressFailed,
                        "匯入過程發生錯誤，本次匯入未完成，請重新匯入。", innerException: error);
                }
            }
        }
    }

    private static async IAsyncEnumerable<StagingRow> WithSourceContext(
        IAsyncEnumerable<StagingRow> rows,
        ImportFileSourcePayload source,
        int sourceNo,
        int sourceCount,
        TabularSourceRequest request,
        QuotedLineBreakTally lineBreaks,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var enumerator = rows.GetAsyncEnumerator(cancellationToken);
        long? lastCompletedRow = null;
        while (true)
        {
            StagingRow current;
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync();
                current = hasNext ? enumerator.Current : null!;
            }
            catch (Exception error)
            {
                ImportFailureDiagnostics.Attach(error, Context(ImportFailureStage.Rows, sourceNo, sourceCount,
                    source.FilePath, request) with { LastCompletedRow = lastCompletedRow });
                if (error is OperationCanceledException) throw;
                if (error is JetActionException actionError)
                    throw AddSourceContext(actionError, source, sourceNo, sourceCount);
                throw FileReadErrorWithSourceContext(error, source, sourceNo, sourceCount);
            }

            if (!hasNext)
            {
                yield break;
            }

            lastCompletedRow = current.SourceRowNumber;
            lineBreaks.Observe(current);
            yield return current;
        }
    }

    private static JetActionException AddSourceContext(
        JetActionException error,
        ImportFileSourcePayload source,
        int sourceNo,
        int sourceCount)
    {
        return new JetActionException(error.Code,
            HasSourceContext(error.Message, source) ? error.Message : $"{SourceContext(source, sourceNo, sourceCount)}：{error.Message}",
            error.Field, error) { Details = error.Details };
    }

    private static JetActionException FileReadErrorWithSourceContext(
        Exception error,
        ImportFileSourcePayload source,
        int sourceNo,
        int sourceCount)
    {
        var context = ImportFailureDiagnostics.Find(error);
        var guidance = context is { Stage: ImportFailureStage.CellConversion, Row: { } row, Column: { } column }
            ? $"第 {row} 列、第 {column} 欄的儲存格內容無法讀取，請在 Excel 確認該儲存格的格式與數值後重試。"
            : error is System.Xml.XmlException
                ? "工作表內容無法解析，請用 Excel 修復或另存來源檔後重試。"
                : "讀取失敗，請確認檔案可讀及編碼設定後重試。";
        return new(
            JetErrorCodes.FileReadError,
            $"{SourceContext(source, sourceNo, sourceCount)}：{guidance}本次匯入未完成。", innerException: error);
    }

    private static ImportFailureContext Context(ImportFailureStage stage, int sourceNo, int sourceCount,
        string path, TabularSourceRequest? request = null) => new(stage, sourceNo, sourceCount,
            Format: Path.GetExtension(path).ToLowerInvariant(), Encoding: request?.EncodingName,
            Delimiter: request?.Delimiter, ReaderVersion: typeof(ImportFromFileHandler).Assembly.GetName().Version?.ToString());

    private static bool HasSourceContext(string message, ImportFileSourcePayload source) =>
        message.Contains(source.FileName, StringComparison.Ordinal)
        && (source.SheetName is null || message.Contains(source.SheetName, StringComparison.Ordinal));

    private static string SourceContext(ImportFileSourcePayload source, int sourceNo, int sourceCount) =>
        source.SheetName is null
            ? $"來源 {sourceNo}/{sourceCount}，檔案「{source.FileName}」"
            : $"來源 {sourceNo}/{sourceCount}，檔案「{source.FileName}」，工作表「{source.SheetName}」";

    private async Task AdvanceStepAsync(string projectId, int minimumStep, CancellationToken cancellationToken)
    {
        await MappingCommitShared.AfterCommitAsync(async () =>
        {
            var document = await projectStore.FindAsync(projectId, cancellationToken);
            if (document is not null && document.CurrentStep < minimumStep)
                await projectStore.SaveAsync(document with { CurrentStep = minimumStep }, cancellationToken);
        }, logger);
    }
}

internal sealed record ImportFileSourcePayload(
    JsonElement Payload,
    string FilePath,
    string FileName,
    string? SheetName);

internal sealed record ImportFromFilePayload(
    string Mode,
    IReadOnlyList<ImportFileSourcePayload> Sources)
{
    internal static ImportFromFilePayload Parse(JsonElement payload)
    {
        var hasFilePath = payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("filePath", out _);
        var sourcesElement = default(JsonElement);
        var hasSources = payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("sources", out sourcesElement);
        var hasRootSourceOptions = payload.ValueKind == JsonValueKind.Object
            && (payload.TryGetProperty("fileName", out _)
                || payload.TryGetProperty("sheetName", out _)
                || payload.TryGetProperty("encoding", out _)
                || payload.TryGetProperty("delimiter", out _)
                || payload.TryGetProperty("headerRow", out _));

        if (hasFilePath == hasSources || (hasSources && hasRootSourceOptions))
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "import_file_path_or_sources");
        }

        var mode = PayloadReader.GetOptionalString(payload, "mode") ?? "replace";
        if (hasFilePath)
        {
            return new ImportFromFilePayload(mode, [ParseSource(payload)]);
        }

        if (sourcesElement.ValueKind != JsonValueKind.Array || sourcesElement.GetArrayLength() == 0)
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "import_sources_empty");
        }

        var sources = new List<ImportFileSourcePayload>(sourcesElement.GetArrayLength());
        foreach (var source in sourcesElement.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object)
            {
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "import_source_not_object");
            }

            sources.Add(ParseSource(source));
        }

        return new ImportFromFilePayload(mode, sources);
    }

    private static ImportFileSourcePayload ParseSource(JsonElement payload)
    {
        var filePath = PayloadReader.GetRequiredString(payload, "filePath");
        var fileName = ImportSourceFileName.Resolve(
            filePath,
            PayloadReader.GetOptionalString(payload, "fileName"));
        return new ImportFileSourcePayload(
            payload.Clone(),
            filePath,
            fileName,
            PayloadReader.GetOptionalString(payload, "sheetName"));
    }
}

public sealed class ImportGlFromFileHandler : ImportFromFileHandler
{
    internal ImportGlFromFileHandler(
        ITabularFileReader reader,
        IProjectStore projectStore,
        ProjectSession session,
        IJetEventPublisher eventPublisher, ILogger? logger = null)
        : base(reader, projectStore, session, eventPublisher, logger)
    {
    }

    public override string Action => "import.gl.fromFile";

    protected override DatasetKind Kind => DatasetKind.Gl;
}

public sealed class ImportTbFromFileHandler : ImportFromFileHandler
{
    internal ImportTbFromFileHandler(
        ITabularFileReader reader,
        IProjectStore projectStore,
        ProjectSession session,
        IJetEventPublisher eventPublisher, ILogger? logger = null)
        : base(reader, projectStore, session, eventPublisher, logger)
    {
    }

    public override string Action => "import.tb.fromFile";

    protected override DatasetKind Kind => DatasetKind.Tb;
}
