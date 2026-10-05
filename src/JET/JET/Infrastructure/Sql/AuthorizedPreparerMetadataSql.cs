using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using JET.Domain;

namespace JET.Infrastructure;

internal static class AuthorizedPreparerMetadataSql
{
    private const string SourceColumnKey = "authorized_preparer_source_column";
    private const string SourceRowCountKey = "authorized_preparer_source_row_count";
    private const string BlankRowCountKey = "authorized_preparer_blank_row_count";
    private const string DuplicateRowCountKey = "authorized_preparer_duplicate_row_count";
    private const string MetadataKeys =
        "'authorized_preparer_source_column', 'authorized_preparer_source_row_count', " +
        "'authorized_preparer_blank_row_count', 'authorized_preparer_duplicate_row_count'";

    internal static async Task WriteAsync(DbConnection connection, DbTransaction transaction,
        string? sourceColumn, CancellationToken ct, string prefix = "",
        int? sourceRowCount = null, int? blankRowCount = null, int? duplicateRowCount = null)
    {
        var key = prefix.Length == 0 ? "key" : "[key]";
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {prefix}schema_info WHERE {key} IN ({MetadataKeys});";
        if (sourceColumn is not null)
        {
            command.CommandText += $" INSERT INTO {prefix}schema_info ({key}, value) VALUES ('{SourceColumnKey}', @column);";
            command.AddWithValue("@column", sourceColumn);
            AddCount(SourceRowCountKey, "@sourceRowCount", sourceRowCount);
            AddCount(BlankRowCountKey, "@blankRowCount", blankRowCount);
            AddCount(DuplicateRowCountKey, "@duplicateRowCount", duplicateRowCount);
        }
        await command.ExecuteNonQueryAsync(ct);

        void AddCount(string name, string parameter, int? value)
        {
            if (value is null) return;
            command.CommandText += $" INSERT INTO {prefix}schema_info ({key}, value) VALUES ('{name}', {parameter});";
            command.AddWithValue(parameter, value.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    internal static async Task<(string? SourceColumn, int? SourceRowCount, int? BlankRowCount, int? DuplicateRowCount)>
        ReadAsync(DbConnection connection, CancellationToken ct, string prefix = "")
    {
        var key = prefix.Length == 0 ? "key" : "[key]";
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {key}, value FROM {prefix}schema_info WHERE {key} IN ({MetadataKeys});";
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) metadata[reader.GetString(0)] = reader.GetString(1);
        return (metadata.GetValueOrDefault(SourceColumnKey), ReadCount(SourceRowCountKey),
            ReadCount(BlankRowCountKey), ReadCount(DuplicateRowCountKey));

        int? ReadCount(string name) => metadata.TryGetValue(name, out var raw)
            && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                ? count : null;
    }

    internal static async Task<long?> CountMatchedAsync(DbConnection connection, DbTransaction? transaction,
        IProviderSqlDialect dialect, CancellationToken ct, string prefix = "")
    {
        // 只讀目前已確認的配對；重新匯入後保留的上一份草稿不能讓比對顯示為已完成。
        await using (var mappingCommand = connection.CreateCommand())
        {
            mappingCommand.Transaction = transaction;
            mappingCommand.CommandText = $"SELECT mapping_json FROM {prefix}config_field_mapping WHERE dataset_kind = 'gl';";
            if (await mappingCommand.ExecuteScalarAsync(ct) is not string mappingJson) return null;
            var mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(mappingJson, JetJsonStorage.Options);
            if (mapping is null || !mapping.TryGetValue("createBy", out var sourceColumn)
                || string.IsNullOrWhiteSpace(sourceColumn)) return null;
        }

        // 名單人員只計一次；GL 僅在資料庫內比對，不把大量識別值帶回記憶體。
        var personKey = $"UPPER({dialect.Trim("ap.name")})";
        var creatorKey = $"UPPER({dialect.Trim("g.created_by")})";
        var count = dialect.ProviderName == "sqlServer" ? "COUNT_BIG" : "COUNT";
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {count}(DISTINCT {personKey})
            FROM {prefix}target_authorized_preparer ap
            WHERE {personKey} IN (
                SELECT {creatorKey}
                FROM {prefix}target_gl_entry g
                WHERE g.created_by IS NOT NULL);
            """;
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0L : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }
}
