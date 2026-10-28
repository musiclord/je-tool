using System.Data.Common;

namespace JET.Infrastructure;

/// <summary>
/// 本機 schema 第 13 版到第 14 版（SQL Server 第 15 版到第 16 版）：匯入來源表多一欄 <c>header_row</c>，
/// 記審計員在第二步替每個來源指定的「欄名在第幾列」（預篩選收斂計畫階段六，使用者 2026-10-22 裁定開放給一般匯入）。
/// 只補欄位，既有來源原樣保留、欄名列是空值，表示當時從第一列起找欄名。新建的資料庫由各自的建表語句直接帶這一欄，
/// 這裡先查欄位存不存在再補，所以新庫與舊庫走同一條升版都不會重複加欄。
/// </summary>
internal static class ImportSourceHeaderRowMigration
{
    internal const string TargetVersion = "14";

    internal static async Task UpgradeLocalAsync(DbConnection connection, CancellationToken ct)
    {
        var hasColumn = await HasHeaderRowColumnAsync(connection, ct);

        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (!hasColumn)
        {
            await using var add = connection.CreateCommand();
            add.Transaction = transaction;
            add.CommandText = "ALTER TABLE import_batch_source ADD COLUMN header_row INTEGER NULL";
            await add.ExecuteNonQueryAsync(ct);
        }

        await using (var bump = connection.CreateCommand())
        {
            bump.Transaction = transaction;
            bump.CommandText = $"UPDATE schema_info SET value = '{TargetVersion}' WHERE key = 'schema_version'";
            await bump.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// SQLite 與 DuckDB 都支援 <c>pragma_table_info</c>，查欄位清單就知道有沒有這一欄，不用靠選欄失敗的例外判斷：
    /// 舊案件開案時偵錯器不會停在一個故意引發的例外上（使用者 2026-10-27 回報）。
    /// </summary>
    private static async Task<bool> HasHeaderRowColumnAsync(DbConnection connection, CancellationToken ct)
    {
        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('import_batch_source') WHERE name = 'header_row'";
        return Convert.ToInt64(await probe.ExecuteScalarAsync(ct)) > 0;
    }
}
