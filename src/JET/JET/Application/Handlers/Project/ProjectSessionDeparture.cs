using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// 離開開著的案件：放掉它的工作鎖、離開 session，再關閉它的資料庫連線，記錄檔併回資料庫檔。
/// project.releaseLock 用 <see cref="LeaveAsync"/> 離開案件；開啟另一個案件與建立新案件時，新的案件開成之後、
/// 進入 session 之前，用 <see cref="LeaveOtherProjectAsync"/> 離開還開著的案件，否則前一個案件的工作鎖與資料庫連線會一直留到 JET 關閉。
/// 新的案件開不成時不離開，目前的案件照樣開著。
/// </summary>
internal sealed class ProjectSessionDeparture(
    ProjectSession session,
    CurrentPrincipal principal,
    IProjectDatabaseCheckpoint? databaseCheckpoint = null,
    ILogger<ProjectSessionDeparture>? logger = null)
{
    /// <summary>
    /// 工作鎖放成功了才離開 session；<paramref name="leaveSession"/> 只在目前案件仍是 <paramref name="projectId"/> 時才清掉它。
    /// 放鎖失敗或被取消時往外丟，session 保留，審計員可以再試一次。
    /// </summary>
    internal static async Task LeaveAsync(
        string projectId,
        ILockService lockService,
        Func<string, bool> leaveSession,
        string principalName,
        IProjectDatabaseCheckpoint? databaseCheckpoint,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        await lockService.ReleaseAsync(projectId, principalName, cancellationToken);
        leaveSession(projectId);
        if (databaseCheckpoint is not null)
        {
            await ProjectDatabaseCheckpointSteps.TryCloseAsync(databaseCheckpoint, projectId, logger);
        }
    }

    /// <summary>
    /// 進入已經開成的 <paramref name="enteringProjectId"/> 之前，先離開目前開著的另一個案件；重開同一個案件時不動。
    /// 畫面回到案件清單之前都會先離開案件，這裡補的是畫面沒有離開成的情形，所以不理會取消要求；
    /// 放不掉時只記進支援日誌，照常開新的案件，不擋審計員。
    /// 離開成功時在離開的案件記一筆離開事件：這種離開沒有 project.releaseLock，支援日誌匯出靠它認出這個程式正常離開了案件。
    /// </summary>
    internal async Task LeaveOtherProjectAsync(string enteringProjectId)
    {
        if (session.Current is (var projectId, var repositories)
            && !string.Equals(projectId, enteringProjectId, StringComparison.Ordinal))
        {
            try
            {
                await LeaveAsync(
                    projectId, repositories.LockService, expected => session.Leave(expected),
                    principal.Name, databaseCheckpoint, logger, CancellationToken.None);
                SupportCaseDiagnostics.CaseLeft(logger, projectId);
            }
            catch (Exception error)
            {
                if (logger is not null) SupportStepDiagnostics.StepFailed(logger, "lock_release", error);
            }
        }
    }
}
