using System.Text.Json;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// SQL Server mapping value profile。OPENJSON 的 key 以參數比對；值去掉和 .NET 相同的空白字元後，
/// 依方言 <see cref="SqlServerDialect.Upper"/> 不分大小寫分組，顯示值取同組碼位最小的寫法（與本地倉儲同口徑，
/// 跨資料庫一致性計畫第三節 D）。查詢只回 summary 與最多 limit 筆 group。
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
            $$"""
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
                SELECT {{TrimmedValue("raw_value")}} AS value
                FROM extracted
            ),
            grouped AS (
                SELECT MIN(value) AS value, COUNT_BIG(*) AS value_count
                FROM normalized
                GROUP BY {{SqlServerDialect.Instance.Upper("value")}}
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

    public async Task<IReadOnlyList<string>?> FindMissingValuesAsync(
        string projectId, string batchId, string sourceColumn, IReadOnlyList<string> comparisonValues,
        CancellationToken cancellationToken)
    {
        if (comparisonValues.Count == 0) return [];
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        // 完整來源先求相異比對鍵，再與最多 100 個請求值做集合式存在性核對，不使用 top-values 清單。
        // 比對鍵和分組相同：去空白後空字串視為空白，再轉大寫（與本地倉儲同口徑）。
        await using var command = database.CreateCommand(connection, projectId,
            $$"""
            WITH available AS (
                SELECT DISTINCT {{ComparisonKey("j.raw_value")}} AS comparison_key
                FROM {s}.staging_gl_raw_row AS r
                OUTER APPLY (
                    SELECT [value] AS raw_value
                    FROM OPENJSON(r.row_json)
                    WHERE [key] = @sourceColumn
                ) AS j
                WHERE r.batch_id = @batchId
            ), requested AS (
                SELECT CAST(c.[key] AS int) AS ordinal, c.[value] AS value,
                       {{ComparisonKey("c.[value]")}} AS comparison_key
                FROM OPENJSON(@comparisonValues) AS c
            )
            SELECT requested.value
            FROM requested
            WHERE NOT EXISTS (SELECT 1 FROM available
                WHERE available.comparison_key = requested.comparison_key
                   OR (available.comparison_key IS NULL AND requested.comparison_key IS NULL))
            ORDER BY requested.ordinal;
            """);
        command.Parameters.AddWithValue("@batchId", batchId);
        command.Parameters.AddWithValue("@sourceColumn", sourceColumn);
        command.Parameters.AddWithValue("@trimCharacters", MappingValueProfileNormalization.DotNetTrimCharacters);
        command.Parameters.AddWithValue("@comparisonValues", JsonSerializer.Serialize(comparisonValues));
        var missing = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) missing.Add(reader.GetString(0));
        return missing;
    }

    /// <summary>去掉和 .NET 相同的空白字元，剩空字串時視為空白（NULL）。</summary>
    private static string TrimmedValue(string rawExpression) =>
        $"NULLIF(TRIM(@trimCharacters COLLATE Latin1_General_BIN2 FROM {rawExpression} COLLATE Latin1_General_BIN2), N'') "
        + "COLLATE Latin1_General_BIN2";

    private static string ComparisonKey(string rawExpression) =>
        SqlServerDialect.Instance.Upper(TrimmedValue(rawExpression));
}
