using System.Data.Common;
using System.Text;
using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 第三步「讀取來源值」的值概況與來源值核對，產品程式用一句 SQL 掃一次來源算出。
/// 測試裡保留改寫前分開執行的兩句 SQL 當對照，兩句各掃一次來源。同一份合成資料、同一組參數，
/// 兩種寫法的空白數、不同值數、常見值與次數、截斷旗標，以及缺少的值與它們的順序都要相同；往下填補空白開與關都比。
/// 合成資料用 JET 的匯入程式寫進案件資料庫，分成三份來源，有特殊欄名、重複值、空字串、只有空白、
/// 只差大小寫或空白的值、.NET 認得的各種空白、整欄空白、只有第一份來源有的欄，以及每一列都沒有的欄。
/// 另外直接用 SQL 寫進一個內容不同的批次，確認只讀指定的批次；也查一個沒有列的批次。
/// </summary>
public sealed class MappingValueProfileQueryEquivalenceTests
{
    private const string OtherBatchId = "other-batch";
    private const string OtherBatchValue = "只在另一個批次";
    private const string EmptyBatchId = "batch-without-rows";

    private static readonly int[] Limits = [1, 2, 50, 100];

    private static readonly string[] Manual = ["M", "m", " M", "M ", "\tm\r\n", "A", "a", "AUTO", "auto ", "", "   ", "\u00a0", "Ａ", "ａ"];

    private static readonly string[] Unicode =
    [
        "Σ", "ς", "σ", "ß", "SS", "ss", "ı", "I", "i", "İ", "é", "É", "e\u0301", "\U00010400", "\U00010428",
        "ｆ", "Ｆ", "\u00a0x\u2028", "x", "\u3000y", "y", "\u0085z\u0085", "z", "\uFEFFw", "w", "K", "k", "\u212A"
    ];

    private static readonly string[] BlankOnly = ["", " ", "\t", "\u2028", "\u3000", "\r\n"];

    /// <summary>欄名與這一欄的值怎麼產生；產生 null 時這一列沒有這一欄。</summary>
    private sealed record SyntheticColumn(string Name, Func<Random, int, string?> Value);

    // 欄名含單雙引號、反斜線、點、中括號、錢號、tab、換行與表情符號，確認欄名只當成參數比對。
    private static readonly SyntheticColumn[] SyntheticColumns =
    [
        new("人工/自動", static (random, _) => random.Next(5) == 0 ? null : Pick(random, Manual)),
        new("摘要", static (random, _) => Vary(random, $"摘要{random.Next(150)}")),
        new("金額", static (random, _) => (random.Next(200_000) / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)),
        new("status' OR 1=1 --", static (random, _) => random.Next(8) == 0 ? null : Pick(random, Unicode)),
        new("a\"b\\c", static (random, _) => Vary(random, Pick(random, Manual))),
        new("$.x[0]", static (random, _) => random.Next(6) == 0 ? Pick(random, Manual) : null),
        new("欄\t名\n😀", static (random, _) => random.Next(4) == 0 ? Pick(random, Unicode) : random.Next(2) == 0 ? "" : null),
        new("dot.name", static (random, _) => random.Next(3) == 0 ? null : Pick(random, BlankOnly)),
        new("只在第一份", static (random, sourceNo) => sourceNo == 1 ? Pick(random, Manual) : null),
        new("每一列都沒有", static (_, _) => null)
    ];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ProfileAndCheck_AreTheSameAsTheTwoQueriesBeforeTheRewrite(int seed)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        // 只有建案會建立案件資料庫，讀寫前的檢查不順便建表，所以照建案的做法先建表。
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        var random = new Random(seed);
        var written = SyntheticColumns.ToDictionary(column => column.Name, _ => new List<string>(), StringComparer.Ordinal);
        var sources = Enumerable.Range(1, 3)
            .Select(sourceNo => new ImportSourceInput(
                new ImportSourceDescriptor($@"C:\synthetic-equivalence-{sourceNo}.xlsx", $"synthetic-equivalence-{sourceNo}.xlsx",
                    null, null, null),
                SyntheticColumns.Select(column => column.Name).ToArray(),
                Rows(GenerateRows(random, sourceNo, random.Next(40, 200), written))))
            .ToArray();
        var batchId = (await new LocalImportRepository(database).ReplaceBatchAsync(
            projectId, DatasetKind.Gl, sources, CancellationToken.None)).Batch.BatchId;
        await WriteOtherBatchAsync(database, projectId, batchId);

        var repository = new LocalMappingValueProfileRepository(database);
        await using var oracle = database.CreateConnection(projectId);
        await oracle.OpenAsync();
        MappingValueProfileNormalization.RegisterLocalFunction(oracle);

