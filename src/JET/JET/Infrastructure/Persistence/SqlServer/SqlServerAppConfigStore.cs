using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// <see cref="IAppConfigStore"/> 的 SQL Server 實作（<c>dbo.app_config</c>）。
/// <b>天生只屬 sqlServer</b>：不在依資料庫種類選定的資料庫組裡,直接持有 <see cref="SqlServerConnectionOptions"/> 對單庫開連線
/// （與 <see cref="SqlServerProjectRegistry"/> 平行）。表隨 <see cref="SqlServerControlPlaneSchema"/> bootstrap；
/// 每個公開方法開頭 ensure（冪等）。UPSERT 走 <c>MERGE</c>,<c>updated_by = SUSER_SNAME()</c>、
/// <c>updated_utc = SYSUTCDATETIME()</c>（伺服器端取值,不採 client 自報）。
/// </summary>
public sealed class SqlServerAppConfigStore(SqlServerConnectionOptions options) : IAppConfigStore
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_json FROM dbo.app_config WHERE [key] = @key;";
        command.Parameters.AddWithValue("@key", key);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : (string)result;
    }

    public async Task SetAsync(string key, string valueJson, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // MERGE UPSERT:同 key 覆寫、不重複。updated_by/updated_utc 一律伺服器端取值。
        command.CommandText =
            """
            MERGE dbo.app_config AS target
            USING (SELECT @key AS [key]) AS source ON target.[key] = source.[key]
            WHEN MATCHED THEN
                UPDATE SET value_json = @value, updated_by = SUSER_SNAME(), updated_utc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT ([key], value_json, updated_by, updated_utc)
                VALUES (@key, @value, SUSER_SNAME(), SYSUTCDATETIME());
            """;
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", valueJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(BuildConnectionString(options.SingleDatabaseName));
        await connection.OpenAsync(cancellationToken);
        await SqlServerControlPlaneSchema.EnsureAsync(connection, cancellationToken);
        return connection;
    }

    private string BuildConnectionString(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(options.BaseConnectionString))
        {
            throw new JetActionException(
                JetErrorCodes.SqlServerNotConfigured,
                "未設定 SQL Server 連線。跨專案系統設定需要環境變數 JET_SQLSERVER_CONNECTION。");
        }

        return new SqlConnectionStringBuilder(options.BaseConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;
    }
}
