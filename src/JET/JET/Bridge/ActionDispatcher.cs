using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using JET.Application;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Bridge;

/// <summary>
/// 依 action 名稱找到對應的 handler 並執行。會改資料的 action 同一時間只讓一個進行：
/// 已有一個在進行時，下一個直接回 operation_in_progress，不排隊。哪些 action 算會改資料，
/// 由 <see cref="ActionExecutionPolicy"/> 決定。每次執行的開始、結束、失敗與取消都記進診斷日誌。
/// </summary>
public sealed class ActionDispatcher
{
    private readonly IReadOnlyDictionary<string, IApplicationActionHandler> _handlers;
    private readonly ILogger<ActionDispatcher> _logger;
    private readonly ProjectSession _session;
    private readonly Func<Exception, JetActionException?>? _engineErrorTranslator;
    private readonly ActionExecutionGate _executionGate;
    private readonly IProjectDatabaseCheckpoint? _databaseCheckpoint;
    private readonly IOperationMonitor? _operationMonitor;
    private readonly IProcessingLog? _processingLog;
    // 同一案件同一動作上一次失敗或取消的操作識別碼，下一次開始時記成 retry_of；成功後清除。
    private readonly ConcurrentDictionary<string, string> _lastUnsuccessful = new(StringComparer.Ordinal);

    public ActionDispatcher(
        IEnumerable<IApplicationActionHandler> handlers,
        ILogger<ActionDispatcher> logger,
        ProjectSession session,
        // 引擎錯誤的單一映射點：composition 注入 Infrastructure 的轉譯 delegate
        // （LocalEngineErrors.TryTranslate），Bridge 不直接依賴 Infrastructure（維持分層依賴方向）。
        Func<Exception, JetActionException?>? engineErrorTranslator = null,
        RequestCancellationRegistry? cancellationRegistry = null,
        ActionExecutionGate? executionGate = null,
        // 變更型作業結束後把當前案件資料庫的記錄檔併回；null 時不併回，自行組裝 dispatcher 的測試不受影響。
        IProjectDatabaseCheckpoint? databaseCheckpoint = null,
        // 操作監看模組：只記錄步驟、SQL 與花掉的時間，不改變執行與結果；null 時不監看。
        IOperationMonitor? operationMonitor = null,
        // 處理紀錄模組：操作成功後把處理經過寫給審計員看，不改變執行與結果；null 時不寫。
        IProcessingLog? processingLog = null)
    {
        var map = new Dictionary<string, IApplicationActionHandler>(StringComparer.Ordinal);

        foreach (var handler in handlers)
        {
            if (!map.TryAdd(handler.Action, handler))
            {
                throw new InvalidOperationException($"Duplicate JET action handler: {handler.Action}");
            }
        }

        _handlers = new ReadOnlyDictionary<string, IApplicationActionHandler>(map);
        _logger = logger;
        _session = session;
        _engineErrorTranslator = engineErrorTranslator;
        CancellationRegistry = cancellationRegistry ?? new RequestCancellationRegistry();
        _executionGate = executionGate ?? new ActionExecutionGate();
        _databaseCheckpoint = databaseCheckpoint;
        _operationMonitor = operationMonitor;
        _processingLog = processingLog;
    }

    public IReadOnlyCollection<string> RegisteredActions => _handlers.Keys.ToArray();

    public RequestCancellationRegistry CancellationRegistry { get; }

    /// <summary>
    /// 視窗關閉前，用會改資料的 action 共用的那一把閘確認沒有作業在進行；取不到就回 null，不等待。
    /// 呼叫端要一直持有取得的閘，直到 runtime 不再接受新的請求、並開始等待進行中的請求結束為止，
    /// 這樣直接關閉視窗也繞不過 dispatcher 和 handler 共用的閘。
    /// </summary>
    internal IDisposable? TryAcquireExecutionLease() => _executionGate.TryAcquire();

