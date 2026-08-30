using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// log.append：前端「狀態與訊息」的持久化（manifest 細節）。
/// 訊息是 UX 輔助紀錄，非審計留痕；需要 active project。
/// </summary>
public sealed class LogAppendHandler(IMessageLogStore store, ProjectSession session) : IApplicationActionHandler
{
    /// <summary>超長訊息（如 column_mismatch 雙向差集）截斷而非拒絕——持久化不該因訊息太長而失敗。</summary>
    public const int MaxTextLength = 4000;

    private static readonly string[] AllowedLevels = ["info", "warn"];

    public string Action => "log.append";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();

        var level = (PayloadReader.GetOptionalString(payload, "level") ?? "info").ToLowerInvariant();
        if (!AllowedLevels.Contains(level))
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"欄位 'level' 的值 '{level}' 無效，允許值：info、warn。");
        }

        var text = PayloadReader.GetRequiredString(payload, "text");
        if (text.Length > MaxTextLength)
        {
            text = text[..MaxTextLength];
        }

        await store.AppendAsync(projectId, level, text, cancellationToken);
        return new { ok = true };
    }
}

/// <summary>log.recent：最近持久化訊息（新→舊），供 project.load 後還原訊息面板歷史。</summary>
public sealed class LogRecentHandler(IMessageLogStore store, ProjectSession session) : IApplicationActionHandler
{
    public string Action => "log.recent";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();
        var limit = Math.Clamp(PayloadReader.GetOptionalInt(payload, "limit") ?? 30, 1, 100);

        var entries = await store.GetRecentAsync(projectId, limit, cancellationToken);

        return new
        {
            messages = entries
                .Select(e => new { occurredUtc = e.OccurredUtc, level = e.Level, text = e.Text })
                .ToArray()
        };
    }
}
