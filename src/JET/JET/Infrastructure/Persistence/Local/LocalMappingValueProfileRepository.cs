using System.Data.Common;
using System.Text.Json;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 「讀取來源值」的 SQLite 查詢：統計 PBC-GL 某一個來源欄有哪些值、各出現幾次，並核對指定的值有沒有出現在這一欄。
/// 來源欄名只當成參數拿去比對 json_each.key，不拼進 JSON path 或 SQL 識別字。
/// SQL 只回摘要、最多 limit 組值與查無的值。來源欄勾了往下填補時先填補再計數，
/// 寫法見 <see cref="MappingValueProfileNormalization.FillDownCtes"/>。
/// </summary>
public sealed class LocalMappingValueProfileRepository(ILocalProjectDatabase database)
    : IMappingValueProfileRepository
{
    // 結果每一列的第一欄：0 是值概況的列，1 是查無的值。
    private const long MissingValuePart = 1;

    // 取這一列這一欄的原始值，沒有這一欄時是 NULL。row_json 由欄名對值的字典寫成，同一列的欄名不會重複，
    // 所以取第一個名稱相符的欄位，和展開全部欄位再挑出這一欄得到同一個值；找到就停，不必比對其餘欄位。
    private const string SourceValue =
        "(SELECT CAST(j.value AS TEXT) FROM json_each(r.row_json) AS j WHERE j.key = @sourceColumn)";

    public async Task<MappingValueProfile> GetAsync(
        string projectId,
        string batchId,
        string sourceColumn,
        int limit,
        bool fillDown,
        CancellationToken cancellationToken)
    {
        var (profile, _) = await QueryAsync(
            projectId, batchId, sourceColumn, limit, fillDown, comparisonValues: null, cancellationToken);
        return profile!;
    }

    public async Task<IReadOnlyList<string>> FindMissingValuesAsync(
        string projectId, string batchId, string sourceColumn, IReadOnlyList<string> comparisonValues,
        CancellationToken cancellationToken)
    {
        if (comparisonValues.Count == 0) return [];
        var (_, missing) = await QueryAsync(
            projectId, batchId, sourceColumn, limit: null, fillDown: false, comparisonValues, cancellationToken);
        return missing!;
    }

    public async Task<CheckedMappingValueProfile> GetWithSourceValueCheckAsync(
        string projectId,
        string batchId,
        string sourceColumn,
        int limit,
        bool fillDown,
        IReadOnlyList<string> comparisonValues,
        CancellationToken cancellationToken)
    {
        var (profile, missing) = await QueryAsync(
            projectId, batchId, sourceColumn, limit, fillDown, comparisonValues, cancellationToken);
        return new CheckedMappingValueProfile(profile!, missing!);
    }

    /// <summary>
    /// 一句 SQL 只掃一次來源，依需要算出值概況、查無的值或兩者。
    /// 先依原始值分組計數，比對鍵只對不重複的值算：先依原始值分組、再依比對鍵合併，得到的組和逐列依比對鍵分組相同，
    /// 每組的次數是組內各值次數的和，代表值仍是組內去空白後最小的值。
    /// 值概況用去空白後的值算比對鍵；核對用沒有去空白的原始值算，和審計員填的值一樣交給比對鍵處理空白。
    /// 往下填補只複製來源裡已有的值，不影響值存不存在，所以勾了往下填補時，核對仍從沒有填補的原始值取不重複的值。
    /// 兩部分放進同一個結果，第一欄分辨是哪一部分；值概況依次數由多到少、次數相同時依 BINARY 順序排列，
    /// 查無的值依請求的順序排列。
    /// </summary>
    internal static string SourceValueSql(bool fillDown, bool profile, bool check)
    {
        var fillDownProfile = fillDown && profile;
        var ctes = new List<string>
        {
            $"""
            extracted AS MATERIALIZED (
                SELECT {(fillDownProfile ? "r.source_no, r.row_number, " : "")}{SourceValue} AS raw_value
                FROM pbc_gl AS r
                WHERE r.batch_id = @batchId
            )
            """
        };
        if (fillDownProfile)
        {
            ctes.Add("""
                normalized AS (
                    SELECT source_no, row_number, NULLIF(TRIM(raw_value, @trimCharacters), '') AS value
                    FROM extracted
                )
                """);
            ctes.Add(MappingValueProfileNormalization.FillDownCtes("normalized"));
            if (check)
            {
                ctes.Add("""
                    check_values AS MATERIALIZED (
                        SELECT raw_value
                        FROM extracted
                        GROUP BY raw_value
                    )
                    """);
            }
        }
        else
        {
            ctes.Add("""
                raw_counts AS MATERIALIZED (
                    SELECT raw_value, COUNT(*) AS value_count
                    FROM extracted
                    GROUP BY raw_value
                )
                """);
            if (profile)
            {
                ctes.Add("""
                    profile_values AS (
                        SELECT NULLIF(TRIM(raw_value, @trimCharacters), '') AS value, value_count
                        FROM raw_counts
                    )
                    """);
            }

            if (check)
            {
                ctes.Add("""
                    check_values AS (
                        SELECT raw_value
                        FROM raw_counts
                    )
                    """);
            }
        }

        if (profile)
        {
            ctes.Add($"""
                grouped AS MATERIALIZED (
                    SELECT MIN(value COLLATE BINARY) AS value, SUM(value_count) AS value_count
                    FROM {(fillDownProfile ? "filled" : "profile_values")}
                    GROUP BY jet_mapping_ordinal_key(value) COLLATE BINARY
                )
                """);
            ctes.Add("""
                summary AS (
                    SELECT CAST(COALESCE(SUM(CASE WHEN value IS NULL THEN value_count ELSE 0 END), 0) AS INTEGER) AS blank_count,
                           CAST(COALESCE(SUM(CASE WHEN value IS NOT NULL THEN 1 ELSE 0 END), 0) AS INTEGER) AS distinct_count
                    FROM grouped
                )
                """);
            ctes.Add("""
                top_values AS (
                    SELECT value, value_count
                    FROM grouped
                    WHERE value IS NOT NULL
                    ORDER BY value_count DESC, value COLLATE BINARY ASC
                    LIMIT @limit
                )
                """);
        }

        if (check)
        {
            // 不重複的比對鍵只算一次：available 是 MATERIALIZED，兩個子查詢都不引用 requested 的欄位，
            // SQLite 各自只執行一次，IN 另把比對鍵建成暫時索引。寫成引用 requested 的 NOT EXISTS 時，
            // 每核對一個值就要把 available 重讀一遍。空白的值另外判斷，因為 IN 不會把兩個 NULL 當成相同。
            ctes.Add("""
                available AS MATERIALIZED (
                    SELECT DISTINCT jet_mapping_ordinal_key(raw_value) AS comparison_key
                    FROM check_values
                )
                """);
            ctes.Add("""
                requested AS (
                    SELECT CAST(c.key AS INTEGER) AS ordinal, CAST(c.value AS TEXT) AS value,
                           jet_mapping_ordinal_key(CAST(c.value AS TEXT)) AS comparison_key
                    FROM json_each(@comparisonValues) c
                )
                """);
            ctes.Add("""
                missing AS (
                    SELECT requested.ordinal, requested.value
                    FROM requested
                    WHERE CASE
                        WHEN requested.comparison_key IS NULL
                            THEN NOT EXISTS (SELECT 1 FROM available WHERE available.comparison_key IS NULL)
                        ELSE requested.comparison_key NOT IN (
                            SELECT available.comparison_key FROM available WHERE available.comparison_key IS NOT NULL)
                    END
                )
                """);
        }

        const string profileRows = """
            SELECT 0 AS part, summary.blank_count, summary.distinct_count, top_values.value, top_values.value_count,
                   top_values.value_count AS count_order, top_values.value AS value_order, NULL AS request_order
            FROM summary
            LEFT JOIN top_values ON 1 = 1
            """;
        const string missingRows = """
            SELECT 1 AS part, NULL AS blank_count, NULL AS distinct_count, missing.value, NULL AS value_count,
                   NULL AS count_order, NULL AS value_order, missing.ordinal AS request_order
            FROM missing
            """;
        var rows = (profile, check) switch
        {
            (true, true) => profileRows + "\nUNION ALL\n" + missingRows,
            (true, false) => profileRows,
            _ => missingRows
        };
        return "WITH " + string.Join(",\n", ctes) + "\n" + rows
            + "\nORDER BY part, count_order DESC, value_order COLLATE BINARY ASC, request_order;";
    }

    private async Task<(MappingValueProfile? Profile, IReadOnlyList<string>? Missing)> QueryAsync(
        string projectId,
        string batchId,
        string sourceColumn,
        int? limit,
        bool fillDown,
        IReadOnlyList<string>? comparisonValues,
        CancellationToken cancellationToken)
    {
        var profile = limit is not null;
        var check = comparisonValues is not null;
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        MappingValueProfileNormalization.RegisterLocalFunction(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = SourceValueSql(fillDown, profile, check);
        command.AddWithValue("@batchId", batchId);
        command.AddWithValue("@sourceColumn", sourceColumn);
        if (profile)
        {
            command.AddWithValue("@trimCharacters", MappingValueProfileNormalization.DotNetTrimCharacters);
            command.AddWithValue("@limit", limit!.Value);
        }

        if (check) command.AddWithValue("@comparisonValues", JsonSerializer.Serialize(comparisonValues));

        return await ReadAsync(command, sourceColumn, profile, check, cancellationToken);
    }

    private static async Task<(MappingValueProfile? Profile, IReadOnlyList<string>? Missing)> ReadAsync(
        DbCommand command,
        string sourceColumn,
        bool profile,
        bool check,
        CancellationToken cancellationToken)
    {
        long blankCount = 0;
        long distinctCount = 0;
        var values = new List<MappingValueProfileValue>();
        var missing = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetInt64(0) == MissingValuePart)
            {
                missing.Add(reader.GetString(3));
                continue;
            }

            blankCount = reader.GetInt64(1);
            distinctCount = reader.GetInt64(2);
            if (!reader.IsDBNull(3))
            {
                values.Add(new MappingValueProfileValue(reader.GetString(3), reader.GetInt64(4)));
            }
        }

        return (
            profile
                ? new MappingValueProfile(sourceColumn, blankCount, distinctCount, values, distinctCount > values.Count)
                : null,
            check ? missing : null);
    }
}