        var compared = 0;
        foreach (var column in SyntheticColumns)
        {
            var lists = ComparisonLists(random, written[column.Name]);
            foreach (var targetBatch in new[] { batchId, OtherBatchId, EmptyBatchId })
            {
                for (var index = 0; index < lists.Count; index++)
                {
                    var values = lists[index];
                    var limit = Limits[index % Limits.Length];
                    var expectedMissing = await OracleMissingValuesAsync(oracle, targetBatch, column.Name, values);
                    var context = $"種子 {seed}，批次 {targetBatch}，欄 {Escape(column.Name)}，前 {limit} 個值，核對 {values.Count} 個值";
                    AssertSameMissing(expectedMissing,
                        await repository.FindMissingValuesAsync(projectId, targetBatch, column.Name, values, CancellationToken.None),
                        $"{context}，只核對");
                    foreach (var fillDown in new[] { false, true })
                    {
                        var fillContext = $"{context}，往下填補{(fillDown ? "開" : "關")}";
                        var expected = await OracleProfileAsync(oracle, targetBatch, column.Name, limit, fillDown);
                        AssertSameProfile(expected,
                            await repository.GetAsync(projectId, targetBatch, column.Name, limit, fillDown, CancellationToken.None),
                            $"{fillContext}，只算值概況");
                        var both = await ((IMappingValueProfileRepository)repository).GetWithSourceValueCheckAsync(
                            projectId, targetBatch, column.Name, limit, fillDown, values, CancellationToken.None);
                        AssertSameProfile(expected, both.Profile, $"{fillContext}，一起算的值概況");
                        AssertSameMissing(expectedMissing, both.MissingValues, $"{fillContext}，一起算的核對");
                        if (targetBatch == batchId)
                            Assert.DoesNotContain(both.Profile.Values, value => value.Value == OtherBatchValue);
                        compared++;
                    }
                }
            }
        }

