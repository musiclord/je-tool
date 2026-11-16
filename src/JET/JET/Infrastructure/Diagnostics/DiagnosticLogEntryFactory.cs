using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Infrastructure;

/// <summary>
/// 把 Microsoft.Extensions.Logging 的一筆日誌轉成 <see cref="DiagnosticLogEntry"/>。
/// 來源是 logLevel、eventId、state、exception，以及目前生效的 scope。
/// 寫到記憶體和寫到檔案的診斷日誌都用這一個轉換，兩邊的欄位才會一致。
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

        // 把目前生效的每一層 scope 的鍵值合併成一筆。correlation_id、transaction_id 與 project_id
        // 放進紀錄自己的欄位，其餘的鍵（例如 action）放進 fields。
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
                            // support_project_id 是從請求內容讀出的案件代號，只給支援日誌判斷事件屬於哪個案件。
                            // 診斷日誌不記這個鍵，載入或刪除案件的請求裡的案件名稱就不會多寫一份。
                            // 已開啟的案件照常記在 project_id 欄位。
                            case "support_project_id": break;
                            default: fields[kv.Key] = kv.Value; break;
                        }
                    }
                }
            },
            (object?)null);

        // state 帶的是 LoggerMessage 的各個欄位，逐一放進 fields；{OriginalFormat} 是訊息模板本身，不放。
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

        // 失敗或取消時各層記在例外上的步驟（failure_stage）、報告與工作表位置，以及檔案錯誤的分類，
        // 和支援日誌用同一份規則，診斷日誌也看得到操作停在哪一步。
        SupportFailureFields.Add(fields, exception);

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
