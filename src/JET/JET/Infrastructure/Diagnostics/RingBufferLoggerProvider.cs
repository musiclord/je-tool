using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Infrastructure;

/// <summary>
/// 把診斷日誌寫進記憶體的 <see cref="ILoggerProvider"/>。日誌放在固定容量、滿了覆寫最舊紀錄的記憶體緩衝區。
/// 只在啟用開發者工具時註冊；Release 組建不註冊，不會留下診斷日誌。
/// 實作 <see cref="IDiagnosticLogStore"/>，dev.log.exportFile 讀不到診斷日誌檔時改從這裡取紀錄。
/// 實作 <see cref="ISupportExternalScope"/>，LoggerFactory 會把各 provider 共用的 scope provider 交給它。
/// ActionDispatcher 建立的 scope 帶有 correlation_id，靠 AsyncLocal 在同一個非同步流程裡讓每一層的 logger 都讀得到，
/// 不必手動傳遞。
/// </summary>
public sealed class RingBufferLoggerProvider : ILoggerProvider, ISupportExternalScope, IDiagnosticLogStore
{
    private readonly BoundedRingBuffer<DiagnosticLogEntry> _buffer;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public RingBufferLoggerProvider(int capacity) => _buffer = new BoundedRingBuffer<DiagnosticLogEntry>(capacity);

    internal IExternalScopeProvider ScopeProvider => _scopeProvider;

    public ILogger CreateLogger(string categoryName) => new RingBufferLogger(categoryName, _buffer, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    public IReadOnlyList<DiagnosticLogEntry> Snapshot() => _buffer.Snapshot();

    public void Dispose()
    {
        // 緩衝區只在記憶體裡，沒有要釋放的外部資源；程式結束就清空。
    }
}

/// <summary>把 Microsoft.Extensions.Logging 的一筆日誌轉成 <see cref="DiagnosticLogEntry"/>，寫進記憶體緩衝區。</summary>
internal sealed class RingBufferLogger(string category, BoundedRingBuffer<DiagnosticLogEntry> buffer, RingBufferLoggerProvider owner)
    : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.ScopeProvider.Push(state);

    public bool IsEnabled(LogLevel logLevel) => true; // 每個層級都收；這個 provider 只在啟用開發者工具時註冊，Release 組建不註冊。

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // 轉換交給 DiagnosticLogEntryFactory，和寫檔的 logger 用同一份，兩邊的欄位才會一致。
        var entry = DiagnosticLogEntryFactory.Create(
            owner.ScopeProvider, category, logLevel, eventId, state, exception, formatter);
        buffer.Add(entry);
    }
}
