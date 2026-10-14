using System.Data.Common;

namespace JET.Infrastructure;

/// <summary>
/// 本機 schema 第 11 版到第 12 版：訊息紀錄 <c>app_message_log.level</c> 的限制加上 <c>error</c>
/// （使用者 2026-10-13 裁定訊息分三級：錯誤、需要注意、一般）。SQLite 不能直接改 CHECK，DuckDB 也用同一個做法，
/// 在同一個交易裡重建這張表：舊紀錄原樣搬過去，舊的 <c>warn</c> 照樣是 <c>warn</c>，不回頭重新分類。
/// 新的建表語句由各資料庫傳入，和各自 SchemaSql 裡的寫法相同。
/// </summary>
internal static class MessageLogLevelMigration
{
    internal const string TargetVersion = "12";

    internal static async Task UpgradeLocalAsync(DbConnection connection, string createTableSql, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var sql in new[]
        {
            "CREATE TEMP TABLE app_message_log_previous AS SELECT message_id, occurred_utc, level, text FROM app_message_log",
            "DROP TABLE app_message_log",
            createTableSql,
            "INSERT INTO app_message_log (message_id, occurred_utc, level, text) "
                + "SELECT message_id, occurred_utc, level, text FROM app_message_log_previous ORDER BY message_id",
            "DROP TABLE app_message_log_previous",
            $"UPDATE schema_info SET value = '{TargetVersion}' WHERE key = 'schema_version'",
        })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }
}
