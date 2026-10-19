using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// 資料已提交之後才寫的操作紀錄。寫入失敗時變更仍然有效，回報成功並附提醒（使用者 2026-10-19 裁定），
/// 不讓畫面誤以為整個動作失敗。本機案件的包裝已把失敗記進支援日誌並回 false；
/// 其他實作往外丟的錯誤在這裡記下錯誤類型。
/// </summary>
internal static partial class CommittedAuditLog
{
    /// <summary>使用者 2026-10-19 在視窗內選擇題確認的句子。</summary>
    internal const string AppendFailedWarning = "變更已儲存，但這次變更沒有寫入操作紀錄。";

    /// <summary>產生報告時的提醒；使用者 2026-10-19 在視窗內選擇題確認的句子。</summary>
    internal const string ReportAppendFailedWarning = "檔案已產生，但這次產生的檔案沒有寫入操作紀錄。";

    /// <summary>寫入成功回傳 null；失敗回傳提醒句，由呼叫端放進回應的 warnings。</summary>
    public static async Task<string?> AppendAsync(
        IProjectAuditLog auditLog, string projectId, ProjectAuditEvent auditEvent, ILogger? logger,
        string failedWarning = AppendFailedWarning)
    {
        try
        {
            return await auditLog.TryAppendAsync(projectId, auditEvent, CancellationToken.None) ? null : failedWarning;
        }
        catch (Exception error)
        {
            if (logger is not null) AppendFailed(logger, auditEvent.Operation, error.GetType().Name);
            return failedWarning;
        }
    }

    /// <summary>
    /// 確認配對後的重新配對紀錄。recommit 為 true 時寫入；為 null 表示確認前讀不到紀錄表、無法判斷這次算不算重新配對，
    /// 不寫可能不實的紀錄，照樣回提醒句；為 false 時不需要紀錄。
    /// </summary>
    public static async Task<string?> AppendMappingRecommitAsync(
        IProjectAuditLog auditLog, string projectId, bool? recommit, string dataset, long projectedRowCount, ILogger? logger) =>
        recommit switch
        {
            true => await AppendAsync(
                auditLog,
                projectId,
                ProjectAuditEvent.Create(
                    ProjectAuditOperations.MappingRecommit,
                    ProjectAuditTargetTypes.Mapping,
                    dataset,
                    projectedRowCount,
                    replacedCount: 1),
                logger),
            null => AppendFailedWarning,
            false => null
        };

    /// <summary>把提醒接在既有提醒後面；沒有提醒時原樣回傳。</summary>
    public static IReadOnlyList<string> WithWarning(IReadOnlyList<string> warnings, string? warning) =>
        warning is null ? warnings : [.. warnings, warning];

    [LoggerMessage(EventName = "audit_log.append_failed", Level = LogLevel.Warning,
        Message = "Change committed; project audit log entry {operation} not written. Error type: {error_type}")]
    private static partial void AppendFailed(ILogger logger, string operation, string error_type);
}
