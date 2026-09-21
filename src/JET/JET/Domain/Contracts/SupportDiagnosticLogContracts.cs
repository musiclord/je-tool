using System.Text.Json.Serialization;

namespace JET.Domain;

/// <summary>
/// 可由公司 Release 匯出的去識別支援事件。InternalProjectId 只供程序內篩選，永不序列化；其餘欄位
/// 必須已在 provider 寫入 buffer 前完成 allowlist 與長度限制，匯出端不得再接觸原始例外或 SQL state。
/// </summary>
public sealed record SupportDiagnosticLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string EventName,
    string Message,
    string? CorrelationId,
    [property: JsonIgnore] string? InternalProjectId,
    IReadOnlyDictionary<string, object?> Fields,
    string? Exception);

public interface ISupportDiagnosticLogStore
{
    /// <summary>目前程序的 bounded 快照，舊到新；回傳內容已去識別。</summary>
    IReadOnlyList<SupportDiagnosticLogEntry> Snapshot();

    long EventsOmitted => 0;
}
