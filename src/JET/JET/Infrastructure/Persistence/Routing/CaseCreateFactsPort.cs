using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// project.create 的技術執行 adapter。案件政策值由 Domain document 提供；
/// backend collision、檔案、schema 與 registry 仍由 Infrastructure 執行。
/// </summary>
internal sealed class CaseCreateFactsPort(
    IProjectStore projectStore,
    IProjectDatabaseInitializer databaseInitializer,
    IProjectRegistry registry,
    ICaseCreateBackendPort backendPort,
    ILockService lockService) : ICaseCreateFactsPort
{
    public async Task PreflightAsync(
        CaseCreatePreflightRequest request,
        CancellationToken cancellationToken)
    {
        if (request.HasUserSuppliedCaseName
            && request.DatabaseProvider == ProjectDocument.SqlServerDatabaseProvider
            && (await databaseInitializer.DatabaseExistsAsync(
                    request.ProjectId,
                    request.DatabaseProvider,
                    cancellationToken)
                || await registry.ExistsAsync(request.ProjectId, cancellationToken)))
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"案件名稱『{request.ProjectId}』在 SQL Server 後端已有既有的案件資料（專案 schema），"
                + "但本機沒有對應的案件登記——可能是 projects 資料夾曾被手動刪除，或該案件由其他電腦建立。"
                + "請改用其他名稱；若要重用此名稱，需先清理後端殘留資料。",
                JetErrorFields.CaseName);
        }
    }

    public async Task<CaseCreateFacts> ExecuteAsync(
        CaseCreatePlan plan,
        CancellationToken cancellationToken)
    {
        var document = plan.Document;
        ICaseCreateBackendAttempt? backendAttempt = null;
        var lockAcquiredThisCall = false;

        // resolver 與本地 file lock 都要先讀到 project.json。SQL Begin 只取得同名 ownership，
        // 接著必須取得一把「本次新建」的工作鎖，才可 materialize provider backend；registry
        // 仍到 handler finalize／session staging 完成後才發布。
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
                plan.HasUserSuppliedCaseName
                    ? $"專案資料夾『{exception.ProjectId}』已存在，請換一個案件名稱。"
                    : $"產生的專案識別碼『{exception.ProjectId}』已存在，請重新建立案件。",
                plan.HasUserSuppliedCaseName ? JetErrorFields.CaseName : null);
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
                throw new CaseCreateLockHeldException(
                    held.LockedBy,
                    held.MachineName,
                    held.LockedUtc);
            }
            if (lockOutcome is not LockOutcome.Acquired { NewlyAcquired: true })
            {
                // 新案不得繼承同 principal 既有鎖。若前一 attempt 的 release 失敗，fail closed
                // 可避免等待者在前一 attempt rollback 後，以即將消失的 reentrant lease 發布。
                throw new CaseCreateExistingWorkLockException();
            }
            lockAcquiredThisCall = true;

            await backendAttempt.PrepareAsync(cancellationToken);

            return new CaseCreateFacts(
                document,
                plan.Principal,
                lockAcquiredThisCall,
                backendAttempt);
        }
        catch (CaseCreateBackendDifferentOwnerException)
        {
            // Another serialized SQL attempt has become authoritative；Begin 尚未取得工作鎖。
            await RollbackCoreAsync(
                document,
                backendAttempt: null,
                lockAcquiredThisCall: false,
                plan.Principal,
                CancellationToken.None);
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"案件名稱『{document.ProjectId}』在 SQL Server 後端已有既有的案件資料，請換一個。",
                plan.HasUserSuppliedCaseName ? JetErrorFields.CaseName : null);
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

    public Task CompleteAsync(
        CaseCreateFacts facts,
        CancellationToken cancellationToken) =>
        facts.BackendAttempt.CommitAsync(cancellationToken);

    public Task RollbackAsync(
        CaseCreateFacts facts,
        CancellationToken cancellationToken) =>
        RollbackCoreAsync(
            facts.Document,
            facts.BackendAttempt,
            facts.LockAcquiredThisCall,
            facts.Principal,
            cancellationToken);

    private async Task RollbackCoreAsync(
        ProjectDocument document,
        ICaseCreateBackendAttempt? backendAttempt,
        bool lockAcquiredThisCall,
        string principal,
        CancellationToken cancellationToken)
    {
        var cleanupFailed = false;

        // 工作鎖必須先釋放，backend ownership 最後才放行等待中的同名 attempt；否則同
        // principal 等待者可能先繼承舊 lease，隨後又被本 attempt 的 release 刪掉。
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
            // backend rollback 或 lock release 失敗時保留 folder，留下 provider／reconcile 錨點。
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