    /// <summary>
    /// 執行一個 action。呼叫端沒有帶 correlation_id 時產生一個，並用 <see cref="ILogger.BeginScope"/> 開一個 scope；
    /// 同一個 LoggerFactory 建出來的 handler 與 repository logger，在這次非同步流程裡寫的日誌都會自動帶上它（靠 AsyncLocal）。
    /// 日誌記錄 action 的開始、結束、失敗與取消，以及 duration_ms、result_status。Debug 的日誌保存完整診斷內容，
    /// Release 只收去識別過、列在允許清單裡的支援事件。開始時另記輸入結構與重試對象，成功時記結果版本，
    /// 失敗或取消時把 handler 最後進入的步驟記到例外上。
    /// </summary>
    public async Task<object?> DispatchAsync(
        string action,
        JsonElement payload,
        CancellationToken cancellationToken,
        string? correlationId = null)
    {
        correlationId = string.IsNullOrWhiteSpace(correlationId)
            ? Guid.NewGuid().ToString("N")
            : correlationId;
        var supportProjectId = TryReadSupportProjectId(payload);
        var currentProjectId = _session.CurrentProjectId;
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["correlation_id"] = correlationId,
            ["action"] = action,
            ["project_id"] = currentProjectId,
            ["support_project_id"] = supportProjectId,
        });

        var retryKey = (supportProjectId ?? currentProjectId ?? "") + "\n" + action;
        var stage = SupportOperationStage.Begin();
        var stopwatch = Stopwatch.StartNew();
        var startFields = new Dictionary<string, object?> { ["payload_shape"] = SupportPayloadShape.Describe(payload) };
        if (_lastUnsuccessful.TryGetValue(retryKey, out var retryOf))
        {
            startFields["retry_of"] = retryOf;
        }

        using (_logger.BeginScope(startFields))
        {
            DispatcherDiagnostics.ActionStart(_logger, action);
        }

        // 監看從這裡到方法返回為止，含記錄檔併回；結束時的摘要寫在 action.end、action.error 或 action.cancelled 之後。
        using var operationWatch = _operationMonitor?.BeginOperation(action, cancellationToken);
        using var processingScope = BeginProcessing(action);
        var exclusive = ActionExecutionPolicy.IsExclusive(action);
        IDisposable? executionLease = null;

        try
        {
            if (!_handlers.TryGetValue(action, out var handler))
            {
                throw new KeyNotFoundException($"No JET action handler is registered for '{action}'.");
            }

            if (exclusive)
            {
                // 變更型作業序列化：非阻塞試取，已有作業進行中即快速回絕（不排隊，避免誤點堆積）。
                executionLease = _executionGate.TryAcquire()
                    ?? throw new JetActionException(
                        JetErrorCodes.OperationInProgress,
                        "另一項作業正在進行中，請待其完成後再操作。");
            }

            // 原生 UI 與取消控制保留原執行緒；同步資料庫呼叫不能卡住 UI。
            // token 同時傳入排程與 handler；Task.Run 本身只負責工作開始前的取消。
            var result = ActionExecutionPolicy.RunsInBackground(action)
                ? await Task.Run(() => HandleWithCheckpointAsync(handler, payload, cancellationToken), cancellationToken).ConfigureAwait(false)
                : await HandleWithCheckpointAsync(handler, payload, cancellationToken).ConfigureAwait(false);
            _lastUnsuccessful.TryRemove(retryKey, out _);
            // 處理紀錄在放閘之前寫：寫的時候別的變更型作業還不能改案件，讀到的條件與筆數就是這次的結果。
            await RecordProcessingAsync(action, payload, result).ConfigureAwait(false);
            var resultRefs = SupportResultRefs.Describe(result);
            using (resultRefs is null ? null : _logger.BeginScope(new Dictionary<string, object?> { ["result_refs"] = resultRefs }))
            {
                DispatcherDiagnostics.ActionEnd(_logger, action, "ok", stopwatch.ElapsedMilliseconds);
            }

            return result;
        }
        catch (Exception exception)
        {
            _lastUnsuccessful[retryKey] = correlationId;
            if (stage.Stage is { } failedStage)
            {
                SupportFailureDiagnostics.Mark(exception, failedStage);
            }

            // 取消不是失敗：例外本身是取消，或已要求取消後資料庫中止語句丟出的例外（下方同樣改回報成取消）。
            // cancellation_requested 分辨使用者或關窗要求的取消，和程式內部丟出的取消例外。
            if (exception is OperationCanceledException
                || (exception is not JetActionException && cancellationToken.IsCancellationRequested))
            {
                DispatcherDiagnostics.ActionCancelled(
                    _logger,
                    action,
                    stopwatch.ElapsedMilliseconds,
                    cancellationToken.IsCancellationRequested,
                    exception);
            }
            else
            {
                DispatcherDiagnostics.ActionError(
                    _logger,
                    action,
                    stopwatch.ElapsedMilliseconds,
                    exception is JetActionException actionException
                        ? actionException.Code
                        : JetErrorCodes.BridgeError,
                    exception);
            }

            if (JetPayloadRejection.CheckOf(exception) is { } payloadCheck)
            {
                // 畫面只看到非預期錯誤那一句；沒過的檢查代號記進支援日誌。
                DispatcherDiagnostics.PayloadRejected(_logger, action, ((JetActionException)exception).Code, payloadCheck);
            }

            // 第五步損毀的條件：畫面只看到通用句，哪一條、哪一種記進支援日誌。
            FilterScenarioDiagnostics.LogException(_logger, action, exception);

            // 已要求取消時，資料庫驅動中止語句後丟出的例外一律回報成取消：驅動丟出的不一定是
            // OperationCanceledException，畫面一律回「作業已取消」。業務錯誤照舊原樣放行。
            if (exception is not (JetActionException or OperationCanceledException)
                && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("作業已取消。", exception, cancellationToken);
            }

            // 資料庫錯誤的轉換：業務錯誤（JetActionException）原樣丟出；其他例外先交給建構時傳入的轉換函式，
            // 認得的資料庫錯誤改用明確的錯誤碼，認不得的維持 bridge_error。
            if (exception is not JetActionException && _engineErrorTranslator?.Invoke(exception) is { } translated)
            {
                throw translated;
            }

            throw;
        }
        finally
        {
            // 資料庫收尾做完才放閘：下一個取得閘的變更型作業（例如刪除案件）開始時，這裡已經放手。
            executionLease?.Dispose();
        }
    }

    private async Task<object?> HandleWithCheckpointAsync(
        IApplicationActionHandler handler, JsonElement payload, CancellationToken cancellationToken)
    {
        // 變更型作業結束後，把它用到的案件資料庫的記錄檔併回（見 IProjectDatabaseCheckpoint）。作業期間其他變更型
        // 作業拿不到閘，案件不會換；併回時還持有閘，下一項作業開始前記錄檔已經清空。
        var checkpointProjectId = _databaseCheckpoint is not null && ActionExecutionPolicy.CheckpointsProjectDatabase(handler.Action)
            ? _session.CurrentProjectId
            : null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await handler.HandleAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (checkpointProjectId is not null)
            {
                await ProjectDatabaseCheckpointSteps.TryCheckpointAsync(
                    _databaseCheckpoint!, checkpointProjectId, _logger).ConfigureAwait(false);
            }
        }
    }

    private IDisposable? BeginProcessing(string action)
    {
        try
        {
            return _processingLog?.BeginAction(action);
        }
        catch (Exception)
        {
            // 處理紀錄只做記錄，開始不了也不影響這次操作。
            return null;
        }
    }

    /// <summary>處理紀錄只做記錄：寫不進去或自己出錯時，這次操作照樣回報成功，回應也不變。</summary>
    private async Task RecordProcessingAsync(string action, JsonElement payload, object? result)
    {
        if (_processingLog is null)
        {
            return;
        }

        try
        {
            await _processingLog.RecordAsync(action, payload, result, _session.CurrentProjectId).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 處理紀錄自己會把寫不進去的原因記進支援日誌；這裡只確保操作的結果不受影響。
        }
    }

    private static string? TryReadSupportProjectId(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("projectId", out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return ProjectNameRules.IsValid(value) ? value : null;
    }

}

