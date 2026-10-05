using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Infrastructure;

/// <summary>
/// SQLite 與 DuckDB 案件的操作紀錄包裝。2026-10-02 使用者裁定本機案件寫操作紀錄失敗時不擋作業：
/// 重新匯入、重新配對與匯出報告的主要資料已經提交，紀錄表損壞也照常回報成功，
/// 失敗只記一筆不含訊息、路徑或 SQL 的支援日誌事件。取消照常往外丟。
/// SQL Server 案件不使用這個包裝，行為維持原樣。
/// </summary>
public sealed class BestEffortProjectAuditLog(IProjectAuditLog inner, ILogger logger) : IProjectAuditLog
{
    public async Task AppendAsync(
        string projectId,
        ProjectAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            await inner.AppendAsync(projectId, auditEvent, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            ProjectAuditLogDiagnostics.WriteFailed(logger, auditEvent.Operation, "append", exception);
        }
    }

    /// <summary>
    /// 讀取失敗時回 false：寧可少記一筆重新配對，也不寫一筆可能不實的重新配對紀錄。
    /// </summary>
    public async Task<bool> RequiresMappingRecommitAuditAsync(
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
            return false;
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
