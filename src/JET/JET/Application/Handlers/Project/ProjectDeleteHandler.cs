using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// project.delete：永久刪除專案（硬刪不可復原）。sqlServer 案件先做授權前置（spec §4，同 project.load 的三路判定）：
/// 登記存在但當前 principal 不可見 → not_authorized，且在動任何資料之前擋下——連本機快取資料夾也保留不刪。通過後
/// 先刪資料庫（provider 路由：SQLite 刪檔／SQL Server DROP SCHEMA），再刪 project.json 資料夾。不需 active project
/// （從專案選擇畫面呼叫）。資料庫刪除失敗則例外冒泡、資料夾保留，供修正後重試（如 sqlServer 連線未設定）。
/// </summary>
public sealed class ProjectDeleteHandler(
    IProjectStore projectStore,
    IProjectDatabaseDeleter databaseDeleter,
    IProjectRegistry registry,
    CurrentPrincipal principal,
    IReportArtifactStore reportArtifactStore,
    IProjectDeletionLockService deletionLockService,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "project.delete";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = PayloadReader.GetRequiredString(payload, "projectId");
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                $"找不到專案 '{projectId}'。");

        // Windows 本地路徑大小寫不敏感；FindAsync 命中後，後續只用文件內的 canonical id。
        // 否則 session Leave／provider resolver cache 會把同一案件的大小寫變體當成兩個身分。
        projectId = document.ProjectId;

        // 授權前置(sqlServer;spec §4,同 project.load):登記存在但當前 principal 不可見 → not_authorized,在動任何
        // 資料之前擋下——連本機快取資料夾也不刪(避免「刪了快取卻誤以為刪了案件」)。登記不存在的孤兒沿現行放行(那是
        // 清理路徑,第四輪 reconcile 範圍)。serverOnly(本機無資料夾)在上方 FindAsync 已回 project_not_found,走不到這裡。
        if (document.DatabaseProvider == ProjectDocument.SqlServerDatabaseProvider
            && await registry.FindVisibleAsync(projectId, principal.Name, cancellationToken) is null
            && await registry.ExistsAsync(projectId, cancellationToken))
        {
            throw new JetActionException(
                JetErrorCodes.NotAuthorized,
                "您沒有此線上案件的存取權，無法刪除。");
        }

        // 本地案件以與 project.load 相同的 OS 檔案鎖做非阻塞試取；SQL Server arm 維持 no-op，
        // 其租約仍只由既有刪案交易處理。Held 時在 artifact／DB／folder 任何變更之前止步。
        var deletionLockOutcome = await deletionLockService.TryAcquireAsync(
            projectId,
            principal.Name,
            cancellationToken);
        var deletionLease = deletionLockOutcome switch
        {
            ProjectDeletionLockOutcome.Acquired acquired => acquired.Lease,
            ProjectDeletionLockOutcome.Held held => throw new JetActionException(
                JetErrorCodes.ProjectLocked,
                ProjectLockErrorMessage.Format(held.LockedBy, held.MachineName, held.LockedUtc)),
            _ => throw new InvalidOperationException("未知的 project deletion lock outcome。")
        };
        await using var heldDeletionLease = deletionLease;

        // 授權通過後才取得案件外的 artifact lease。它會先復原 pending mutation journal，並一路保持到
        // provider 資料與案件資料夾處理完成，避免匯出／清理與刪案跨程序交錯。
        await using var artifactDeletionLease = await reportArtifactStore.AcquireProjectDeletionLeaseAsync(
            projectId,
            cancellationToken);

        // 1) 刪資料庫(須在刪資料夾前——provider 路由讀 project.json 判定引擎)。sqlServer 走原子刪除:單一交易內
        // drop schema → 寫 audit_log → 刪 dbo.project_access／project_registry（控制面第四輪 §2,單庫唯一管家）,
        // 故不再另呼 registry.UnregisterAsync（清單即時消失、可見性同步移除由同一交易保證,無兩連線半刪窗口）。
        // schema drop 失敗殘留的孤兒由建案預檢擋同名、待 dev.db.reconcile 清。sqlite/duckdb 刪本地檔、不觸及 registry。
        string? cleanupMessage = null;
        var databaseDeleted = false;
        try
        {
            await databaseDeleter.DeleteAsync(projectId, cancellationToken);
            databaseDeleted = true;

            // 2) 刪 project.json 資料夾（交易外 best-effort 本地快取清理）。DB 刪除交易已 commit 時，
            // folder-cleanup 失敗只回 message，不把已成功的硬刪誤報為失敗。
            try
            {
                await projectStore.DeleteAsync(projectId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                cleanupMessage = "案件已刪除；本機快取資料夾清理失敗，可稍後手動移除。";
            }
        }
        finally
        {
            if (databaseDeleted)
            {
                // DB commit 後案件已不存在；即使 folder cleanup 被取消，也不可留下指向已刪案件的 session／鎖。
                session.Leave(projectId);
                heldDeletionLease.Complete();
            }
        }

        if (cleanupMessage is null)
        {
            return new { ok = true, projectId };
        }

        return new { ok = true, projectId, message = cleanupMessage };
    }
}
