using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 確認 GL 配對的投影做完以後，gl 的內容、索引與查詢的執行計畫，都和剛建好的案件資料庫一樣。
/// 投影成功、被擋下或取消，gl 的四個索引都還在，定義文字與彼此的先後也不變；
/// 每一列照來源的順序寫進 gl，沒有配對分錄序號時，每張傳票的分錄依來源順序從 1 編起。
/// </summary>
public sealed class GlProjectionIndexTests
{
    private static readonly IReadOnlyList<string> Columns = ["doc", "line", "date", "appr", "acc", "debit", "credit"];

    private static readonly string[] GlIndexNames = ["ix_gl_voucher", "ix_gl_account", "ix_gl_gl_date", "ix_gl_approval_date"];

    /// <summary>來源檔的六列：三張傳票的分錄交錯排列，每張傳票一借一貸。</summary>
    private static readonly SourceLine[] InterleavedLines =
    [
        new("V1", "10", "2025-01-02", "2025-01-05", "1101", "100", "0"),
        new("V2", "20", "2025-01-03", "2025-01-06", "4101", "50", "0"),
        new("V1", "30", "2025-01-02", "2025-01-05", "2101", "0", "100"),
        new("V3", "40", "2025-01-04", "2025-01-07", "1101", "30", "0"),
        new("V2", "50", "2025-01-03", "2025-01-06", "1101", "0", "50"),
        new("V3", "60", "2025-01-04", "2025-01-07", "4101", "0", "30"),
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlProjection_RebuildsTheGlIndexesWithTheSameDefinitionsAndOrder(bool lineItemMapped)
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateProjectAsync(root);
        var expected = await ReadFreshGlIndexesAsync(root);
        Assert.Equal(GlIndexNames, expected.Select(static index => index.Name));

        var batchId = await ImportAsync(database, projectId, InterleavedLines);
        var first = await ProjectAsync(database, projectId, batchId, lineItemMapped, CancellationToken.None);
        Assert.Equal(6, first.ProjectedRowCount);
        Assert.Equal(expected, await ReadGlIndexesAsync(database, projectId));

        // 重新確認配對時再投影一次，索引照樣建回原本的樣子。
        var second = await ProjectAsync(database, projectId, batchId, lineItemMapped, CancellationToken.None);
        Assert.Equal(6, second.ProjectedRowCount);
        Assert.Equal(expected, await ReadGlIndexesAsync(database, projectId));
        Assert.Equal("ok", await ScalarAsync(database, projectId, "PRAGMA integrity_check;"));
    }

    [Fact]
    public async Task GlProjection_KeepsTheQueryPlansOfGlQueries()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateProjectAsync(root);
        var batchId = await ImportAsync(database, projectId, InterleavedLines);
        await ProjectAsync(database, projectId, batchId, lineItemMapped: false, CancellationToken.None);

