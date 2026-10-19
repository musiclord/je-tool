using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Infrastructure;

/// <summary>
/// SQLite 與 DuckDB 案件的操作紀錄包裝。2026-10-02 使用者裁定本機案件寫操作紀錄失敗時不擋作業：
/// 重新匯入、重新配對與匯出報告的主要資料已經提交，紀錄表損壞也照常回報成功，
/// 失敗只記一筆不含訊息、路徑或 SQL 的支援日誌事件。取消照常往外丟。
/// SQL Server 案件不使用這個包裝，行為維持原樣。
/// 2026-10-19 使用者裁定寫不進操作紀錄時回報成功並提醒：TryAppendAsync 回 false，重新匯入與重新配對據此附提醒；
/// 確認配對前讀不到紀錄表時 TryRequiresMappingRecommitAuditAsync 回 null，重新配對同樣附提醒。
/// </summary>
public sealed class BestEffortProjectAuditLog(IProjectAuditLog inner, ILogger logger) : IProjectAuditLog
{
    public Task AppendAsync(
        string projectId,
        ProjectAuditEvent auditEvent,
        CancellationToken cancellationToken) =>
        TryAppendAsync(projectId, auditEvent, cancellationToken);

    public async Task<bool> TryAppendAsync(
        string projectId,
        ProjectAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            await inner.AppendAsync(projectId, auditEvent, cancellationToken);
            return true;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            ProjectAuditLogDiagnostics.WriteFailed(logger, auditEvent.Operation, "append", exception);
            return false;
        }
    }

    /// <summary>
    /// 讀取失敗時回 false：寧可少記一筆重新配對，也不寫一筆可能不實的重新配對紀錄。
    /// </summary>
    public async Task<bool> RequiresMappingRecommitAuditAsync(
        string projectId,
        string dataset,
        CancellationToken cancellationToken) =>
        await TryRequiresMappingRecommitAuditAsync(projectId, dataset, cancellationToken) ?? false;

    /// <summary>讀取失敗時記一筆支援日誌後回 null，表示無法判斷；呼叫端不寫紀錄，照樣提醒。</summary>
    public async Task<bool?> TryRequiresMappingRecommitAuditAsync(
        string projectId,
        string dataset,
        CancellationToken cancellationToken)
    {
        try
        {
            return await inner.RequiresMappingRecommitAuditAsync(projectId, dataset, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            ProjectAuditLogDiagnostics.WriteFailed(
                logger, ProjectAuditOperations.MappingRecommit, "check", exception);
            return null;
        }
    }
}

internal static partial class ProjectAuditLogDiagnostics
{
    [LoggerMessage(
        EventId = 2300,
        EventName = "project_audit.write_failed",
        Level = LogLevel.Warning,
        Message = "project operation record not written operation={operation} phase={phase}")]
    public static partial void WriteFailed(ILogger logger, string operation, string phase, Exception exception);
}
