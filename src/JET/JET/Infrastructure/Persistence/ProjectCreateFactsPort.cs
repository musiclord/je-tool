using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// project.create 的技術執行 adapter。案件政策值由 Domain document 提供；
/// 資料夾、資料庫與工作鎖由 Infrastructure 執行。
/// </summary>
internal sealed class ProjectCreateFactsPort(
    IProjectStore projectStore,
    IProjectCreateBackendPort backendPort,
    ILockService lockService) : IProjectCreateFactsPort
{
    public async Task<ProjectCreateFacts> ExecuteAsync(
        ProjectCreatePlan plan,
        CancellationToken cancellationToken)
    {
        var document = plan.Document;
        IProjectCreateBackendAttempt? backendAttempt = null;
        var lockAcquiredThisCall = false;

        // 檔案鎖要先讀到 project.json，所以先發布設定檔；取得一把「本次新建」的工作鎖之後才建立資料庫。
        try
        {
            await projectStore.CreateAsync(document, cancellationToken);
        }
        catch (ProjectStoreCollisionException exception)
        {
            // JsonFileProjectStore 的同名檢查也涵蓋 TOCTOU 並行發布；在 case-create
            // adapter 這個具語意的邊界補上欄位歸屬，不讓通用 project store 猜 UI 欄位。
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                plan.HasUserSuppliedProjectName
                    ? $"案件位置已有名為「{exception.ProjectId}」的資料夾，請改用其他案件名稱，或先刪除該資料夾。"
                    : $"系統自動產生的案件名稱「{exception.ProjectId}」已被使用，請重新建立案件。",
                plan.HasUserSuppliedProjectName ? JetErrorFields.ProjectName : null);
        }
        try
        {
            await backendPort.PrepareAsync(document, cancellationToken);

            backendAttempt = await backendPort.BeginAsync(
                document,
                plan.Principal,
                cancellationToken);

            var lockOutcome = await lockService.AcquireAsync(
                document.ProjectId,
                plan.Principal,
                cancellationToken);
            if (lockOutcome is LockOutcome.Held held)
            {
                throw new ProjectCreateLockHeldException(
                    held.LockedBy,
                    held.MachineName,
                    held.LockedUtc);
            }
            if (lockOutcome is not LockOutcome.Acquired { NewlyAcquired: true })
            {
                // 新案不得繼承同 principal 既有鎖。若前一 attempt 的 release 失敗，fail closed
                // 可避免等待者在前一 attempt rollback 後，以即將消失的 reentrant lease 發布。
                throw new ProjectCreateExistingWorkLockException();
            }
            lockAcquiredThisCall = true;

            await backendAttempt.PrepareAsync(cancellationToken);

            return new ProjectCreateFacts(
                document,
                plan.Principal,
                lockAcquiredThisCall,
                backendAttempt);
        }
        catch
        {
            await RollbackCoreAsync(
                document,
                backendAttempt,
                lockAcquiredThisCall,
                plan.Principal,
                CancellationToken.None);

            throw;
        }
    }

    public async Task CompleteAsync(
        ProjectCreateFacts facts,
        CancellationToken cancellationToken)
    {
        await facts.BackendAttempt.CommitAsync(cancellationToken);
        try
        {
            // 資料庫建好、建案確定之後才刪建案中的標記。
            await projectStore.CompleteCreateAsync(facts.Document.ProjectId, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 建案已經完成，標記刪不掉不影響結果；下次開案確認資料庫已經建好後會再刪一次。
        }
    }

    public Task RollbackAsync(
        ProjectCreateFacts facts,
        CancellationToken cancellationToken) =>
        RollbackCoreAsync(
            facts.Document,
            facts.BackendAttempt,
            facts.LockAcquiredThisCall,
            facts.Principal,
            cancellationToken);

    private async Task RollbackCoreAsync(
        ProjectDocument document,
        IProjectCreateBackendAttempt? backendAttempt,
        bool lockAcquiredThisCall,
        string principal,
        CancellationToken cancellationToken)
    {
        var cleanupFailed = false;

        // 工作鎖必須先釋放，再回滾資料庫；否則同一個帳號等待中的建案可能先繼承舊鎖，隨後又被這次的釋放刪掉。
        if (lockAcquiredThisCall)
        {
            try
            {
                await lockService.ReleaseAsync(
                    document.ProjectId,
                    principal,
                    cancellationToken);
            }
            catch (Exception)
            {
                cleanupFailed = true;
            }
        }

        if (backendAttempt is not null)
        {
            try
            {
                await backendAttempt.RollbackAsync(cancellationToken);
            }
            catch (Exception)
            {
                cleanupFailed = true;
            }
        }

        if (cleanupFailed)
        {
            // 資料庫回滾或釋放工作鎖失敗時保留資料夾，留下判斷殘留狀態需要的依據。
            return;
        }

        try
        {
            await projectStore.DeleteAsync(document.ProjectId, cancellationToken);
        }
        catch (Exception)
        {
            // 補償失敗不得遮蔽原始 create failure。
        }
    }
}
