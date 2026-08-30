using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// SQL Server mapping value profile。OPENJSON 的 key 以參數比對；binary collation 鎖定
/// case-sensitive identity 與 ordinal tie ordering，查詢只回 summary 與最多 limit 筆 group。
/// </summary>
public sealed class SqlServerMappingValueProfileRepository(SqlServerProjectDatabase database)
    : IMappingValueProfileRepository
{
    public async Task<MappingValueProfile> GetAsync(
        string projectId,
        string batchId,
        string sourceColumn,
        int limit,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = database.CreateCommand(connection, projectId,
            """
            WITH extracted AS (
                SELECT j.raw_value
                FROM {s}.staging_gl_raw_row AS r
                OUTER APPLY (
                    SELECT [value] AS raw_value
                    FROM OPENJSON(r.row_json)
                    WHERE [key] = @sourceColumn
                ) AS j
                WHERE r.batch_id = @batchId
            ),
            normalized AS (
                SELECT NULLIF(
                    TRIM(
                        @trimCharacters COLLATE Latin1_General_BIN2
                        FROM raw_value COLLATE Latin1_General_BIN2),
                    N'') COLLATE Latin1_General_BIN2 AS value
                FROM extracted
            ),
            grouped AS (
                SELECT value, COUNT_BIG(*) AS value_count
                FROM normalized
                GROUP BY value
            ),
            summary AS (
                SELECT COALESCE(SUM(CASE WHEN value IS NULL THEN value_count ELSE 0 END), 0) AS blank_count,
                       COALESCE(SUM(CASE WHEN value IS NOT NULL THEN CONVERT(bigint, 1) ELSE 0 END), 0) AS distinct_count
                FROM grouped
            ),
            top_values AS (
                SELECT TOP (@limit) value, value_count
                FROM grouped
                WHERE value IS NOT NULL
                ORDER BY value_count DESC, value ASC
            )
            SELECT summary.blank_count, summary.distinct_count, top_values.value, top_values.value_count
            FROM summary
            LEFT JOIN top_values ON 1 = 1
            ORDER BY top_values.value_count DESC, top_values.value ASC;
            """);
        command.Parameters.AddWithValue("@batchId", batchId);
        command.Parameters.AddWithValue("@sourceColumn", sourceColumn);
        command.Parameters.AddWithValue("@trimCharacters", MappingValueProfileNormalization.DotNetTrimCharacters);
        command.Parameters.AddWithValue("@limit", limit);

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