        // 這幾個查詢各自可以在 gl 的一個或兩個索引之間選。案件資料庫不收集統計資料，
        // 執行計畫只看資料表與索引的定義，所以要和剛建好的資料庫相同。
        string[] queries =
        [
            "SELECT entry_id FROM gl WHERE voucher_number = 'V1';",
            "SELECT entry_id FROM gl WHERE account_number = '1101' AND gl_date BETWEEN '2025-01-01' AND '2025-01-31';",
            "SELECT entry_id FROM gl WHERE gl_date = '2025-01-02' AND approval_date = '2025-01-05';",
            "SELECT entry_id FROM gl WHERE approval_date > '2025-01-01' AND account_number = '1101';",
            "SELECT voucher_number, COUNT(*) FROM gl GROUP BY voucher_number;",
            "SELECT entry_id FROM gl WHERE voucher_number IN (SELECT voucher_number FROM gl WHERE account_number = '4101') "
                + "ORDER BY voucher_number, line_item_numeric_sort_key, entry_id;",
        ];
        var freshPath = await CreateFreshDatabaseAsync(root);
        foreach (var query in queries)
        {
            Assert.Equal(await ReadPlanAsync(freshPath, query), await ReadPlanAsync(database.GetDatabasePath(projectId), query));
        }
    }

    [Fact]
    public async Task GlProjection_WritesEachSourceRowAndNumbersLinesPerVoucher()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateProjectAsync(root);
        var batchId = await ImportAsync(database, projectId, InterleavedLines);

        await ProjectAsync(database, projectId, batchId, lineItemMapped: false, CancellationToken.None);

        // 金額存放倍率是 10,000；沒有配對分錄序號，每張傳票依來源順序編 1、2。
        var key1 = "2" + "2147483649" + "1".PadRight(29, '0');
        var key2 = "2" + "2147483649" + "2".PadRight(29, '0');
        GlRow[] expected =
        [
            new(1, 2, "V1", "1", key1, "2025-01-02", "2025-01-05", "1101", 1_000_000, 1_000_000, 0, "DEBIT", 0),
            new(2, 3, "V2", "1", key1, "2025-01-03", "2025-01-06", "4101", 500_000, 500_000, 0, "DEBIT", 0),
            new(3, 4, "V1", "2", key2, "2025-01-02", "2025-01-05", "2101", -1_000_000, 0, 1_000_000, "CREDIT", 0),
            new(4, 5, "V3", "1", key1, "2025-01-04", "2025-01-07", "1101", 300_000, 300_000, 0, "DEBIT", 0),
            new(5, 6, "V2", "2", key2, "2025-01-03", "2025-01-06", "1101", -500_000, 0, 500_000, "CREDIT", 0),
            new(6, 7, "V3", "2", key2, "2025-01-04", "2025-01-07", "4101", -300_000, 0, 300_000, "CREDIT", 0),
        ];
        Assert.Equal(expected, await ReadGlRowsAsync(database, projectId));
    }

    [Fact]
    public async Task RejectedGlProjection_LeavesTheGlIndexesInPlace()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateProjectAsync(root);
        var expected = await ReadFreshGlIndexesAsync(root);
        var batchId = await ImportAsync(
            database,
            projectId,
            [InterleavedLines[0], InterleavedLines[1] with { Debit = "not a number" }, InterleavedLines[2]]);

        var result = await ProjectAsync(database, projectId, batchId, lineItemMapped: false, CancellationToken.None);

        Assert.NotEmpty(result.Errors);
        Assert.Equal(expected, await ReadGlIndexesAsync(database, projectId));
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(database, projectId, "SELECT COUNT(*) FROM gl;")));
    }

    [Fact]
    public async Task EmptyGlProjection_LeavesTheGlIndexesInPlace()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateProjectAsync(root);
        var expected = await ReadFreshGlIndexesAsync(root);
        var batchId = await ImportAsync(database, projectId, InterleavedLines);

        // 查核期間不含任何一列，沒有分錄列入 GL，整批擋下。
        var error = await Assert.ThrowsAsync<JetActionException>(() => ProjectAsync(
            database, projectId, batchId, lineItemMapped: false, CancellationToken.None,
            periodStart: new DateOnly(2024, 1, 1), periodEnd: new DateOnly(2024, 12, 31)));

        Assert.Equal(JetErrorCodes.EmptyGl, error.Code);
        Assert.Equal(expected, await ReadGlIndexesAsync(database, projectId));
    }

    [Fact]
    public async Task CancelledGlProjection_LeavesTheGlIndexesInPlace()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateProjectAsync(root);
        var expected = await ReadFreshGlIndexesAsync(root);
        var batchId = await ImportAsync(database, projectId, InterleavedLines);

        // 逐列寫完後回報進度時要求取消，之後的步驟在交易提交前停下。
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectAsync(
            database, projectId, batchId, lineItemMapped: false, cancellation.Token,
            progress: _ => cancellation.Cancel()));

        Assert.Equal(expected, await ReadGlIndexesAsync(database, projectId));
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(database, projectId, "SELECT COUNT(*) FROM gl;")));
    }

    private sealed record SourceLine(
        string Doc, string Line, string Date, string Approval, string Account, string Debit, string Credit);

    private sealed record GlIndex(string Name, string? Sql);

    private sealed record GlRow(
        long EntryId,
        long SourceRowNumber,
        string? VoucherNumber,
        string? LineItem,
        string? LineItemNumericSortKey,
        string? GlDate,
        string? ApprovalDate,
        string? AccountNumber,
        long AmountScaled,
        long DebitAmountScaled,
        long CreditAmountScaled,
        string DrCr,
        long? IsExcluded);

    private static async Task<(ILocalProjectDatabase Database, string ProjectId)> CreateProjectAsync(TempProjectRoot root)
    {
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        return (database, projectId);
    }

    /// <summary>另建一個剛建好、沒有資料的案件資料庫，回傳它的路徑。</summary>
    private static async Task<string> CreateFreshDatabaseAsync(TempProjectRoot root)
    {
        var (database, projectId) = await CreateProjectAsync(root);
        return database.GetDatabasePath(projectId);
    }

    private static async Task<IReadOnlyList<GlIndex>> ReadFreshGlIndexesAsync(TempProjectRoot root) =>
        await ReadGlIndexesAsync(await CreateFreshDatabaseAsync(root));

    private static async Task<string> ImportAsync(ILocalProjectDatabase database, string projectId, IReadOnlyList<SourceLine> lines)
    {
        var result = await new LocalImportRepository(database).ReplaceBatchAsync(
            projectId,
            DatasetKind.Gl,
            [new ImportSourceInput(new ImportSourceDescriptor("fixture.csv", "fixture.csv", null, null, null), Columns, Rows(lines))],
            CancellationToken.None);
        return result.Batch.BatchId;
    }

    private static async IAsyncEnumerable<StagingRow> Rows(IReadOnlyList<SourceLine> lines)
    {
        var sourceRow = 2;
        foreach (var line in lines)
        {
            yield return new StagingRow(
                sourceRow++,
                new Dictionary<string, string>
                {
                    ["doc"] = line.Doc,
                    ["line"] = line.Line,
                    ["date"] = line.Date,
                    ["appr"] = line.Approval,
                    ["acc"] = line.Account,
                    ["debit"] = line.Debit,
                    ["credit"] = line.Credit
                });
        }

        await Task.CompletedTask;
    }

    private static Task<ProjectionResult> ProjectAsync(
        ILocalProjectDatabase database,
        string projectId,
        string batchId,
        bool lineItemMapped,
        CancellationToken cancellationToken,
        DateOnly? periodStart = null,
        DateOnly? periodEnd = null,
        Action<ProjectionProgress>? progress = null)
    {
        var mapping = new Dictionary<string, string>
        {
            [GlMappingKeys.VoucherNumber] = "doc",
            [GlMappingKeys.GlDate] = "date",
            [GlMappingKeys.ApprovalDate] = "appr",
            [GlMappingKeys.AccountNumber] = "acc",
            [GlMappingKeys.DebitAmount] = "debit",
            [GlMappingKeys.CreditAmount] = "credit"
        };
        if (lineItemMapped)
        {
            mapping[GlMappingKeys.LineItem] = "line";
        }

        var spec = new GlMappingSpec(mapping, GlAmountMode.DualAmount)
        {
            Options = GlMappingOptions.NormalizeLegacy(mapping)
        };
        return new LocalGlRepository(database).ProjectStagingToTargetAsync(
            projectId,
            batchId,
            spec,
            10_000,
            DateParseOptions.Default,
            periodStart ?? DateOnly.MinValue,
            periodEnd ?? DateOnly.MaxValue,
            postingStatusMapped: false,
            postingStatusPolicy: null,
            committedUtc: DateTimeOffset.UnixEpoch,
            cancellationToken,
            progress);
    }

    private static Task<IReadOnlyList<GlIndex>> ReadGlIndexesAsync(ILocalProjectDatabase database, string projectId) =>
        ReadGlIndexesAsync(database.GetDatabasePath(projectId));

    /// <summary>資料庫結構表裡 gl 的索引，照建立的先後排列。</summary>
    private static async Task<IReadOnlyList<GlIndex>> ReadGlIndexesAsync(string databasePath)
    {
        await using var connection = await OpenReadOnlyAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'index' AND tbl_name = 'gl' ORDER BY rowid;";
        await using var reader = await command.ExecuteReaderAsync();
        var indexes = new List<GlIndex>();
        while (await reader.ReadAsync())
        {
            indexes.Add(new GlIndex(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return indexes;
    }

    private static async Task<IReadOnlyList<string>> ReadPlanAsync(string databasePath, string query)
    {
        await using var connection = await OpenReadOnlyAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + query;
        await using var reader = await command.ExecuteReaderAsync();
        var plan = new List<string>();
        while (await reader.ReadAsync())
        {
            plan.Add(reader.GetString(reader.GetOrdinal("detail")));
        }

        return plan;
    }

    private static async Task<IReadOnlyList<GlRow>> ReadGlRowsAsync(ILocalProjectDatabase database, string projectId)
    {
        await using var connection = await OpenReadOnlyAsync(database.GetDatabasePath(projectId));
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT entry_id, source_row_number, voucher_number, line_item, line_item_numeric_sort_key,
                   gl_date, approval_date, account_number, amount_scaled, debit_amount_scaled, credit_amount_scaled,
                   dr_cr, is_excluded
            FROM gl
            ORDER BY entry_id;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<GlRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new GlRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                NullableText(reader, 2),
                NullableText(reader, 3),
                NullableText(reader, 4),
                NullableText(reader, 5),
                NullableText(reader, 6),
                NullableText(reader, 7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetInt64(12)));
        }

        return rows;
    }

    private static string? NullableText(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static async Task<object?> ScalarAsync(ILocalProjectDatabase database, string projectId, string sql)
    {
        await using var connection = await OpenReadOnlyAsync(database.GetDatabasePath(projectId));
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }
}
