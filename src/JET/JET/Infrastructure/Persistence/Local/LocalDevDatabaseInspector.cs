using System.Data.Common;
using System.Globalization;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 開發階段本地引擎檢視工具（SQLite／DuckDB 共用）——**獨立唯讀路徑**（dev.db.* action 使用）：
/// 以 ReadOnly 連線直讀磁碟檔，零副作用（不建 schema、不寫入），
/// 看到的必然是已持久化資料。DB 檔不存在 → file_not_found（不建檔）。
/// 「列出資料表」與「引擎版本」查詢走 <see cref="ISqlDialect.ListTablesSql"/>／
/// <see cref="ISqlDialect.EngineVersionSql"/>（兩本地引擎各自表述、不在此 if-else 引擎名）；
/// table 名稱一律先比對該清單白名單，識別子插值只使用白名單內的名稱（雙引號 + "" 跳脫）；
/// LIMIT/OFFSET 參數化。
/// </summary>
public sealed class LocalDevDatabaseInspector(ILocalProjectDatabase database) : IDevDatabaseInspector
{
    public async Task<DevDatabaseOverview> GetOverviewAsync(string projectId, CancellationToken cancellationToken)
    {
        var databasePath = database.GetDatabasePath(projectId);

        await using var connection = await OpenReadOnlyAsync(projectId, cancellationToken);

        var tableNames = await ListTableNamesAsync(connection, database.Dialect.ListTablesSql, cancellationToken);
        var tables = new List<DevTableInfo>(tableNames.Count);

        foreach (var name in tableNames)
        {
            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(name)};";
            var rowCount = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            tables.Add(new DevTableInfo(name, rowCount));
        }

        string engineVersion;
        await using (var version = connection.CreateCommand())
        {
            version.CommandText = database.Dialect.EngineVersionSql;
            engineVersion = (string)(await version.ExecuteScalarAsync(cancellationToken))!;
        }

        var fileSize = File.Exists(databasePath) ? new FileInfo(databasePath).Length : 0L;

        return new DevDatabaseOverview(databasePath, fileSize, engineVersion, tables);
    }

    public async Task<DevTablePage?> GetTablePageAsync(
        string projectId,
        string tableName,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadOnlyAsync(projectId, cancellationToken);

        var tableNames = await ListTableNamesAsync(connection, database.Dialect.ListTablesSql, cancellationToken);
        if (!tableNames.Contains(tableName, StringComparer.Ordinal))
        {
            return null;
        }

        long totalCount;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(tableName)};";
            totalCount = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        await using var select = connection.CreateCommand();
        select.CommandText = $"SELECT * FROM {QuoteIdentifier(tableName)} LIMIT @limit OFFSET @offset;";
        select.AddWithValue("@limit", limit);
        select.AddWithValue("@offset", offset);

        var columns = new List<string>();
        var rows = new List<IReadOnlyList<string?>>();

        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(reader.GetName(i));
            }

            while (await reader.ReadAsync(cancellationToken))
            {
                var cells = new string?[reader.FieldCount];

                for (var i = 0; i < reader.FieldCount; i++)
                {
                    cells[i] = reader.IsDBNull(i)
                        ? null
                        : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                }

                rows.Add(cells);
            }
        }

        return new DevTablePage(tableName, columns, rows, totalCount, limit, offset);
    }

    private async Task<DbConnection> OpenReadOnlyAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var databasePath = database.GetDatabasePath(projectId);
        if (!File.Exists(databasePath))
        {
            throw new JetActionException(
                JetErrorCodes.FileNotFound,
                $"專案資料庫檔案不存在：{databasePath}（唯讀檢視不會建立資料庫）。");
        }

        var connection = database.CreateReadOnlyConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<List<string>> ListTableNamesAsync(
        DbConnection connection,
        string listTablesSql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = listTablesSql;

        var names = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static string QuoteIdentifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";
}
