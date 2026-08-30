using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Infrastructure;

/// <summary>
/// 把 M.E.Logging 的 logLevel/eventId/state/active scopes/exception 轉成 <see cref="DiagnosticLogEntry"/>。
/// 診斷日誌所有 sink(ring buffer、檔案)共用此轉換,避免 scope 攤平邏輯重複而漂移。
/// </summary>
internal static class DiagnosticLogEntryFactory
{
    public static DiagnosticLogEntry Create<TState>(
        IExternalScopeProvider scopeProvider,
        string category,
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        string? correlationId = null;
        string? transactionId = null;
        string? projectId = null;

        // 攤平 active scopes:常用 id 鍵升為頂層,其餘(如 action)進 fields。
        scopeProvider.ForEachScope(
            (scope, _) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    foreach (var kv in pairs)
                    {
                        switch (kv.Key)
                        {
                            case "correlation_id": correlationId = kv.Value?.ToString(); break;
                            case "transaction_id": transactionId = kv.Value?.ToString(); break;
                            case "project_id": projectId = kv.Value?.ToString(); break;
                            default: fields[kv.Key] = kv.Value; break;
                        }
                    }
                }
            },
            (object?)null);

        // 結構化 state(LoggerMessage 欄位);略過訊息模板鍵。
        if (state is IEnumerable<KeyValuePair<string, object?>> statePairs)
        {
            foreach (var kv in statePairs)
            {
                if (kv.Key != "{OriginalFormat}")
                {
                    fields[kv.Key] = kv.Value;
                }
            }
        }

        return new DiagnosticLogEntry(
            DateTimeOffset.UtcNow,
            logLevel.ToString(),
            category,
            eventId.Name ?? string.Empty,
            formatter(state, exception),
            correlationId,
            transactionId,
            projectId,
            fields,
            exception?.ToString());
    }
}
