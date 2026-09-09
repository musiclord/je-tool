using System.Data.Common;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// schema v9 到 v10：在 <c>target_account_mapping</c> 加 <c>classification_explicit</c>（原始配對檔的分類欄是否有填），
/// 並依原始匯入列回填。這個旗標只供第四步顯示「分類留白 N 筆，視為 Others」；篩選述詞不看它（分類留白依 legacy
/// 落到 Others）。原分類值與已存情境不改寫。2026-09-07 起不再於每次開案重跑證據修復。
/// </summary>
internal static class AccountClassificationMigration
{
    internal static async Task UpgradeLocalAsync(DbConnection connection, ISqlDialect dialect, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        bool exists;
        await using (var shape = connection.CreateCommand())
        {
            shape.Transaction = transaction;
            shape.CommandText = "SELECT * FROM target_account_mapping WHERE 1 = 0";
            await using var reader = await shape.ExecuteReaderAsync(ct);
            exists = Enumerable.Range(0, reader.FieldCount).Any(index => reader.GetName(index) == "classification_explicit");
        }
        if (!exists)
        {
            await using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText = "ALTER TABLE target_account_mapping ADD COLUMN classification_explicit INTEGER NULL";
            await alter.ExecuteNonQueryAsync(ct);
        }
        await BackfillAsync(connection, transaction, dialect, "", ct);
        await using (var bump = connection.CreateCommand())
        {
            bump.Transaction = transaction;
            bump.CommandText = "UPDATE schema_info SET value = '10' WHERE key = 'schema_version'";
            await bump.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    internal static async Task<bool> BackfillAsync(DbConnection connection, DbTransaction transaction,
        ISqlDialect dialect, string prefix, CancellationToken ct)
    {
        long after = 0;
        var changed = false;
        while (true)
        {
            var batch = new List<(long Id, int? Explicit, int? Previous)>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.AddWithValue("@after", after);
                command.AddWithValue("@size", 500);
                command.CommandText = $"SELECT m.mapping_id, r.row_json, b.columns_json, m.classification_explicit FROM {prefix}target_account_mapping m "
                    + $"LEFT JOIN {prefix}staging_account_mapping_raw_row r ON r.batch_id = m.batch_id "
                    + "AND r.row_number = m.source_row_number "
                    + $"LEFT JOIN {prefix}import_batch b ON b.batch_id = m.batch_id "
                    + "WHERE m.mapping_id > @after ORDER BY m.mapping_id " + dialect.LimitClause("@size");
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var id = Convert.ToInt64(reader.GetValue(0));
                    var supplied = reader.IsDBNull(1) || reader.IsDBNull(2) ? null
                        : ReadExplicit(reader.GetString(1), reader.GetString(2));
                    batch.Add((id, supplied, reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3))));
                }
            }
            if (batch.Count == 0) break;
            foreach (var (id, supplied, previous) in batch)
            {
                if (supplied == previous || supplied is null) continue;
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.AddWithValue("@id", id);
                update.AddWithValue("@explicit", supplied.HasValue ? supplied.Value : DBNull.Value);
                update.CommandText = $"UPDATE {prefix}target_account_mapping SET classification_explicit = @explicit WHERE mapping_id = @id";
                await update.ExecuteNonQueryAsync(ct);
                changed = true;
            }
            after = batch[^1].Id;
        }
        return changed;
    }

    internal static int? ReadExplicit(string rowJson, string columnsJson)
    {
        try
        {
            var columns = JsonSerializer.Deserialize<string[]>(columnsJson);
            var values = JsonSerializer.Deserialize<Dictionary<string, string?>>(rowJson);
            if (columns is not { Length: >= 3 } || values is null) return null;
            var category = AccountMappingColumnResolver.Resolve(columns).CategoryColumn;
            return values.TryGetValue(category, out var raw) ? (string.IsNullOrWhiteSpace(raw) ? 0 : 1) : 0;
        }
        catch (JsonException) { return null; }
    }
}
