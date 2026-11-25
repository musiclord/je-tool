using System.Text.Json;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// project.saveProgress：存下審計員目前停在流程的哪一步，重新載入案件時回到這一步。
/// 匯入與確認配對之後的 AdvanceStep 只會往前推進；這個 action 記的是審計員實際所在的步驟，可以比原本存的步驟小。
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
/// project.releaseLock：放掉目前 session 這個案件的工作鎖；鎖不是自己持有的就不做事。前端只在真的要離開案件時呼叫。
/// handler 先試著取得會改資料的 action 共用的那一把閘，取不到就直接回 operation_in_progress，不等待；
/// 取到之後，放鎖與離開 session 都在持有這把閘的期間完成。沒有開啟中的案件時，取得閘之後什麼都不做，回 { ok }。
/// 工作鎖放成功了，才清掉取得閘之後讀到的那個 session 案件；放鎖失敗或被取消時保留 session，審計員可以再試一次。
/// 離開案件後關閉這個案件的資料庫連線，記錄檔併回資料庫檔，JET 不再開著案件資料庫（見 IProjectDatabaseCheckpoint）。
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
            await ProjectSessionDeparture.LeaveAsync(
                projectId, repositories.LockService, expected => session.Leave(expected),
                principal.Name, databaseCheckpoint, logger, cancellationToken);
        }

        return new { ok = true };
    }
}
