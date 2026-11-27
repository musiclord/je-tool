namespace JET.Domain;

/// <summary>
/// 診斷日誌單筆，只給開發者在 Debug 組建使用，和 validation_result、prescreen_result 等審計結果表，
/// 以及 IMessageLogStore 的畫面訊息互相獨立。
/// 純資料，不引用 Microsoft.Extensions.Logging；由 Infrastructure 的 RingBufferLogger 從 M.E.Logging 的
/// state、active scopes 與 exception 轉換而來。Fields 含結構化欄位（SQL、參數、duration_ms、rows_affected…）
/// 與非 id 的 scope 值（如 action）;CorrelationId/TransactionId/ProjectId 為攤平後的常用 scope 鍵。
/// </summary>
public sealed record DiagnosticLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string EventName,
    string Message,
    string? CorrelationId,
    string? TransactionId,
    string? ProjectId,
    IReadOnlyDictionary<string, object?> Fields,
    string? Exception);

/// <summary>
/// 診斷日誌讀取埠（dev tool 匯出用;鏡射 <see cref="IMessageLogStore"/> / IDevDatabaseInspector 的埠慣例）。
/// 由 Infrastructure 的 ring buffer provider 實作;dev.log.exportFile 在檔案 sink 不可讀時經此取得紀錄。
/// </summary>
public interface IDiagnosticLogStore
{
    /// <summary>目前 ring buffer 內容,舊→新（重啟清空、滿則覆寫最舊）。</summary>
    IReadOnlyList<DiagnosticLogEntry> Snapshot();
}
