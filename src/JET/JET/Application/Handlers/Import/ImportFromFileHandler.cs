using System.Runtime.CompilerServices;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// import.gl.fromFile / import.tb.fromFile 的共同流程。
/// payload 只帶單一 filePath 或非空 sources（scale constraint：不帶 rows），
/// 檔案由 reader streaming 直入同一個 provider transaction。
/// </summary>
public abstract class ImportFromFileHandler : IApplicationActionHandler
{
    private readonly ITabularFileReader reader;
    private readonly IIntakeFactsPort intakeFactsPort;
    private readonly IProjectStore projectStore;
    private readonly ProjectSession session;
    private readonly IJetEventPublisher eventPublisher;
    private readonly IImportRepository? auditReadRepository;
    private readonly IProjectAuditLog auditLog;

    internal ImportFromFileHandler(
        ITabularFileReader reader,
        IIntakeFactsPort intakeFactsPort,
        IProjectStore projectStore,
        ProjectSession session,
        IJetEventPublisher eventPublisher,
        IImportRepository? auditReadRepository = null,
        IProjectAuditLog? auditLog = null)
    {
        this.reader = reader;
        this.intakeFactsPort = intakeFactsPort;
        this.projectStore = projectStore;
        this.session = session;
        this.eventPublisher = eventPublisher;
        this.auditReadRepository = auditReadRepository;
        this.auditLog = auditLog ?? NullProjectAuditLog.Instance;
    }

    public abstract string Action { get; }

    protected abstract DatasetKind Kind { get; }

    /// <summary>import.progress 事件節奏（manifest 事件章節）：每讀滿 20,000 列推播一次。</summary>
    internal const int ProgressRowInterval = 20_000;

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();
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
        var replacesExistingBatch = plan.Operation == IntakeOperation.Replace
            && auditReadRepository is not null
            && await auditReadRepository.GetLatestBatchAsync(projectId, Kind, cancellationToken) is not null;

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

        // 檔案讀取 + bulk insert 移出 UI thread，避免匯入期間介面凍結
        var facts = await Task.Run(
            async () =>
            {
                // 所有 bounded 標頭先讀完，才把任何 row stream 交給 repository；因此後來源的
                // 標頭／選項失敗不會讓 provider 白做前來源的 staging 寫入。
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
                        item.Request.Delimiter?.ToString());

                    // 串流途中推播 import.progress（完成以本 action 的 response 為準，無完成事件）；
                    // sourceNo/sourceCount 是本 action 內的位置，不是批次累積的 source ordinal。
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
                        cancellationToken);
                    inputs.Add(new ImportSourceInput(descriptor, columnsBySource[index], rows));
                }

                return await JetAuditProgram.ExecuteAsync(
                    plan,
                    intakeFactsPort,
                    inputs,
                    cancellationToken);
            },
            cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);

        // repository 已完成 transaction commit 後即跨過取消邊界；必要的 workflow metadata
        // 必須完成，不能因晚到的取消把已匯入資料留在舊步驟。
        await AdvanceStepAsync(projectId, minimumStep: 2, CancellationToken.None);

        var batch = result.Data.Batch;

        if (replacesExistingBatch)
        {
            await auditLog.AppendAsync(
                projectId,
                ProjectAuditEvent.Create(
                    ProjectAuditOperations.DataReimport,
                    ProjectAuditTargetTypes.Dataset,
                    Kind.ToStorageName(),
                    batch.RowCount,
                    replacedCount: 1),
                CancellationToken.None);
        }

        return new
        {
            batchId = batch.BatchId,
            rowCount = batch.RowCount,
            addedRowCount = result.Data.AddedRowCount,
            columns = batch.Columns,
            sources = ImportStateShapes.ToSourceList(batch.Sources)
        };
    }

    /// <summary>每滿 interval 列回報一次累計列數（public static 以利直測節奏，不經 WebView）。</summary>
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
                        "匯入進度通知失敗，資料尚未完成匯入。請重試；若仍失敗，匯出支援日誌。", innerException: error);
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
            ? $"第 {row} 列、第 {column} 欄的儲存格無法轉換，請檢查型別或數值範圍後重試。"
            : error is System.Xml.XmlException
                ? "工作表內容無法解析，請用 Excel 修復或另存來源檔後重試。"
                : "讀取失敗，請確認檔案可讀及編碼設定後重試。";
        return new(
            JetErrorCodes.FileReadError,
            $"{SourceContext(source, sourceNo, sourceCount)}：{guidance}匯入尚未完成；仍失敗時可匯出支援日誌。", innerException: error);
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
            ? $"來源 {sourceNo}/{sourceCount}，檔案 '{source.FileName}'"
            : $"來源 {sourceNo}/{sourceCount}，檔案 '{source.FileName}'，工作表 '{source.SheetName}'";

    private async Task AdvanceStepAsync(string projectId, int minimumStep, CancellationToken cancellationToken)
    {
        var document = await projectStore.FindAsync(projectId, cancellationToken);
        if (document is not null && document.CurrentStep < minimumStep)
        {
            await projectStore.SaveAsync(document with { CurrentStep = minimumStep }, cancellationToken);
        }
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
                || payload.TryGetProperty("delimiter", out _));

        if (hasFilePath == hasSources || (hasSources && hasRootSourceOptions))
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "payload 必須且只能提供根層 'filePath' 或非空 'sources' 陣列其中一種。");
        }

        var mode = PayloadReader.GetOptionalString(payload, "mode") ?? "replace";
        if (hasFilePath)
        {
            return new ImportFromFilePayload(mode, [ParseSource(payload)]);
        }

        if (sourcesElement.ValueKind != JsonValueKind.Array || sourcesElement.GetArrayLength() == 0)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "欄位 'sources' 必須是非空陣列。");
        }

        var sources = new List<ImportFileSourcePayload>(sourcesElement.GetArrayLength());
        foreach (var source in sourcesElement.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object)
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidPayload,
                    "欄位 'sources' 的每個元素都必須是 object。");
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
        IIntakeFactsPort intakeFactsPort,
        IProjectStore projectStore,
        ProjectSession session,
        IJetEventPublisher eventPublisher,
        IImportRepository? auditReadRepository = null,
        IProjectAuditLog? auditLog = null)
        : base(reader, intakeFactsPort, projectStore, session, eventPublisher, auditReadRepository, auditLog)
    {
    }

    public override string Action => "import.gl.fromFile";

    protected override DatasetKind Kind => DatasetKind.Gl;
}

public sealed class ImportTbFromFileHandler : ImportFromFileHandler
{
    internal ImportTbFromFileHandler(
        ITabularFileReader reader,
        IIntakeFactsPort intakeFactsPort,
        IProjectStore projectStore,
        ProjectSession session,
        IJetEventPublisher eventPublisher,
        IImportRepository? auditReadRepository = null,
        IProjectAuditLog? auditLog = null)
        : base(reader, intakeFactsPort, projectStore, session, eventPublisher, auditReadRepository, auditLog)
    {
    }

    public override string Action => "import.tb.fromFile";

    protected override DatasetKind Kind => DatasetKind.Tb;
}
