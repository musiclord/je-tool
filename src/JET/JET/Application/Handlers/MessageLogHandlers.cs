using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// log.append：把前端「狀態與訊息」的一則訊息存進案件。
/// 訊息只是方便使用者回看的紀錄，不是審計軌跡；要先開啟案件才能用。
/// </summary>
public sealed class LogAppendHandler(ProjectSession session) : IApplicationActionHandler
{
    /// <summary>
    /// 訊息超過這個長度就截斷，不拒絕。例如 column_mismatch 的訊息會列出兩邊各自多出的欄位，可能很長；
    /// 存訊息不該因為太長而失敗。
    /// </summary>
    public const int MaxTextLength = 4000;

    // 訊息分三級：error 是操作失敗，warn 是需要注意，info 是一般說明與完成紀錄。
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

/// <summary>log.recent：讀回最近存下的訊息，從新到舊排列；前端在 project.load 之後用它還原訊息面板的歷史。</summary>
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
