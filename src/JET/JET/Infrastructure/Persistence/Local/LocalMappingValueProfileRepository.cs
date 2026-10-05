using System.Data.Common;
using System.Text.Json;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// SQLite／DuckDB 共用的 mapping value profile。JSON key 只以參數比對 json_each.key，
/// 不組成 JSON path 或 SQL identifier；SQL 只回 summary 與最多 limit 筆 group。
/// </summary>
public sealed class LocalMappingValueProfileRepository(ILocalProjectDatabase database)
    : IMappingValueProfileRepository
{
    private const string SqliteSql =
        """
        WITH extracted AS (
            SELECT CAST(j.value AS TEXT) AS raw_value
            FROM staging_gl_raw_row AS r
            LEFT JOIN json_each(r.row_json) AS j ON j.key = @sourceColumn
            WHERE r.batch_id = @batchId
        ),
        normalized AS (
            SELECT NULLIF(TRIM(raw_value, @trimCharacters), '') AS value
            FROM extracted
        ),
        grouped AS (
            SELECT MIN(value COLLATE BINARY) AS value, COUNT(*) AS value_count
            FROM normalized
            GROUP BY jet_mapping_ordinal_key(value) COLLATE BINARY
        ),
        summary AS (
            SELECT CAST(COALESCE(SUM(CASE WHEN value IS NULL THEN value_count ELSE 0 END), 0) AS INTEGER) AS blank_count,
                   CAST(COALESCE(SUM(CASE WHEN value IS NOT NULL THEN 1 ELSE 0 END), 0) AS INTEGER) AS distinct_count
            FROM grouped
        ),
        top_values AS (
            SELECT value, value_count
            FROM grouped
            WHERE value IS NOT NULL
            ORDER BY value_count DESC, value COLLATE BINARY ASC
            LIMIT @limit
        )
        SELECT summary.blank_count, summary.distinct_count, top_values.value, top_values.value_count
        FROM summary
        LEFT JOIN top_values ON 1 = 1
        ORDER BY top_values.value_count DESC, top_values.value COLLATE BINARY ASC;
        """;

    private const string DuckDbSql =
        """
        WITH extracted AS (
            SELECT json_extract_string(j.value, '$') AS raw_value
            FROM staging_gl_raw_row AS r
            LEFT JOIN json_each(r.row_json) AS j ON j.key = @sourceColumn
            WHERE r.batch_id = @batchId
        ),
        normalized AS (
            SELECT NULLIF(TRIM(raw_value, @trimCharacters), '') AS value
            FROM extracted
        ),
        grouped AS (
            SELECT MIN(value) AS value, COUNT(*) AS value_count
            FROM normalized
            GROUP BY jet_mapping_ordinal_key(value)
        ),
        summary AS (
            SELECT CAST(COALESCE(SUM(CASE WHEN value IS NULL THEN value_count ELSE 0 END), 0) AS BIGINT) AS blank_count,
                   CAST(COALESCE(SUM(CASE WHEN value IS NOT NULL THEN 1 ELSE 0 END), 0) AS BIGINT) AS distinct_count
            FROM grouped
        ),
        top_values AS (
            SELECT value, value_count
            FROM grouped
            WHERE value IS NOT NULL
            ORDER BY value_count DESC, value ASC
            LIMIT @limit
        )
        SELECT summary.blank_count, summary.distinct_count, top_values.value, top_values.value_count
        FROM summary
        LEFT JOIN top_values ON TRUE
        ORDER BY top_values.value_count DESC, top_values.value ASC;
        """;

    public async Task<MappingValueProfile> GetAsync(
        string projectId,
        string batchId,
        string sourceColumn,
        int limit,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        MappingValueProfileNormalization.RegisterLocalFunction(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = database.Dialect.ProviderName == "duckdb" ? DuckDbSql : SqliteSql;
        command.AddWithValue("@batchId", batchId);
        command.AddWithValue("@sourceColumn", sourceColumn);
        command.AddWithValue("@trimCharacters", MappingValueProfileNormalization.DotNetTrimCharacters);
        command.AddWithValue("@limit", limit);

        return await ReadProfileAsync(command, sourceColumn, cancellationToken);
    }

    public async Task<IReadOnlyList<string>?> FindMissingValuesAsync(
        string projectId, string batchId, string sourceColumn, IReadOnlyList<string> comparisonValues,
        CancellationToken cancellationToken)
    {
        if (comparisonValues.Count == 0) return [];
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        MappingValueProfileNormalization.RegisterLocalFunction(connection);
        var duckDb = database.Dialect.ProviderName == "duckdb";
        var sourceValue = duckDb ? "json_extract_string(j.value, '$')" : "CAST(j.value AS TEXT)";
        var requestedValue = duckDb ? "json_extract_string(c.value, '$')" : "CAST(c.value AS TEXT)";
        await using var command = connection.CreateCommand();
        // 完整來源先求相異正準值，再與最多 100 個請求值做集合式存在性核對，不使用 top-values 清單。
        command.CommandText = $"""
            WITH available AS (
                SELECT DISTINCT jet_mapping_ordinal_key({sourceValue}) AS comparison_key
                FROM staging_gl_raw_row r
                LEFT JOIN json_each(r.row_json) j ON j.key = @sourceColumn
                WHERE r.batch_id = @batchId
            ), requested AS (
                SELECT CAST(c.key AS INTEGER) AS ordinal, {requestedValue} AS value,
                       jet_mapping_ordinal_key({requestedValue}) AS comparison_key
                FROM json_each(@comparisonValues) c
            )
            SELECT requested.value
            FROM requested
            WHERE NOT EXISTS (SELECT 1 FROM available
                WHERE available.comparison_key = requested.comparison_key
                   OR (available.comparison_key IS NULL AND requested.comparison_key IS NULL))
            ORDER BY requested.ordinal;
            """;
        command.AddWithValue("@batchId", batchId);
        command.AddWithValue("@sourceColumn", sourceColumn);
        command.AddWithValue("@comparisonValues", JsonSerializer.Serialize(comparisonValues));
        var missing = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) missing.Add(reader.GetString(0));
        return missing;
    }

    private static async Task<MappingValueProfile> ReadProfileAsync(
        DbCommand command,
        string sourceColumn,
        CancellationToken cancellationToken)
    {
        long blankCount = 0;
        long distinctCount = 0;
        var values = new List<MappingValueProfileValue>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var first = true;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (first)
            {
                blankCount = reader.GetInt64(0);
                distinctCount = reader.GetInt64(1);
                first = false;
            }

            if (!reader.IsDBNull(2))
            {
                values.Add(new MappingValueProfileValue(reader.GetString(2), reader.GetInt64(3)));
            }
        }

        return new MappingValueProfile(
            sourceColumn,
            blankCount,
            distinctCount,
            values,
            distinctCount > values.Count);
    }
}
