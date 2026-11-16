using System.Data.Common;
using System.Text.Json;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 「讀取來源值」的 SQLite 查詢：統計 PBC-GL 某一個來源欄有哪些值、各出現幾次。
/// 來源欄名只當成參數拿去比對 json_each.key，不拼進 JSON path 或 SQL 識別字。
/// SQL 只回摘要和最多 limit 組值。來源欄勾了往下填補時先填補再計數，
/// 寫法見 <see cref="MappingValueProfileNormalization.FillDownCtes"/>。
/// </summary>
public sealed class LocalMappingValueProfileRepository(ILocalProjectDatabase database)
    : IMappingValueProfileRepository
{
    // 勾了往下填補時，去空白之後先接 MappingValueProfileNormalization.FillDownCtes，分組改讀填補後的值。
    private static string SqliteSql(bool fillDown) =>
        $"""
        WITH extracted AS (
            SELECT r.source_no, r.row_number, CAST(j.value AS TEXT) AS raw_value
            FROM pbc_gl AS r
            LEFT JOIN json_each(r.row_json) AS j ON j.key = @sourceColumn
            WHERE r.batch_id = @batchId
        ),
        normalized AS (
            SELECT source_no, row_number, NULLIF(TRIM(raw_value, @trimCharacters), '') AS value
            FROM extracted
        ),
        {(fillDown ? MappingValueProfileNormalization.FillDownCtes("normalized") : "")}
        grouped AS (
            SELECT MIN(value COLLATE BINARY) AS value, COUNT(*) AS value_count
            FROM {(fillDown ? "filled" : "normalized")}
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

    public async Task<MappingValueProfile> GetAsync(
        string projectId,
        string batchId,
        string sourceColumn,
        int limit,
        bool fillDown,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        MappingValueProfileNormalization.RegisterLocalFunction(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = SqliteSql(fillDown);
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
        const string sourceValue = "CAST(j.value AS TEXT)";
        const string requestedValue = "CAST(c.value AS TEXT)";
        await using var command = connection.CreateCommand();
        // 先從整份來源算出相異的比對鍵，再用一句 SQL 核對最多 100 個指定的值有沒有出現在來源裡；
        // 不拿出現次數最多的那份清單來核對。
        command.CommandText = $"""
            WITH available AS (
                SELECT DISTINCT jet_mapping_ordinal_key({sourceValue}) AS comparison_key
                FROM pbc_gl r
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
