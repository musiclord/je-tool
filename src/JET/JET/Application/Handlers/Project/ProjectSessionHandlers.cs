using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// project.saveProgress：保存使用者目前所在的流程步驟（resume 位置）。
/// 與匯入/配對的 AdvanceStep（只前進不後退）不同，本 action 記錄使用者實際所在位置，允許倒退。
/// </summary>
public sealed class ProjectSaveProgressHandler(
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    /// <summary>6 步模型的最大步驟索引（manifest Step Data Outline）。</summary>
    private const int MaxStepIndex = 5;

    public string Action => "project.saveProgress";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();

        var currentStep = PayloadReader.GetOptionalInt(payload, "currentStep")
            ?? throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "payload 缺少必填欄位 'currentStep'。");

        if (currentStep < 0 || currentStep > MaxStepIndex)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"欄位 'currentStep' 必須介於 0 與 {MaxStepIndex}，收到 {currentStep}。");
        }

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                $"找不到專案 '{projectId}'。");

        if (document.CurrentStep != currentStep)
        {
            await projectStore.SaveAsync(document with { CurrentStep = currentStep }, cancellationToken);
        }

        return new { ok = true, currentStep };
    }
}

/// <summary>
/// project.heartbeat（控制面第六輪租約鎖）：續租當前 session 專案的鎖（更新 heartbeat_utc，只更新自己持有的列）。
/// 背景保活 action、分類 concurrent（絕不被作業 busy 閘擋住）。sqlServer 續租；本地檔案鎖不需續期。
/// 無 active session 專案時優雅 no-op 回 { ok }（背景保活不因偶發無 session 而報錯）。
/// </summary>
public sealed class ProjectHeartbeatHandler(
    ILockService lockService,
    CurrentPrincipal principal,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "project.heartbeat";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.CurrentProjectId;
        if (projectId is not null)
        {
            await lockService.RenewAsync(projectId, principal.Name, cancellationToken);
        }

        return new { ok = true };
    }
}

/// <summary>
/// project.releaseLock（控制面第六輪租約鎖）：釋放當前 session 專案的鎖（只刪自己持有的列，釋放他人為 no-op）。
/// 離場清理 action 由 handler 非阻塞試取與變更型作業相同的 execution gate；取不到即 fail-fast，
/// 取到後才把放鎖與 session 離場原子包在同一 lease。前端只在真正離開專案時呼叫。無 active session
/// 專案時在取得閘後優雅 no-op 回 { ok }。只有底層放鎖成功才清掉取閘後捕捉的 session 案件；
/// 失敗或取消時保留 session 與心跳，讓使用者可安全重試。
/// </summary>
public sealed class ProjectReleaseLockHandler(
    ILockService lockService,
    CurrentPrincipal principal,
    ProjectSession session,
    ActionExecutionGate executionGate) : IApplicationActionHandler
{
    public string Action => "project.releaseLock";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        using var executionLease = executionGate.TryAcquire()
            ?? throw new JetActionException(
                JetErrorCodes.OperationInProgress,
                "另一項作業正在進行中，請待其完成後再操作。");

        var projectId = session.CurrentProjectId;
        if (projectId is not null)
        {
            await lockService.ReleaseAsync(projectId, principal.Name, cancellationToken);
            session.Leave(projectId);
        }

        return new { ok = true };
    }
}
