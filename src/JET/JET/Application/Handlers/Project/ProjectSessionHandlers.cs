using System.Text.Json;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// project.saveProgress：保存使用者目前所在的流程步驟（resume 位置）。
/// 與匯入/配對的 AdvanceStep（只前進不後退）不同，本 action 記錄使用者實際所在位置，允許倒退。
/// </summary>
public sealed class ProjectSaveProgressHandler(
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    /// <summary>6 步模型的最大步驟索引。</summary>
    private const int MaxStepIndex = 5;

    public string Action => "project.saveProgress";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();

        var currentStep = PayloadReader.GetOptionalInt(payload, "currentStep")
            ?? throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "current_step_missing");

        if (currentStep < 0 || currentStep > MaxStepIndex)
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "current_step_out_of_range");
        }

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                ProjectFolderMessages.ActiveProjectMissing);

        if (document.CurrentStep != currentStep)
        {
            await projectStore.SaveAsync(document with { CurrentStep = currentStep }, cancellationToken);
        }

        return new { ok = true, currentStep };
    }
}

/// <summary>
/// project.releaseLock：釋放當前 session 案件的工作鎖（不是自己持有的鎖不做事）。
/// 離場清理 action 由 handler 非阻塞試取與變更型作業相同的作業互斥鎖；取不到即 fail-fast，
/// 取到後才把放鎖與 session 離場原子包在同一 lease。前端只在真正離開專案時呼叫。無 active session
/// 專案時在取得互斥鎖後優雅 no-op 回 { ok }。只有底層放鎖成功才清掉取得互斥鎖後捕捉的 session 案件；
/// 失敗或取消時保留 session，讓使用者可安全重試。離開案件後關閉這個案件的資料庫連線，
/// 記錄檔併回資料庫檔，JET 不再開著案件資料庫（見 IProjectDatabaseCheckpoint）。
/// </summary>
public sealed class ProjectReleaseLockHandler(
    CurrentPrincipal principal,
    ProjectSession session,
    ActionExecutionGate executionGate,
    IProjectDatabaseCheckpoint? databaseCheckpoint = null,
    ILogger<ProjectReleaseLockHandler>? logger = null) : IApplicationActionHandler
{
    public string Action => JetActionNames.ProjectReleaseLock;

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        using var executionLease = executionGate.TryAcquire()
            ?? throw new JetActionException(
                JetErrorCodes.OperationInProgress,
                "另一項作業正在進行中，請待其完成後再操作。");

        if (session.Current is (var projectId, var repositories))
        {
            await repositories.LockService.ReleaseAsync(projectId, principal.Name, cancellationToken);
            session.Leave(projectId);
            if (databaseCheckpoint is not null)
            {
                await ProjectDatabaseCheckpointSteps.TryCloseAsync(databaseCheckpoint, projectId, logger);
            }
        }

        return new { ok = true };
    }
}
