using System.Text.Json;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// project.delete：永久刪除案件，刪了不能復原。先刪資料庫檔，再刪 project.json 所在的案件資料夾。
/// 從案件清單呼叫，不需要先開啟案件。資料庫刪除失敗時例外照樣往外丟，資料夾保留，排除問題後可以重試。
/// 不支援的資料庫種類在動任何檔案之前回 unsupported_provider，資料夾保持原狀。
/// </summary>
public sealed class ProjectDeleteHandler : IApplicationActionHandler
{
    private readonly IProjectStore _projectStore;
    private readonly ProjectRepositoryCatalog _repositoryCatalog;
    private readonly CurrentPrincipal _principal;
    private readonly ProjectSession _session;
    private readonly ILogger? _logger;
    private readonly ISupportCaseHistory? _supportHistory;

    internal ProjectDeleteHandler(
        IProjectStore projectStore,
        ProjectRepositoryCatalog repositoryCatalog,
        CurrentPrincipal principal,
        ProjectSession session,
        ILogger<ProjectDeleteHandler>? logger = null,
        ISupportCaseHistory? supportHistory = null)
    {
        this._projectStore = projectStore;
        this._repositoryCatalog = repositoryCatalog;
        this._principal = principal;
        this._session = session;
        this._logger = logger;
        this._supportHistory = supportHistory;
    }

    public string Action => "project.delete";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = PayloadReader.GetRequiredString(payload, "projectId");
        ProjectDocument? found;
        SupportOperationStage.Enter("metadata_read");
        try
        {
            found = await _projectStore.FindAsync(projectId, cancellationToken);
        }
        catch (JetActionException error) when (error.Code is JetErrorCodes.FileReadError or JetErrorCodes.InvalidProjectSchema)
        {
            // 設定檔損壞、版本較舊或期間無效的案件，畫面請使用者從備份還原或另建案件，舊的這份也要能從案件清單刪掉。
            // 只處理確定是 SQLite 的案件；判斷不出來時維持原本的錯誤。
            var localProvider = await _projectStore.FindLocalDatabaseProviderAsync(projectId, cancellationToken);
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

        // Windows 的路徑不分大小寫，所以 FindAsync 找到案件後，之後一律用 project.json 裡記的 projectId。
        // 不這樣做的話，session.Leave 會把只差大小寫的同一個案件當成兩個。
        projectId = document.ProjectId;

        return await DeleteAsync(projectId, document.DatabaseProvider, cancellationToken);
    }

    private async Task<object> DeleteAsync(string projectId, string databaseProvider, CancellationToken cancellationToken)
    {
        // 被刪的案件不一定是作用中案件，所以依它自己的 project.json 選資料庫組，不讀也不改 session 的那一組。
        // 未知種類在取刪案鎖之前回 unsupported_provider。
        var repositories = _repositoryCatalog.For(databaseProvider);
        var deletionLockService = repositories.DeletionLockService;
        var reportStore = repositories.ReportStore;
        var databaseDeleter = repositories.DatabaseDeleter;

        // 試著取得和 project.load 相同的作業系統檔案鎖，取不到不等待。鎖被別人持有時回 project_locked，
        // 這時還沒動到報告檔、資料庫與案件資料夾。
        SupportOperationStage.Enter("deletion_lock");
        var deletionLockOutcome = await deletionLockService.TryAcquireAsync(
            projectId,
            _principal.Name,
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

        // 拿到刪案鎖之後，才取得報告 store 的鎖；這把鎖放在案件資料夾外面。刪案時只用它擋住其他正在讀寫報告檔的程式，
        // 不先讀取或復原報告的 journal，因為 journal 馬上會跟著案件資料夾一起刪掉。這把鎖一直持有到資料庫與案件資料夾都處理完。
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

            // 2) 刪整個案件資料夾，包含報告、工作底稿及其他檔案。資料庫已經刪掉之後，
            // 資料夾刪不乾淨只在回應的 message 提醒，不把已經完成的刪除回報成失敗。
            SupportOperationStage.Enter("folder_delete");
            try
            {
                // 支援紀錄保留到刪除案件，跟著案件資料夾刪除。先停止寫入這個案件，
                // 紀錄檔不會在刪除時被占用；之後這個案件的事件只留在記憶體。
                if (_supportHistory is not null) await _supportHistory.ForgetAsync(projectId, CancellationToken.None);
                await _projectStore.DeleteAsync(projectId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 支援日誌記下刪不掉的原因，例如檔案正被開啟。
                if (_logger is not null) SupportStepDiagnostics.StepFailed(_logger, "folder_delete", exception);
                cleanupMessage = "案件已刪除，但案件資料夾未能完全刪除，請自行確認並刪除剩餘的檔案。";
            }
        }
        finally
        {
            if (databaseDeleted)
            {
                // 資料庫刪掉之後，案件就不存在了。即使刪資料夾時被取消，也不能留下指向這個案件的 session 或鎖。
                _session.Leave(projectId);
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
