using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Infrastructure;

/// <summary>
/// 把診斷日誌即時寫到檔案的 <see cref="ILoggerProvider"/>。只在啟用開發者工具而且指定了日誌目錄時註冊，
/// 和 <see cref="RingBufferLoggerProvider"/> 一起使用；Release 組建不註冊，不會寫出診斷日誌。
/// 每筆 <see cref="DiagnosticLogEntry"/> 寫成一行 NDJSON，附加到這次啟動專用的檔案。
/// 程式結束後可以直接讀這個檔，不必從開發面板手動複製。
/// 轉換用 <see cref="DiagnosticLogEntryFactory"/>，序列化用 <see cref="DiagnosticNdjson"/>，
/// 所以格式和 dev.log.exportFile 改從記憶體緩衝區匯出時相同。
/// 實作 <see cref="ISupportExternalScope"/>，才讀得到 ActionDispatcher 建立的 scope 裡的 correlation_id 等欄位。
/// 寫檔失敗時放棄那一行，不影響主程式。
/// </summary>
public sealed class NdjsonFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly Lock _writeGate = new();
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public NdjsonFileLoggerProvider(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"jet-dev-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.ndjson");
    }

    /// <summary>這次啟動的診斷日誌檔的絕對路徑。每次啟動寫一個新檔，不同次的紀錄不會混在一起。</summary>
    public string FilePath { get; }

    internal IExternalScopeProvider ScopeProvider => _scopeProvider;

    public ILogger CreateLogger(string categoryName) => new NdjsonFileLogger(categoryName, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    internal void Append(DiagnosticLogEntry entry)
    {
        var line = DiagnosticNdjson.SerializeLine(entry);
        try
        {
            lock (_writeGate)
            {
                File.AppendAllText(FilePath, line + '\n');
            }
        }
        catch
        {
            // 這是開發者工具。檔案被鎖定、沒有權限或磁碟滿了而寫不進去時，放棄這一行，不影響主程式。
        }
    }

    public void Dispose()
    {
        // 沒有要釋放的資源：每筆都用 File.AppendAllText 開檔、寫入後關檔，不留著開啟的檔案。
    }
}

/// <summary>把 Microsoft.Extensions.Logging 的一筆日誌轉成 <see cref="DiagnosticLogEntry"/>，寫進診斷日誌檔。</summary>
internal sealed class NdjsonFileLogger(string category, NdjsonFileLoggerProvider owner) : ILogger
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
        var entry = DiagnosticLogEntryFactory.Create(
            owner.ScopeProvider, category, logLevel, eventId, state, exception, formatter);
        owner.Append(entry);
    }
}
