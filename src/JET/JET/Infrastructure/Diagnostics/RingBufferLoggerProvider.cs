using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Infrastructure;

/// <summary>
/// 診斷日誌的 <see cref="ILoggerProvider"/>（dev-only 註冊;寫入 in-memory ring buffer,實作
/// <see cref="IDiagnosticLogStore"/> 供 dev.log.export 匯出）。實作 <see cref="ISupportExternalScope"/>:
/// LoggerFactory 注入共享 scope provider,使 ActionDispatcher 開的 correlation scope 跨層（AsyncLocal）
/// 對所有 logger 可見,不需手動傳遞。Release 不註冊此 provider（log 變 no-op）。
/// </summary>
public sealed class RingBufferLoggerProvider : ILoggerProvider, ISupportExternalScope, IDiagnosticLogStore
{
    private readonly DiagnosticRingBuffer _buffer;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public RingBufferLoggerProvider(int capacity) => _buffer = new DiagnosticRingBuffer(capacity);

    internal IExternalScopeProvider ScopeProvider => _scopeProvider;

    public ILogger CreateLogger(string categoryName) => new RingBufferLogger(categoryName, _buffer, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    public IReadOnlyList<DiagnosticLogEntry> Snapshot() => _buffer.Snapshot();

    public void Dispose()
    {
        // ring buffer 為 in-memory、無外部資源;重啟即清空（程序生命週期）。
    }
}

/// <summary>把 M.E.Logging 的 state＋active scopes＋exception 轉成 <see cref="DiagnosticLogEntry"/> 寫入 ring buffer。</summary>
internal sealed class RingBufferLogger(string category, DiagnosticRingBuffer buffer, RingBufferLoggerProvider owner)
    : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.ScopeProvider.Push(state);

    public bool IsEnabled(LogLevel logLevel) => true; // dev-only:全收（Release 不註冊本 provider）

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // scope 攤平與 state 轉換共用 DiagnosticLogEntryFactory（檔案 sink 走同一條路,避免漂移）。
        var entry = DiagnosticLogEntryFactory.Create(
            owner.ScopeProvider, category, logLevel, eventId, state, exception, formatter);
        buffer.Add(entry);
    }
}

/// <summary>bounded、thread-safe、滿則覆寫最舊的環形緩衝;Snapshot 回舊→新複本。</summary>
internal sealed class DiagnosticRingBuffer(int capacity)
{
    private readonly DiagnosticLogEntry[] _items = new DiagnosticLogEntry[Math.Max(1, capacity)];
    private readonly Lock _gate = new();
    private int _start;
    private int _count;

    public void Add(DiagnosticLogEntry entry)
    {
        lock (_gate)
        {
            if (_count < _items.Length)
            {
                _items[(_start + _count) % _items.Length] = entry;
                _count++;
            }
            else
            {
                _items[_start] = entry; // 覆寫最舊
                _start = (_start + 1) % _items.Length;
            }
        }
    }

    public IReadOnlyList<DiagnosticLogEntry> Snapshot()
    {
        lock (_gate)
        {
            var result = new DiagnosticLogEntry[_count];
            for (var i = 0; i < _count; i++)
            {
                result[i] = _items[(_start + i) % _items.Length];
            }

            return result;
        }
    }
}
