using JET.Infrastructure;
using Microsoft.Extensions.Logging;

namespace JET;

internal static class SqlServerStartupOrchestration
{
    internal static Func<CancellationToken, Task> Create(
        string probeConnectionString,
        string singleDatabaseName,
        bool ensureDatabaseOnStartup,
        ILogger logger,
        Func<string, CancellationToken, Task<HealthResult>> probeAsync,
        Func<CancellationToken, Task> ensureDatabaseReadyAsync) =>
        async cancellationToken =>
        {
            var health = await probeAsync(probeConnectionString, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (health.Ok)
            {
                logger.LogInformation("{HealthMessage}", health.Message);
            }
            else
            {
                logger.LogWarning("{HealthMessage}", health.Message);
            }

            // 開啟即建庫（正式 app）：確保單庫存在（不存在則以設定登入建立）與反查表就位。
            // 非致命——登入未生效（剛切混合模式未重啟）或伺服器不可達時只記警告，稍後建案再試。
            if (ensureDatabaseOnStartup)
            {
                try
                {
                    await ensureDatabaseReadyAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    logger.LogInformation(
                        "SQL Server 單一資料庫 {Database} 已就緒（不存在則已建立）。", singleDatabaseName);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // application 正在關閉；取消屬正常生命週期，不寫成啟動失敗。
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "啟動時確保 SQL Server 單一資料庫失敗（非致命，稍後建案時再試）。");
                }
            }
        };
}
