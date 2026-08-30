namespace JET.Domain;

/// <summary>一則持久化的狀態訊息（manifest log.append / log.recent）。</summary>
public sealed record MessageLogEntry(
    DateTimeOffset OccurredUtc,
    string Level,
    string Text);

/// <summary>
/// 前端「狀態與訊息」的每專案持久化儲存。UX 輔助紀錄，非審計留痕
/// （審計留痕在 result_* 表與工作底稿）；每專案僅保留最近若干則，舊的自動修剪。
/// </summary>
public interface IMessageLogStore
{
    Task AppendAsync(string projectId, string level, string text, CancellationToken cancellationToken);

    /// <summary>最近訊息，新→舊。</summary>
    Task<IReadOnlyList<MessageLogEntry>> GetRecentAsync(string projectId, int limit, CancellationToken cancellationToken);
}