        Assert.True(compared >= SyntheticColumns.Length * 3 * 2 * 4, $"只比了 {compared} 組。");
    }

    private static IReadOnlyList<StagingRow> GenerateRows(
        Random random, int sourceNo, int count, Dictionary<string, List<string>> written)
    {
        var rows = new List<StagingRow>(count);
        for (var index = 0; index < count; index++)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var column in SyntheticColumns)
            {
                if (column.Value(random, sourceNo) is not { } value) continue;
                values[column.Name] = value;
                written[column.Name].Add(value);
            }

            rows.Add(new StagingRow(index + 2, values));
        }

        return rows;
    }

    // 要核對的值：來源裡有的值、只差大小寫或前後空白的寫法、重複的值、空字串、只有空白，以及來源沒有的值，
    // 打亂順序讓缺少的值不照字母排列；另有不帶任何值與剛好 100 個值的情形。
    private static IReadOnlyList<IReadOnlyList<string>> ComparisonLists(Random random, IReadOnlyList<string> written)
    {
        var lists = new List<IReadOnlyList<string>> { Array.Empty<string>() };
        foreach (var size in new[] { 1, 12, 100 })
        {
            var values = new List<string>();
            while (values.Count < size)
            {
                var source = written.Count > 0 ? written[random.Next(written.Count)] : "沒有值";
                values.Add(random.Next(9) switch
                {
                    0 => source.ToUpperInvariant(),
                    1 => source.ToLowerInvariant(),
                    2 => " " + source + "\t",
                    3 => "",
                    4 => "\u00a0 ",
                    5 => OtherBatchValue,
                    6 => $"來源沒有的代碼{random.Next(5)}",
                    7 when values.Count > 0 => values[random.Next(values.Count)],
                    _ => source
                });
            }

            lists.Add(values.OrderBy(_ => random.Next()).ToArray());
        }

        return lists;
    }

    private static async Task WriteOtherBatchAsync(ILocalProjectDatabase database, string projectId, string batchId)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pbc_gl (batch_id, row_number, source_no, source_row_number, row_json)
            SELECT @otherBatchId, row_number, source_no, source_row_number, @rowJson
            FROM pbc_gl
            WHERE batch_id = @batchId;
            """;
        command.AddWithValue("@otherBatchId", OtherBatchId);
        command.AddWithValue("@batchId", batchId);
        command.AddWithValue("@rowJson", JsonSerializer.Serialize(
            SyntheticColumns.ToDictionary(column => column.Name, _ => OtherBatchValue), JetJsonStorage.Options));
        Assert.True(await command.ExecuteNonQueryAsync() > 0);
    }

    // 改寫前的值概況：每一列都展開全部欄位挑出這一欄，每一列都算一次比對鍵；勾往下填補時用兩個視窗函式逐列填補。
    private static string OracleProfileSql(bool fillDown) =>
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
        {(fillDown ? OracleFillDownCtes : "")}
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

    private const string OracleFillDownCtes = """
        fill_down_runs AS (
            SELECT source_no, value,
                   COUNT(value) OVER (PARTITION BY source_no ORDER BY row_number
                                      ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS run_no
            FROM normalized
        ),
        filled AS (
            SELECT MAX(value) OVER (PARTITION BY source_no, run_no) AS value
            FROM fill_down_runs
        ),
        """;

    // 改寫前的核對：另掃一次來源，用沒有去空白的原始值算比對鍵。
    private const string OracleCheckSql = """
        WITH available AS MATERIALIZED (
            SELECT DISTINCT jet_mapping_ordinal_key(CAST(j.value AS TEXT)) AS comparison_key
            FROM pbc_gl r
            LEFT JOIN json_each(r.row_json) j ON j.key = @sourceColumn
            WHERE r.batch_id = @batchId
        ), requested AS (
            SELECT CAST(c.key AS INTEGER) AS ordinal, CAST(c.value AS TEXT) AS value,
                   jet_mapping_ordinal_key(CAST(c.value AS TEXT)) AS comparison_key
            FROM json_each(@comparisonValues) c
        )
        SELECT requested.value
        FROM requested
        WHERE CASE
            WHEN requested.comparison_key IS NULL
                THEN NOT EXISTS (SELECT 1 FROM available WHERE available.comparison_key IS NULL)
            ELSE requested.comparison_key NOT IN (
                SELECT available.comparison_key FROM available WHERE available.comparison_key IS NOT NULL)
        END
        ORDER BY requested.ordinal;
        """;

    private static async Task<MappingValueProfile> OracleProfileAsync(
        DbConnection connection, string batchId, string sourceColumn, int limit, bool fillDown)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = OracleProfileSql(fillDown);
        command.AddWithValue("@batchId", batchId);
        command.AddWithValue("@sourceColumn", sourceColumn);
        command.AddWithValue("@trimCharacters", MappingValueProfileNormalization.DotNetTrimCharacters);
        command.AddWithValue("@limit", limit);

        long blankCount = 0;
        long distinctCount = 0;
        var values = new List<MappingValueProfileValue>();
        await using var reader = await command.ExecuteReaderAsync();
        var first = true;
        while (await reader.ReadAsync())
        {
            if (first)
            {
                blankCount = reader.GetInt64(0);
                distinctCount = reader.GetInt64(1);
                first = false;
            }

            if (!reader.IsDBNull(2)) values.Add(new MappingValueProfileValue(reader.GetString(2), reader.GetInt64(3)));
        }

        return new MappingValueProfile(sourceColumn, blankCount, distinctCount, values, distinctCount > values.Count);
    }

    private static async Task<IReadOnlyList<string>> OracleMissingValuesAsync(
        DbConnection connection, string batchId, string sourceColumn, IReadOnlyList<string> comparisonValues)
    {
        if (comparisonValues.Count == 0) return [];
        await using var command = connection.CreateCommand();
        command.CommandText = OracleCheckSql;
        command.AddWithValue("@batchId", batchId);
        command.AddWithValue("@sourceColumn", sourceColumn);
        command.AddWithValue("@comparisonValues", JsonSerializer.Serialize(comparisonValues));
        var missing = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) missing.Add(reader.GetString(0));
        return missing;
    }

    private static void AssertSameProfile(MappingValueProfile expected, MappingValueProfile actual, string context)
    {
        var same = expected.SourceColumn == actual.SourceColumn
            && expected.BlankCount == actual.BlankCount
            && expected.DistinctCount == actual.DistinctCount
            && expected.Truncated == actual.Truncated
            && expected.Values.SequenceEqual(actual.Values);
        Assert.True(same, $"{context}：值概況不同。改寫前 {Describe(expected)}；改寫後 {Describe(actual)}");
    }

    private static void AssertSameMissing(IReadOnlyList<string> expected, IReadOnlyList<string>? actual, string context)
    {
        Assert.True(actual is not null && expected.SequenceEqual(actual),
            $"{context}：缺少的值不同。改寫前 [{string.Join("，", expected.Select(Escape))}]；"
            + $"改寫後 [{(actual is null ? "沒有核對" : string.Join("，", actual.Select(Escape)))}]");
    }

    private static string Describe(MappingValueProfile profile) =>
        $"空白 {profile.BlankCount}，不同值 {profile.DistinctCount}，截斷 {profile.Truncated}，"
        + $"值 [{string.Join("，", profile.Values.Select(value => $"{Escape(value.Value)}={value.Count}"))}]";

    private static string Escape(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var character in value)
            builder.Append(character is < ' ' or > '~' && !char.IsLetterOrDigit(character)
                ? $"\\u{(int)character:x4}"
                : character.ToString());
        return builder.Append('"').ToString();
    }

    private static string Pick(Random random, string[] pool) => pool[random.Next(pool.Length)];

    private static string Vary(Random random, string value) => random.Next(6) switch
    {
        0 => value.ToUpperInvariant(),
        1 => value.ToLowerInvariant(),
        2 => " " + value,
        3 => value + "\u3000",
        _ => value
    };

    private static async IAsyncEnumerable<StagingRow> Rows(IReadOnlyList<StagingRow> rows)
    {
        foreach (var row in rows) yield return row;
        await Task.CompletedTask;
    }
}