/// <summary>
/// ActionDispatcher 的診斷日誌事件（LoggerMessage 來源產生器;結構化欄位即 NDJSON 欄位）。
/// correlation_id / project_id 由 dispatcher 的 BeginScope 攜帶,不在訊息模板內。
/// </summary>
internal static partial class DispatcherDiagnostics
{
    [LoggerMessage(EventId = 1000, EventName = SupportEventNames.ActionStart, Level = LogLevel.Information,
        Message = "action {action} start")]
    public static partial void ActionStart(ILogger logger, string action);

    [LoggerMessage(EventId = 1001, EventName = SupportEventNames.ActionEnd, Level = LogLevel.Information,
        Message = "action {action} {result_status} in {duration_ms} ms")]
    public static partial void ActionEnd(ILogger logger, string action, string result_status, long duration_ms);

    [LoggerMessage(EventId = 1002, EventName = SupportEventNames.ActionError, Level = LogLevel.Error,
        Message = "action {action} failed in {duration_ms} ms code={error_code}")]
    public static partial void ActionError(
        ILogger logger,
        string action,
        long duration_ms,
        string error_code,
        Exception exception);

    /// <summary>操作被取消；不算失敗，不佔支援日誌保留的失敗名額。</summary>
    [LoggerMessage(EventId = 1004, EventName = SupportEventNames.ActionCancelled, Level = LogLevel.Information,
        Message = "action {action} cancelled in {duration_ms} ms requested={cancellation_requested}")]
    public static partial void ActionCancelled(
        ILogger logger,
        string action,
        long duration_ms,
        bool cancellation_requested,
        Exception exception);

