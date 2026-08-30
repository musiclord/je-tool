using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Infrastructure;

/// <summary>
/// 診斷日誌的檔案 sink(dev-only;與 <see cref="RingBufferLoggerProvider"/> 並列註冊)。把每筆
/// <see cref="DiagnosticLogEntry"/> 以 NDJSON(每行一筆)即時 append 到固定路徑,讓 agent 跑完 app 後
/// 直接讀執行時日誌,免去從 DEV 面板手動複製。重用 <see cref="DiagnosticLogEntryFactory"/> 的轉換與
/// <see cref="DiagnosticNdjson"/> 的序列化(與 dev.log.export 同格式)。Release 不註冊(log no-op)。
/// 實作 <see cref="ISupportExternalScope"/> 以取得 ActionDispatcher 開的跨層 correlation scope。
/// 檔案 I/O 失敗自我吞納,不影響主程式。
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

    /// <summary>本次程序的診斷日誌檔絕對路徑(每次啟動一檔,避免跨次混淆)。</summary>
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
            // dev 工具:檔案 I/O 失敗(鎖定、權限、磁碟滿)不得影響主程式;放棄這一行即可。
        }
    }

    public void Dispose()
    {
        // 無持有資源:每筆以 File.AppendAllText 開閉,無常開 handle。
    }
}

/// <summary>把 M.E.Logging 事件轉成 <see cref="DiagnosticLogEntry"/> 後寫入檔案 sink。</summary>
internal sealed class NdjsonFileLogger(string category, NdjsonFileLoggerProvider owner) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.ScopeProvider.Push(state);

    public bool IsEnabled(LogLevel logLevel) => true; // dev-only:全收(Release 不註冊本 provider)

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
