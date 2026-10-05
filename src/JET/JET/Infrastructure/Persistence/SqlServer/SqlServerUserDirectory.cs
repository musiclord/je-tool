using System.Data;
using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 使用者目錄 <see cref="IUserDirectory"/> 的 SQL Server 實作（<c>dbo.app_user</c>，在單庫 <c>JET</c>／測試 <c>JET_Test</c>
/// 的 <c>dbo</c>）。<b>天生只屬 sqlServer</b>（比照 <see cref="SqlServerProjectRegistry"/>）：不在依資料庫種類選定的資料庫組裡，
/// 直接持有 <see cref="SqlServerConnectionOptions"/> 對單庫開連線。bootstrap 冪等（IF OBJECT_ID IS NULL CREATE TABLE，
/// 並以 TRY/CATCH 吞 2714 併發建表競速），首次觸碰時 ensure；<c>principal</c> 鍵欄釘 <c>Latin1_General_BIN2</c>。
/// 連線未設定 → <see cref="JetActionException"/> code <c>sql_server_not_configured</c>（fail-loud，呼叫端 handler 退階）。
/// 這不是驗證機制：記錄「誰來過、給編號」，不證明「他是他」。
/// </summary>
public sealed class SqlServerUserDirectory(SqlServerConnectionOptions options) : IUserDirectory
{
    // 首次觸碰時 bootstrap dbo.app_user；之後同一實例跳過（表已在、單庫必存在）——比照 SqlServerProjectRegistry。
    private bool _tableEnsured;

    public async Task<AppUserRecord> EnsureUserAsync(
        string principal, string displayName, CancellationToken cancellationToken)
    {
        await using var connection = await OpenSingleDatabaseAsync(cancellationToken);

        var userId = await EnsureUserIdAsync(connection, principal, displayName, cancellationToken);

        // best-effort 戳記最近確認時間；失敗不影響「已取得編號」的主結果。
        await TryUpdateLastSeenAsync(connection, principal, cancellationToken);

        // 一次性收斂：早期版本的 project_access 列存裸帳號名，升級為合格名。輔助收斂不得阻斷註冊。
        await TryHealBarePrincipalAccessAsync(connection, principal, cancellationToken);

        return new AppUserRecord(userId, principal);
    }

    private static async Task<int> EnsureUserIdAsync(
        SqlConnection connection, string principal, string displayName, CancellationToken cancellationToken)
    {
        if (await SelectUserIdAsync(connection, principal, cancellationToken) is int existing)
        {
            return existing;
        }

        try
        {
            return await InsertUserAsync(connection, principal, displayName, cancellationToken);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // 唯一鍵競速（2601 唯一索引重複／2627 PK/UNIQUE 違反）：另一連線先插了同 principal → 重查取其編號。
            if (await SelectUserIdAsync(connection, principal, cancellationToken) is int raced)
            {
                return raced;
            }

            throw; // 競速後仍查無（不應發生）——不吞真正的錯，讓真相上拋。
        }
    }

    private static async Task<int?> SelectUserIdAsync(
        SqlConnection connection, string principal, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id FROM dbo.app_user WHERE principal = @principal;";
        command.Parameters.AddWithValue("@principal", principal);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    private static async Task<int> InsertUserAsync(
        SqlConnection connection, string principal, string displayName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO dbo.app_user (principal, display_name, created_utc, last_seen_utc)
            OUTPUT INSERTED.user_id
            VALUES (@principal, @displayName, @now, @now);
            """;
        command.Parameters.AddWithValue("@principal", principal);
        command.Parameters.AddWithValue("@displayName", displayName);
        command.Parameters.Add("@now", SqlDbType.DateTime2).Value = DateTimeOffset.UtcNow.UtcDateTime;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task TryUpdateLastSeenAsync(
        SqlConnection connection, string principal, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE dbo.app_user SET last_seen_utc = @now WHERE principal = @principal;";
            command.Parameters.Add("@now", SqlDbType.DateTime2).Value = DateTimeOffset.UtcNow.UtcDateTime;
            command.Parameters.AddWithValue("@principal", principal);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException)
        {
            // best-effort：戳記失敗不影響已取得的編號。
        }
    }

    private static async Task TryHealBarePrincipalAccessAsync(
        SqlConnection connection, string principal, CancellationToken cancellationToken)
    {
        // 一次性收斂：早期版本的 dbo.project_access.principal 存的是裸帳號名。身分升級為合格化
        // 格式後，把「等於自己短名」的 access 列升級為合格名；目標列已存在則跳過（NOT EXISTS 防主鍵衝突）。
        // 已知取捨：同短名的跨網域他人列會被誤收編——早期資料僅存於開發機，風險可接受。日後改用人員名單授權時移除本段。
        var separator = principal.LastIndexOf('\\');
        if (separator < 0)
        {
            return; // 非合格化格式（無網域）→ 無裸名可升級。
        }

        var bare = principal[(separator + 1)..];
        if (bare.Length == 0)
        {
            return;
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE dbo.project_access
                SET principal = @qualified
                WHERE principal = @bare
                  AND NOT EXISTS (
                      SELECT 1 FROM dbo.project_access p2
                      WHERE p2.project_id = dbo.project_access.project_id AND p2.principal = @qualified);
                """;
            command.Parameters.AddWithValue("@qualified", principal);
            command.Parameters.AddWithValue("@bare", bare);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException)
        {
            // 輔助收斂不得阻斷註冊：project_access 尚未建立、或 UPDATE 失敗都吞掉。
        }
    }

    /// <summary>開單庫連線並確保 app_user 就位（fail-loud：單庫不存在則 OpenAsync 拋，由呼叫端 handler 退階）。</summary>
    private async Task<SqlConnection> OpenSingleDatabaseAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(BuildConnectionString(options.SingleDatabaseName));
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);
        return connection;
    }

    private async Task EnsureTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (_tableEnsured)
        {
            return;
        }

        // 冪等 bootstrap（principal 釘 BIN2）。TRY/CATCH 吞併發建表競速（2714＝物件已存在，屬另一
        // 連線先建成，其餘錯誤照拋）——多個 dispatcher/測試並行首次觸碰可能競速。
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            BEGIN TRY
                IF OBJECT_ID(N'dbo.app_user','U') IS NULL
                    CREATE TABLE dbo.app_user (
                        user_id       INT IDENTITY(1,1) PRIMARY KEY,
                        principal     NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL UNIQUE,
                        display_name  NVARCHAR(128) NOT NULL,
                        created_utc   DATETIME2 NOT NULL,
                        last_seen_utc DATETIME2 NULL
                    );
            END TRY BEGIN CATCH
                IF ERROR_NUMBER() <> 2714 THROW;
            END CATCH;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        _tableEnsured = true;
    }

    private string BuildConnectionString(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(options.BaseConnectionString))
        {
            throw new JetActionException(
                JetErrorCodes.SqlServerNotConfigured,
                "未設定 SQL Server 連線。使用者目錄需要環境變數 JET_SQLSERVER_CONNECTION。");
        }

        return new SqlConnectionStringBuilder(options.BaseConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;
    }
}