    /// <summary>畫面送出的資料沒過後端格式檢查；payload_check 是程式寫死的檢查代號，不含資料內容。</summary>
    [LoggerMessage(EventId = 1003, EventName = SupportEventNames.ActionPayloadRejected, Level = LogLevel.Warning,
        Message = "action {action} payload rejected code={error_code} check={payload_check}")]
    public static partial void PayloadRejected(
        ILogger logger,
        string action,
        string error_code,
        string payload_check);
}

/// <summary>
/// 記錄進行中的請求：每個 WebView requestId 對應一個 CancellationTokenSource。它由 dispatcher 持有，
/// 所以和 dispatcher 放在同一個檔案；請求完成、失敗或取消後，由 scope 把它移除並 dispose。
/// </summary>
public sealed class RequestCancellationRegistry : IOperationCancellationService
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests =
        new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private TaskCompletionSource _drained = CompletedSignal();
    private Task? _shutdownTask;
    private bool _stopping;

    public RequestCancellationScope Begin(string requestId)
    {
        var source = new CancellationTokenSource();
        lock (_gate)
        {
            if (_stopping)
            {
                source.Dispose();
                throw new OperationCanceledException("JET 正在關閉，不再接受新的作業。");
            }

            if (!_requests.TryAdd(requestId, source))
            {
                source.Dispose();
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "request_id_in_use");
            }

            if (_requests.Count == 1)
            {
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        return new RequestCancellationScope(this, requestId, source);
    }

    private static readonly AsyncLocal<string?> CurrentRequest = new();

    /// <summary>
    /// 正在處理的畫面請求編號，只在 <see cref="RunAsync"/> 執行那個請求的期間有值。
    /// 它跟著同一個請求的非同步流程走，背景執行緒上送出的進度事件也讀得到，畫面才分得出進度屬於哪一個請求。
    /// </summary>
    public static string? CurrentRequestId => CurrentRequest.Value;

    public async Task<T> RunAsync<T>(
        string requestId,
        Func<CancellationToken, Task<T>> operation)
    {
        using var scope = Begin(requestId);
        // 在 async 方法裡設定，方法結束時自動回到呼叫端原本的值，不會留給下一個請求。
        CurrentRequest.Value = requestId;
        return await operation(scope.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 把關閉視窗當成不會再恢復的訊號：取消目前所有請求，回傳的 Task 在進行中的 scope 全部結束時完成。
    /// 之後的 Begin 一律拒絕，這樣 CancelAll 取消完當下的請求之後，不會再有沒被取消的工作加進來。
    /// </summary>
    public Task CancelAll()
    {
        lock (_gate)
        {
            if (_shutdownTask is not null)
            {
                return _shutdownTask;
            }

            _stopping = true;
            var broadcasts = _requests.Values.Select(CancelAsyncSafe);
            _shutdownTask = Task.WhenAll(broadcasts.Append(_drained.Task));
            return _shutdownTask;
        }
    }

    private static Task CancelAsyncSafe(CancellationTokenSource source)
    {
        try
        {
            return source.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // 這個 scope 可能在 CancelAll 列出請求之後剛好結束；它已從登錄表移除，等全部請求結束的 Task 照樣會完成。
            return Task.CompletedTask;
        }
    }

    public bool TryCancel(string requestId)
    {
        if (!_requests.TryGetValue(requestId, out var source))
        {
            return false;
        }

        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // 目標可能在 TryGetValue 之後剛好完成，已由 scope 移除並 dispose。
            // 這時已經沒有可以取消的對象，照 operation.cancel 的契約回 requested:false。
            return false;
        }
    }

    private void End(string requestId)
    {
        CancellationTokenSource? source;
        lock (_gate)
        {
            if (!_requests.TryRemove(requestId, out source))
            {
                return;
            }

            if (_requests.IsEmpty)
            {
                _drained.TrySetResult();
            }
        }

        source.Dispose();
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    public sealed class RequestCancellationScope(
        RequestCancellationRegistry owner,
        string requestId,
        CancellationTokenSource source) : IDisposable
    {
        private int _disposed;

        public CancellationToken Token => source.Token;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.End(requestId);
            }
        }
    }
}
