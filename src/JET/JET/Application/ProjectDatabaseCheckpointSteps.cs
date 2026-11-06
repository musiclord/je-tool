using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// 呼叫案件資料庫收尾（<see cref="IProjectDatabaseCheckpoint"/>）的共用寫法。收尾失敗不改變作業結果，
/// 不往外丟例外，只在支援日誌記一筆步驟失敗。一律不帶呼叫端的取消：作業被取消時，記錄檔更需要清空。
/// </summary>
internal static class ProjectDatabaseCheckpointSteps
{
    public static async Task TryCheckpointAsync(IProjectDatabaseCheckpoint checkpoint, string projectId, ILogger? logger)
    {
        try
        {
            await checkpoint.CheckpointAsync(projectId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (logger is not null)
            {
                SupportStepDiagnostics.StepFailed(logger, "database_checkpoint", exception);
            }
        }
    }

    public static async Task TryCloseAsync(IProjectDatabaseCheckpoint checkpoint, string projectId, ILogger? logger)
    {
        try
        {
            await checkpoint.CloseAsync(projectId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (logger is not null)
            {
                SupportStepDiagnostics.StepFailed(logger, "database_close", exception);
            }
        }
    }
}

/// <summary>
/// 結束 JET 時關閉目前案件的資料庫連線。正常關窗會先經過 project.releaseLock 離開案件，這裡只處理沒有離開案件就結束的情形，
/// 例如畫面已經無法使用時由主機直接關窗。由 runtime 在所有要求結束後、放開案件鎖之前釋放。
/// </summary>
internal sealed class ProjectDatabaseShutdown(
    ProjectSession session,
    IProjectDatabaseCheckpoint databaseCheckpoint,
    ILogger? logger) : IDisposable
{
    private int disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0 || session.CurrentProjectId is not { } projectId)
        {
            return;
        }

        // 關窗時在介面執行緒上釋放；放到背景執行緒等它完成，資料庫呼叫的後續步驟不會排回被占住的介面執行緒。
        Task.Run(() => ProjectDatabaseCheckpointSteps.TryCloseAsync(databaseCheckpoint, projectId, logger))
            .GetAwaiter()
            .GetResult();
    }
}
