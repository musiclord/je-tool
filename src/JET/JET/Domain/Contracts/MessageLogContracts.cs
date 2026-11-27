namespace JET.Domain;

/// <summary>一則持久化的狀態訊息（log.append / log.recent）。</summary>
public sealed record MessageLogEntry(
    DateTimeOffset OccurredUtc,
    string Level,
    string Text);

/// <summary>
/// 前端「狀態與訊息」在每個案件裡的儲存。這是操作輔助紀錄，不是審計留痕；審計留痕在 validation_result、
/// prescreen_result 等結果表與工作底稿。每個案件只保留最近若干則，較舊的自動刪除。
/// </summary>
public interface IMessageLogStore
{
    Task AppendAsync(string projectId, string level, string text, CancellationToken cancellationToken);

    /// <summary>最近訊息，新→舊。</summary>
    Task<IReadOnlyList<MessageLogEntry>> GetRecentAsync(string projectId, int limit, CancellationToken cancellationToken);
}
