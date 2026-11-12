using System.Text.Json;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// project.delete：永久刪除專案（硬刪不可復原）。先刪資料庫檔，再刪 project.json 所在的案件資料夾。不需 active project
/// （從專案選擇畫面呼叫）。資料庫刪除失敗則例外冒泡、資料夾保留，供修正後重試。不支援的資料庫種類在動任何檔案之前
/// 回 unsupported_provider，資料夾保持原狀。
/// </summary>
public sealed class ProjectDeleteHandler : IApplicationActionHandler
{
    private readonly IProjectStore projectStore;
    private readonly ProjectRepositoryCatalog repositoryCatalog;
    private readonly CurrentPrincipal principal;
    private readonly ProjectSession session;
    private readonly ILogger? logger;
    private readonly ISupportCaseHistory? supportHistory;

    internal ProjectDeleteHandler(
        IProjectStore projectStore,
        ProjectRepositoryCatalog repositoryCatalog,
        CurrentPrincipal principal,
        ProjectSession session,
        ILogger<ProjectDeleteHandler>? logger = null,
        ISupportCaseHistory? supportHistory = null)
    {
        this.projectStore = projectStore;
        this.repositoryCatalog = repositoryCatalog;
        this.principal = principal;
        this.session = session;
        this.logger = logger;
        this.supportHistory = supportHistory;
    }

    public string Action => "project.delete";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = PayloadReader.GetRequiredString(payload, "projectId");
        ProjectDocument? found;
        SupportOperationStage.Enter("metadata_read");
        try
        {
            found = await projectStore.FindAsync(projectId, cancellationToken);
        }
        catch (JetActionException error) when (error.Code is JetErrorCodes.FileReadError or JetErrorCodes.InvalidProjectSchema)
        {
            // 設定檔損壞、舊版或期間無效的案件，畫面請審計員從備份還原或另建案件，舊的這份也要能從案件清單刪掉。
            // 只處理確定是 SQLite 的案件；判斷不出來時維持原本的錯誤。
            var localProvider = await projectStore.FindLocalDatabaseProviderAsync(projectId, cancellationToken);
            if (localProvider is null)
            {
                throw;
            }

            return await DeleteAsync(projectId, localProvider, cancellationToken);
        }

        var document = found
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                ProjectFolderMessages.CannotDelete(projectId));

        // Windows 本地路徑大小寫不敏感；FindAsync 命中後，後續只用文件內的 canonical id。
        // 否則 session Leave 會把同一案件的大小寫變體當成兩個身分。
        projectId = document.ProjectId;

        return await DeleteAsync(projectId, document.DatabaseProvider, cancellationToken);
    }

    private async Task<object> DeleteAsync(string projectId, string databaseProvider, CancellationToken cancellationToken)
    {
        // 被刪的案件不一定是作用中案件，所以依它自己的 project.json 選資料庫組，不讀也不改 session 的那一組。
        // 未知種類在取刪案鎖之前回 unsupported_provider。
        var repositories = repositoryCatalog.For(databaseProvider);
        var deletionLockService = repositories.DeletionLockService;
        var reportStore = repositories.ReportStore;
        var databaseDeleter = repositories.DatabaseDeleter;

        // 以與 project.load 相同的 OS 檔案鎖做非阻塞試取。Held 時在報告檔、資料庫與案件資料夾有任何變更之前止步。
        SupportOperationStage.Enter("deletion_lock");
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

        // 取得刪案鎖後才取得案件外的報告租約。刪案路徑只排除其他讀寫報告檔的程式，不先
        // 讀取或復原即將一併刪除的 journal；lease 一路保持到 provider 資料與案件資料夾處理完成。
        SupportOperationStage.Enter("report_lease");
        await using var reportDeletionLease = await reportStore.AcquireProjectDeletionLeaseAsync(
            projectId,
            cancellationToken);

        // 1) 先刪資料庫檔。要在刪資料夾之前做，因為資料夾裡的 project.json 記錄資料庫種類，失敗重試時仍要讀它。
        string? cleanupMessage = null;
        var databaseDeleted = false;
        try
        {
            SupportOperationStage.Enter("database_delete");
            await databaseDeleter.DeleteAsync(projectId, cancellationToken);
            databaseDeleted = true;

            // 2) 刪整個案件資料夾，包含報告、工作底稿及其他檔案。DB 刪除交易已 commit 時，
            // folder-cleanup 失敗只回 message，不把已成功的硬刪誤報為失敗。
            SupportOperationStage.Enter("folder_delete");
            try
            {
                // 支援紀錄保留到刪除案件，跟著案件資料夾刪除。先停止寫入這個案件，
                // 紀錄檔不會在刪除時被占用；之後這個案件的事件只留在記憶體。
                if (supportHistory is not null) await supportHistory.ForgetAsync(projectId, CancellationToken.None);
                await projectStore.DeleteAsync(projectId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 支援日誌記下刪不掉的原因，例如檔案正被開啟。
                if (logger is not null) SupportStepDiagnostics.StepFailed(logger, "folder_delete", exception);
                cleanupMessage = "案件已刪除，但案件資料夾未能完全刪除，請自行確認並刪除剩餘的檔案。";
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
