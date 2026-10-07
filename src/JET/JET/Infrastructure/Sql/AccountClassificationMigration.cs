using System.Data.Common;
using System.Text.Json;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// schema v9 到 v10：在 <c>target_account_mapping</c> 加 <c>classification_explicit</c>（原始配對檔的分類欄是否有填），
/// 並依原始匯入列回填。這個旗標只供第四步顯示「分類留白 N 筆，視為 Others」；篩選述詞不看它（分類留白依 legacy
/// 落到 Others）。原分類值與已存情境不改寫。2026-09-07 起不再於每次開案重跑證據修復。
/// 已存配對是唯一來源：不用目前的欄名規則重新判讀舊檔，而是找出和已存分類一致的來源欄；無法唯一判斷時保留空值，
/// 不計入分類留白。
/// </summary>
internal static class AccountClassificationMigration
{
    internal static async Task UpgradeLocalAsync(DbConnection connection, CancellationToken ct)
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
        await BackfillAsync(connection, transaction, "", ct);
        await using (var bump = connection.CreateCommand())
        {
            bump.Transaction = transaction;
            bump.CommandText = "UPDATE schema_info SET value = '10' WHERE key = 'schema_version'";
            await bump.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    internal sealed record SavedMappingRow(
        long MappingId, string BatchId, string? RowJson, string? ColumnsJson,
        string? CategoryId, string? StandardizedCategory, string? CategoryLabel, int? Previous);

    internal static async Task<bool> BackfillAsync(DbConnection connection, DbTransaction transaction,
        string prefix, CancellationToken ct)
    {
        // 科目配對列數有界（一份配對檔的科目數），整份讀入後依匯入批次判斷來源欄。
        var rows = new List<SavedMappingRow>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT m.mapping_id, m.batch_id, r.row_json, b.columns_json, m.category_id, "
                + $"m.standardized_category, t.label, m.classification_explicit FROM {prefix}target_account_mapping m "
                + $"LEFT JOIN {prefix}staging_account_mapping_raw_row r ON r.batch_id = m.batch_id "
                + "AND r.row_number = m.source_row_number "
                + $"LEFT JOIN {prefix}import_batch b ON b.batch_id = m.batch_id "
                + $"LEFT JOIN {prefix}config_account_taxonomy t ON t.category_id = m.category_id "
                + "ORDER BY m.mapping_id";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
                rows.Add(new SavedMappingRow(
                    Convert.ToInt64(reader.GetValue(0)), reader.GetString(1), Text(2), Text(3), Text(4), Text(5), Text(6),
                    reader.IsDBNull(7) ? null : Convert.ToInt32(reader.GetValue(7))));
            }
        }

        var changed = false;
        foreach (var batch in rows.GroupBy(row => row.BatchId, StringComparer.Ordinal))
        {
            var inferred = InferExplicit(batch.ToArray());
            foreach (var row in batch)
            {
                if (!inferred.TryGetValue(row.MappingId, out var supplied) || supplied is null || supplied == row.Previous) continue;
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.AddWithValue("@id", row.MappingId);
                update.AddWithValue("@explicit", supplied.Value);
                update.CommandText = $"UPDATE {prefix}target_account_mapping SET classification_explicit = @explicit WHERE mapping_id = @id";
                await update.ExecuteNonQueryAsync(ct);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// 依同一匯入批次的已存配對推回每列分類欄有沒有填：1 有填、0 留白、null 無法判斷。沒有原始列的科目不列入結果。
    /// 已存分類不是 Others 就一定有填（留白只會落到 Others）。Others 列要靠來源欄判斷：和每一列已存分類都一致的欄
    /// （留白對應 Others，有值則等於已存分類的代碼、名稱或舊版分類名）才是候選；候選欄對這一列的答案一致才採用。
    /// </summary>
    internal static IReadOnlyDictionary<long, int?> InferExplicit(IReadOnlyList<SavedMappingRow> batchRows)
    {
        var columns = ParseColumns(batchRows.Select(row => row.ColumnsJson).FirstOrDefault(json => json is not null));
        var evaluable = batchRows
            .Select(row => (Row: row, Values: ParseValues(row.RowJson)))
            .Where(item => item.Values is not null)
            .ToArray();
        var candidates = columns
            .Where(column => evaluable.Length > 0 && evaluable.All(item => Consistent(item.Values!, column, item.Row)))
            .ToArray();

        var result = new Dictionary<long, int?>();
        foreach (var (row, values) in evaluable)
        {
            if (!IsOthers(row))
            {
                result[row.MappingId] = 1;
                continue;
            }
            var answers = candidates.Select(column => IsBlank(values!, column) ? 0 : 1).Distinct().ToArray();
            result[row.MappingId] = answers.Length == 1 ? answers[0] : null;
        }
        return result;
    }

    private static bool Consistent(IReadOnlyDictionary<string, string?> values, string column, SavedMappingRow row)
    {
        if (IsBlank(values, column)) return IsOthers(row);
        var raw = values[column]!.Trim();
        return string.Equals(raw, row.CategoryId, StringComparison.Ordinal)
            || string.Equals(raw, row.CategoryLabel, StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, row.StandardizedCategory, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOthers(SavedMappingRow row) => row.CategoryId is null
        ? string.Equals(row.StandardizedCategory, AccountMappingCategories.Others, StringComparison.OrdinalIgnoreCase)
        : string.Equals(row.CategoryId, AccountTaxonomyBuiltIns.OthersId, StringComparison.Ordinal);

    private static bool IsBlank(IReadOnlyDictionary<string, string?> values, string column) =>
        !values.TryGetValue(column, out var raw) || string.IsNullOrWhiteSpace(raw);

    private static string[] ParseColumns(string? columnsJson)
    {
        if (columnsJson is null) return [];
        try { return JsonSerializer.Deserialize<string[]>(columnsJson) ?? []; }
        catch (JsonException) { return []; }
    }

    private static Dictionary<string, string?>? ParseValues(string? rowJson)
    {
        if (rowJson is null) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, string?>>(rowJson); }
        catch (JsonException) { return null; }
    }
}
