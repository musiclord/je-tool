using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using JET.Application;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Bridge;

public sealed class ActionDispatcher
{
    private readonly IReadOnlyDictionary<string, IApplicationActionHandler> _handlers;
    private readonly ILogger<ActionDispatcher> _logger;
    private readonly ProjectSession _session;
    private readonly Func<Exception, JetActionException?>? _engineErrorTranslator;
    private readonly ActionExecutionGate _executionGate;
    private readonly IProjectDatabaseRetention? _databaseRetention;

    public ActionDispatcher(
        IEnumerable<IApplicationActionHandler> handlers,
        ILogger<ActionDispatcher> logger,
        ProjectSession session,
        // 引擎錯誤的單一映射點：composition 注入 Infrastructure 的轉譯 delegate
        // （SqlServerEngineErrors.TryTranslate），Bridge 不直接依賴 Infrastructure（維持分層依賴方向）。
        Func<Exception, JetActionException?>? engineErrorTranslator = null,
        RequestCancellationRegistry? cancellationRegistry = null,
        ActionExecutionGate? executionGate = null,
        // 一次操作期間保持當前案件資料庫開啟（只有 DuckDB 有作用）；null 時不持有，自行組裝 dispatcher 的測試不受影響。
        IProjectDatabaseRetention? databaseRetention = null)
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
        _databaseRetention = databaseRetention;
    }

    public IReadOnlyCollection<string> RegisteredActions => _handlers.Keys.ToArray();

    public RequestCancellationRegistry CancellationRegistry { get; }

    /// <summary>
    /// Host 關閉前以同一把 execution gate 做非阻塞安全檢查；lease 必須一路持有到 runtime
    /// 停止接受 request 並完成 drain 啟動，避免原生關窗繞過 dispatcher／handler 的共用閘。
    /// </summary>
    internal IDisposable? TryAcquireExecutionLease() => _executionGate.TryAcquire();

    /// <summary>
    /// 每次 dispatch 生成 correlation_id 並以 <see cref="ILogger.BeginScope"/> 建立 scope——
    /// 同一 LoggerFactory 的子層 logger（Handler/Repository）在該 async 流程內自動帶入（AsyncLocal）。
    /// 記錄 action 生命週期（start/end/error、duration_ms、result_status）；Debug provider 保存完整診斷，
    /// Release provider 只接收已去識別的 allowlist 支援事件。
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
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["correlation_id"] = correlationId,
            ["action"] = action,
            ["project_id"] = _session.CurrentProjectId,
            ["support_project_id"] = supportProjectId,
        });

        var stopwatch = Stopwatch.StartNew();
        DispatcherDiagnostics.ActionStart(_logger, action);

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

            // 原生 UI 與取消控制保留原執行緒；同步資料庫呼叫及其 retention 開關都不能卡住 UI。
            // token 同時傳入排程與 handler；Task.Run 本身只負責工作開始前的取消。
            var result = ActionExecutionPolicy.RunsInBackground(action)
                ? await Task.Run(() => HandleWithRetentionAsync(handler, payload, cancellationToken), cancellationToken).ConfigureAwait(false)
                : await HandleWithRetentionAsync(handler, payload, cancellationToken).ConfigureAwait(false);
            DispatcherDiagnostics.ActionEnd(_logger, action, "ok", stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception exception)
        {
            DispatcherDiagnostics.ActionError(
                _logger,
                action,
                stopwatch.ElapsedMilliseconds,
                exception is JetActionException actionException
                    ? actionException.Code
                    : JetErrorCodes.BridgeError,
                exception);

            // 引擎錯誤映射：業務錯誤（JetActionException）原樣放行；其餘例外先過映射點，
            // 可辨識者（登入失敗/唯一鍵衝突/死鎖/逾時）以明確錯誤碼取代裸 bridge_error。
            if (exception is not JetActionException && _engineErrorTranslator?.Invoke(exception) is { } translated)
            {
                throw translated;
            }

            throw;
        }
        finally
        {
            // 先放開資料庫再放閘：下一個取得閘的變更型作業（例如刪除案件）開始時，這裡已經放手。
            executionLease?.Dispose();
        }
    }

    private async Task<object?> HandleWithRetentionAsync(
        IApplicationActionHandler handler, JsonElement payload, CancellationToken cancellationToken)
    {
        IDisposable? retention = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_databaseRetention is not null
                && ActionExecutionPolicy.RetainsProjectDatabase(handler.Action)
                && _session.CurrentProjectId is { } projectId)
                retention = _databaseRetention.TryRetain(projectId);
            return await handler.HandleAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        finally { retention?.Dispose(); }
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
    [LoggerMessage(EventId = 1000, EventName = "action.start", Level = LogLevel.Information,
        Message = "action {action} start")]
    public static partial void ActionStart(ILogger logger, string action);

    [LoggerMessage(EventId = 1001, EventName = "action.end", Level = LogLevel.Information,
        Message = "action {action} {result_status} in {duration_ms} ms")]
    public static partial void ActionEnd(ILogger logger, string action, string result_status, long duration_ms);

    [LoggerMessage(EventId = 1002, EventName = "action.error", Level = LogLevel.Error,
        Message = "action {action} failed in {duration_ms} ms code={error_code}")]
    public static partial void ActionError(
        ILogger logger,
        string action,
        long duration_ms,
        string error_code,
        Exception exception);
}

/// <summary>
/// WebView requestId → CancellationTokenSource 的在途登錄表。與唯一使用者 dispatcher 同檔，
/// 維持 Bridge 扁平三檔；完成、失敗或取消後由 scope 移除並 dispose。
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
                throw new JetActionException(
                    JetErrorCodes.InvalidPayload,
                    $"requestId '{requestId}' 已在執行中，不能重複使用。");
            }

            if (_requests.Count == 1)
            {
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        return new RequestCancellationScope(this, requestId, source);
    }

    public async Task<T> RunAsync<T>(
        string requestId,
        Func<CancellationToken, Task<T>> operation)
    {
        using var scope = Begin(requestId);
        return await operation(scope.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 將關窗視為終局訊號：取消目前所有 request，並回傳在途 scope 全部結束時完成的 task。
    /// 後續 Begin 會直接拒絕，避免 CancelAll snapshot 後又放入未取消工作。
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
            // scope 可能正好在 shutdown snapshot 後完成；drain task 仍會反映它已離開 registry。
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
            // 目標可能正好在 TryGetValue 後完成並由 scope 移除／dispose。
            // 此時取消已沒有對象，依 operation.cancel 契約誠實回 requested:false。
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
