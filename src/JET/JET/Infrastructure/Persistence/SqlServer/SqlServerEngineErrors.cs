using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// SQL Server 引擎錯誤的單一映射點（錯誤碼登錄在 <see cref="JetErrorCodes"/>）：
/// 把常見 <see cref="SqlException"/> 轉譯為明確的 <see cref="JetActionException"/> 錯誤碼，
/// 取代裸 <c>bridge_error</c>。由 dispatcher 在 action 例外出口統一呼叫（composition 注入，
/// Bridge 只見 delegate、不依賴 Infrastructure），涵蓋所有 sqlServer 專案操作、不逐 repository 貼片。
/// 另提供死鎖有限次重試（指數退避）：多人共用單庫後 1205 不再是理論風險，
/// dbo 共用管理表（dbo.project_registry／project_access）的寫入以它包裹。
/// </summary>
public static class SqlServerEngineErrors
{
    private const int LoginFailedNumber = 18456;          // 登入失敗（帳密錯、登入未生效）
    private const int DuplicateKeyIndexNumber = 2601;     // 唯一索引重複
    private const int DuplicateKeyConstraintNumber = 2627; // PK/UNIQUE 條件約束重複
    private const int DeadlockVictimNumber = 1205;        // 死鎖犧牲者（交易已 rollback）
    private const int ClientTimeoutNumber = -2;           // SqlClient 用戶端逾時（連線/指令）
    private const int Win32WaitTimeoutNumber = 258;       // WAIT_TIMEOUT（新版 SqlClient 部分逾時以此呈現）

    /// <summary>死鎖重試上限（含首次嘗試共 3 次）；耗盡後原樣拋出，由映射點轉 sql_server_deadlock。</summary>
    public const int DeadlockMaxAttempts = 3;

    private static readonly TimeSpan DefaultRetryBaseDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 嘗試把例外（含 InnerException 鏈上的 <see cref="SqlException"/>）轉譯為明確錯誤碼；
    /// 不可辨識時回 null（維持既有 bridge_error fallback）。已是 <see cref="JetActionException"/> 的
    /// 業務錯誤不會進到這裡（dispatcher 先行放行）。
    /// </summary>
    public static JetActionException? TryTranslate(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                return TranslateSqlException(sql);
            }
        }

        return null;
    }

    /// <summary>例外（含 InnerException 鏈）是否為死鎖犧牲者（1205）。</summary>
    public static bool IsDeadlock(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql && HasNumber(sql, DeadlockVictimNumber))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 以有限次重試（指數退避：base、2×base）執行單交易操作；死鎖犧牲者的交易已被引擎 rollback，
    /// 重跑整個操作是安全的（呼叫端須保證 operation 自含完整交易、無交易外副作用）。
    /// 重試耗盡後原樣拋出最後一次的死鎖例外——由 <see cref="TryTranslate"/> 在映射點轉為
    /// <c>sql_server_deadlock</c>。非死鎖例外不重試、立即拋出。
    /// </summary>
    public static async Task<T> ExecuteWithDeadlockRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        TimeSpan? retryBaseDelay = null)
    {
        var baseDelay = retryBaseDelay ?? DefaultRetryBaseDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (Exception exception) when (attempt < DeadlockMaxAttempts && IsDeadlock(exception))
            {
                // 指數退避：第 1 次重試等 base、第 2 次等 2×base（錯開兩個併發者的節奏）。
                await Task.Delay(baseDelay * attempt, cancellationToken);
            }
        }
    }

    /// <inheritdoc cref="ExecuteWithDeadlockRetryAsync{T}"/>
    public static Task ExecuteWithDeadlockRetryAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken,
        TimeSpan? retryBaseDelay = null)
    {
        return ExecuteWithDeadlockRetryAsync<object?>(
            async ct =>
            {
                await operation(ct);
                return null;
            },
            cancellationToken,
            retryBaseDelay);
    }

    private static JetActionException? TranslateSqlException(SqlException sql)
    {
        if (HasNumber(sql, DeadlockVictimNumber))
        {
            return new JetActionException(
                JetErrorCodes.SqlServerDeadlock,
                "SQL Server 死鎖：本操作被引擎選為死鎖犧牲者且自動重試仍失敗（或該操作未包重試）。請稍後重試。");
        }

        if (HasNumber(sql, LoginFailedNumber))
        {
            return new JetActionException(
                JetErrorCodes.SqlServerLoginFailed,
                "SQL Server 登入失敗：請檢查環境變數 JET_SQLSERVER_CONNECTION 中的帳號與密碼；" +
                "若剛啟用混合驗證模式，需重啟 SQL Server 服務後登入才生效。");
        }

        if (HasNumber(sql, DuplicateKeyIndexNumber) || HasNumber(sql, DuplicateKeyConstraintNumber))
        {
            // 保留引擎原文：內含索引/資料表名與重複鍵值，是辨識衝突來源的關鍵資訊。
            return new JetActionException(
                JetErrorCodes.DuplicateKey,
                $"唯一鍵衝突：{sql.Message}");
        }

        if (HasNumber(sql, ClientTimeoutNumber) || HasNumber(sql, Win32WaitTimeoutNumber))
        {
            return new JetActionException(
                JetErrorCodes.SqlServerTimeout,
                "SQL Server 執行逾時：伺服器暫時過載或不可達。請確認伺服器狀態後重試。");
        }

        return null;
    }

    /// <summary>掃整個 Errors 集合（首錯之外的批次錯誤也算），不只看 <see cref="SqlException.Number"/>。</summary>
    private static bool HasNumber(SqlException sql, int number)
    {
        if (sql.Number == number)
        {
            return true;
        }

        foreach (SqlError error in sql.Errors)
        {
            if (error.Number == number)
            {
                return true;
            }
        }

        return false;
    }
}
