using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// log.append：前端「狀態與訊息」的持久化。
/// 訊息是 UX 輔助紀錄，非審計留痕；需要 active project。
/// </summary>
public sealed class LogAppendHandler(ProjectSession session) : IApplicationActionHandler
{
    /// <summary>超長訊息（如 column_mismatch 雙向差集）截斷而非拒絕——持久化不該因訊息太長而失敗。</summary>
    public const int MaxTextLength = 4000;

    // 訊息分三級（使用者 2026-10-13 裁定）：error 是操作失敗，warn 是需要注意，info 是一般說明與完成紀錄。
    private static readonly string[] AllowedLevels = ["info", "warn", "error"];

    public string Action => "log.append";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();

        var level = (PayloadReader.GetOptionalString(payload, "level") ?? "info").ToLowerInvariant();
        if (!AllowedLevels.Contains(level))
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "message_level_invalid");
        }

        var text = PayloadReader.GetRequiredString(payload, "text");
        if (text.Length > MaxTextLength)
        {
            text = text[..MaxTextLength];
        }

        await repositories.MessageLog.AppendAsync(projectId, level, text, cancellationToken);
        return new { ok = true };
    }
}

/// <summary>log.recent：最近持久化訊息（新→舊），供 project.load 後還原訊息面板歷史。</summary>
public sealed class LogRecentHandler(ProjectSession session) : IApplicationActionHandler
{
    public string Action => "log.recent";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var limit = Math.Clamp(PayloadReader.GetOptionalInt(payload, "limit") ?? 30, 1, 100);

        var entries = await repositories.MessageLog.GetRecentAsync(projectId, limit, cancellationToken);

        return new
        {
            messages = entries
                .Select(e => new { occurredUtc = e.OccurredUtc, level = e.Level, text = e.Text })
                .ToArray()
        };
    }
}
