using System.Data;
using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 專案租約鎖 <see cref="ILockService"/> 的 SQL Server 真實作（<c>dbo.project_lock</c>）。
/// <b>天生只屬 sqlServer</b>：不在依資料庫種類選定的資料庫組裡，直接持有 <see cref="SqlServerConnectionOptions"/> 對單庫開連線
/// （與 <see cref="SqlServerProjectRegistry"/>／<see cref="SqlServerAppConfigStore"/> 平行）。表隨
/// <see cref="SqlServerControlPlaneSchema"/> bootstrap；每個公開方法開頭 ensure（冪等）。
/// <para>
/// 取鎖為單一交易＋<c>HOLDLOCK</c> 原子臨界區：MERGE 對該鍵範圍持鎖至 commit，並行取鎖序列化——
/// 兩並行 AcquireAsync 恰一 <see cref="LockOutcome.Acquired"/>。MERGE 同時回讀更新前持有人，區分本次新取／接管
/// 與同 principal 既有鎖；回讀 <c>locked_by</c>：＝當前 principal → Acquired；
/// 否則→ <see cref="LockOutcome.Held"/>（他人未過期租約）。逾時門檻 <c>@timeout</c> 讀自 <c>dbo.app_config</c>
/// （<see cref="ProjectLockDefaults.TimeoutSecondsKey"/>，缺鍵回程式常數 120）。
/// </para>
/// 心跳只更新自己持有的列、釋放只刪自己持有的列（他人的為 no-op）。以
/// <see cref="SqlServerEngineErrors.ExecuteWithDeadlockRetryAsync"/> 包裹交易型操作（自含完整交易，rollback 後重跑安全）。
/// 為什麼是租約表＋心跳而非 <c>sp_getapplock</c>：JET 每指令開短連線、不留長連線，連線一關 applock 就掉，撐不住
/// 「整個開啟期間持鎖」；租約表＋背景心跳與短連線模型相容。
/// </summary>
public sealed class SqlServerLockService(
    SqlServerConnectionOptions options, IAppConfigStore appConfig) : ILockService
{
    // 首次觸碰時 bootstrap dbo 管理表；之後同一實例跳過（表已在、單庫必存在）——參考 SqlServerProjectRegistry。
    private bool _tablesEnsured;

    public Task<LockOutcome> AcquireAsync(string projectId, string principal, CancellationToken cancellationToken) =>
        SqlServerEngineErrors.ExecuteWithDeadlockRetryAsync(
            ct => AcquireOnceAsync(projectId, principal, ct), cancellationToken);

    private async Task<LockOutcome> AcquireOnceAsync(
        string projectId, string principal, CancellationToken cancellationToken)
    {
        var timeoutSeconds = await ReadTimeoutSecondsAsync(cancellationToken);

        await using var connection = await OpenSingleDatabaseAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        string lockedBy;
        string machineName;
        DateTime lockedUtc;
        bool newlyAcquired;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            // HOLDLOCK 讓 MERGE 對該鍵範圍持鎖至 commit（並行取鎖序列化）。MATCHED 分支：自己續租、或他人租約已逾時→接管。
            // machine_name / locked_utc / heartbeat_utc 一律伺服器端取值（HOST_NAME()＝連線工作站名＝持有人機器）。
            // OUTPUT 保留更新前持有人：insert／接管為本次新取得；同 principal 續持為既有鎖。這讓 load 的失敗
            // 補償只釋放本次新鎖，不會誤放同案 reload 前已持有的鎖。
            command.CommandText =
                """
                DECLARE @acquired TABLE (
                    previous_locked_by NVARCHAR(128) NULL,
                    locked_by          NVARCHAR(128) NOT NULL,
                    machine_name       NVARCHAR(128) NOT NULL,
                    locked_utc         DATETIME2 NOT NULL
                );
                MERGE dbo.project_lock WITH (HOLDLOCK) AS t
                USING (SELECT @id AS project_id) AS s ON t.project_id = s.project_id
                WHEN MATCHED AND (t.locked_by = @principal
                                  OR t.heartbeat_utc < DATEADD(SECOND, -@timeout, SYSUTCDATETIME())) THEN
                    UPDATE SET locked_by = @principal, machine_name = HOST_NAME(),
                               locked_utc = SYSUTCDATETIME(), heartbeat_utc = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN
                    INSERT (project_id, locked_by, machine_name, locked_utc, heartbeat_utc)
                    VALUES (@id, @principal, HOST_NAME(), SYSUTCDATETIME(), SYSUTCDATETIME())
                OUTPUT deleted.locked_by, inserted.locked_by, inserted.machine_name, inserted.locked_utc
                    INTO @acquired;

                IF EXISTS (SELECT 1 FROM @acquired)
                    SELECT locked_by, machine_name, locked_utc,
                           CAST(CASE WHEN previous_locked_by = @principal THEN 0 ELSE 1 END AS bit) AS newly_acquired
                    FROM @acquired;
                ELSE
                    SELECT locked_by, machine_name, locked_utc, CAST(0 AS bit) AS newly_acquired
                    FROM dbo.project_lock
                    WHERE project_id = @id;
                """;
            command.Parameters.AddWithValue("@id", projectId);
            command.Parameters.AddWithValue("@principal", principal);
            command.Parameters.Add("@timeout", SqlDbType.Int).Value = timeoutSeconds;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                // MERGE 恆使該列存在，SELECT 必回一列；理論上不可達。防禦性拋出，讓上層看到異常而非誤判取得。
                throw new JetActionException(
                    JetErrorCodes.ProjectLocked, "取鎖後回讀不到鎖列（不預期）。");
            }

            lockedBy = reader.GetString(0);
            machineName = reader.GetString(1);
            lockedUtc = reader.GetDateTime(2);
            newlyAcquired = reader.GetBoolean(3);
        }

        await transaction.CommitAsync(cancellationToken);

        return string.Equals(lockedBy, principal, StringComparison.Ordinal)
            ? new LockOutcome.Acquired(newlyAcquired)
            : new LockOutcome.Held(lockedBy, machineName, new DateTimeOffset(lockedUtc, TimeSpan.Zero));
    }

    public Task RenewAsync(string projectId, string principal, CancellationToken cancellationToken) =>
        SqlServerEngineErrors.ExecuteWithDeadlockRetryAsync(
            ct => RenewOnceAsync(projectId, principal, ct), cancellationToken);

    private async Task RenewOnceAsync(string projectId, string principal, CancellationToken cancellationToken)
    {
        await using var connection = await OpenSingleDatabaseAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // 只更新自己持有的列：被他人接管（locked_by 已變）則 0 列受影響、no-op（不奪回鎖）。
        command.CommandText =
            "UPDATE dbo.project_lock SET heartbeat_utc = SYSUTCDATETIME() WHERE project_id = @id AND locked_by = @principal;";
        command.Parameters.AddWithValue("@id", projectId);
        command.Parameters.AddWithValue("@principal", principal);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task ReleaseAsync(string projectId, string principal, CancellationToken cancellationToken) =>
        SqlServerEngineErrors.ExecuteWithDeadlockRetryAsync(
            ct => ReleaseOnceAsync(projectId, principal, ct), cancellationToken);

    private async Task ReleaseOnceAsync(string projectId, string principal, CancellationToken cancellationToken)
    {
        await using var connection = await OpenSingleDatabaseAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // 只刪自己持有的列：釋放他人的鎖為 no-op（防誤放）。
        command.CommandText = "DELETE FROM dbo.project_lock WHERE project_id = @id AND locked_by = @principal;";
        command.Parameters.AddWithValue("@id", projectId);
        command.Parameters.AddWithValue("@principal", principal);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectLockInfo>> ListActiveAsync(CancellationToken cancellationToken)
    {
        var timeoutSeconds = await ReadTimeoutSecondsAsync(cancellationToken);

        await using var connection = await TryOpenSingleDatabaseAsync(cancellationToken);
        if (connection is null)
        {
            return []; // 單庫尚未建立 → 無鎖可列（伺服器可達但 dbo 管理表尚未建立）。
        }

        await using var command = connection.CreateCommand();
        // 只回未過期的租約（heartbeat_utc 仍在逾時窗內）：過期的視同無人持有、不顯示鎖徽章。
        command.CommandText =
            """
            SELECT project_id, locked_by, machine_name, locked_utc
            FROM dbo.project_lock
            WHERE heartbeat_utc >= DATEADD(SECOND, -@timeout, SYSUTCDATETIME());
            """;
        command.Parameters.Add("@timeout", SqlDbType.Int).Value = timeoutSeconds;

        var results = new List<ProjectLockInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ProjectLockInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero)));
        }

        return results;
    }

    /// <summary>逾時秒數（秒）：讀 app_config 的 lock.timeoutSeconds，缺鍵／不可解析回程式常數 120。</summary>
    private async Task<int> ReadTimeoutSecondsAsync(CancellationToken cancellationToken)
    {
        var raw = await appConfig.GetAsync(ProjectLockDefaults.TimeoutSecondsKey, cancellationToken);
        return ProjectLockDefaults.ParsePositive(raw, ProjectLockDefaults.TimeoutSeconds);
    }

    /// <summary>開單庫連線並確保 dbo 管理表就位（fail-loud：Acquire/Renew/Release 期間單庫必存在——專案已建/載入）。</summary>
    private async Task<SqlConnection> OpenSingleDatabaseAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(BuildConnectionString(options.SingleDatabaseName));
        await connection.OpenAsync(cancellationToken);
        await EnsureTablesAsync(connection, cancellationToken);
        return connection;
    }

    /// <summary>開單庫連線；單庫尚未建立時回 null（ListActiveAsync 在全新環境據此優雅回空）。</summary>
    private async Task<SqlConnection?> TryOpenSingleDatabaseAsync(CancellationToken cancellationToken)
    {
        if (!_tablesEnsured && !await SingleDatabaseExistsAsync(cancellationToken))
        {
            return null;
        }

        return await OpenSingleDatabaseAsync(cancellationToken);
    }

    /// <summary>
    /// 單庫是否已存在（master 依賴最小化，與 <see cref="SqlServerProjectRegistry"/> 同語意）：
    /// AssumeDatabaseExists 或 process 級就緒已確認時一律當「已存在」、不連 master；否則連 master、DB_ID。
    /// </summary>
    private async Task<bool> SingleDatabaseExistsAsync(CancellationToken cancellationToken)
    {
        var readinessKey = SqlServerSingleDatabaseReadiness.KeyFor(options);
        if (options.AssumeDatabaseExists || SqlServerSingleDatabaseReadiness.IsConfirmed(readinessKey))
        {
            return true;
        }

        await using var master = new SqlConnection(BuildConnectionString("master"));
        await master.OpenAsync(cancellationToken);
        await using var command = master.CreateCommand();
        command.CommandText = "SELECT DB_ID(@db);";
        command.Parameters.AddWithValue("@db", options.SingleDatabaseName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        var exists = result is not null and not DBNull;
        if (exists)
        {
            SqlServerSingleDatabaseReadiness.MarkConfirmed(readinessKey);
        }

        return exists;
    }

    private async Task EnsureTablesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (_tablesEnsured)
        {
            return;
        }

        await SqlServerControlPlaneSchema.EnsureAsync(connection, cancellationToken);
        _tablesEnsured = true;
    }

    private string BuildConnectionString(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(options.BaseConnectionString))
        {
            throw new JetActionException(
                JetErrorCodes.SqlServerNotConfigured,
                "未設定 SQL Server 連線。專案租約鎖需要環境變數 JET_SQLSERVER_CONNECTION。");
        }

        return new SqlConnectionStringBuilder(options.BaseConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;
    }
}
