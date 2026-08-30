using System.Data.Common;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class LocalPaginationBoundaryMatrixTests
{
    [Theory]
    [InlineData("sqlite", 0)]
    [InlineData("sqlite", 1)]
    [InlineData("sqlite", 2)]
    [InlineData("sqlite", 3)]
    [InlineData("sqlite", 4)]
    [InlineData("duckdb", 0)]
    [InlineData("duckdb", 1)]
    [InlineData("duckdb", 2)]
    [InlineData("duckdb", 3)]
    [InlineData("duckdb", 4)]
    public async Task FiveBoundaries_AllKeyFamilies_UseRealLookahead(string provider, int rowCount)
    {
        await using var engine = await PaginationBoundaryEngine.CreateLocalAsync(provider, rowCount);

        await PaginationBoundaryAssertions.AssertAllAsync(engine, rowCount);
    }
}

public sealed class SqlServerPaginationBoundaryMatrixTests
{
    [SqlServerTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task FiveBoundaries_AllKeyFamilies_UseRealLookahead(int rowCount)
    {
        await using var engine = await PaginationBoundaryEngine.CreateSqlServerAsync(rowCount);

        await PaginationBoundaryAssertions.AssertAllAsync(engine, rowCount);
    }
}

internal static class PaginationBoundaryAssertions
{
    private const int PageSize = 2;
    private static readonly GlPopulationContext Population =
        new(GlPopulationScope.AuditPeriod, "2025-01-01", "2025-12-31");

    public static async Task AssertAllAsync(PaginationBoundaryEngine engine, int expectedCount)
    {
        await AssertNumericPageAsync(engine, expectedCount);
        await AssertStringPageAsync(engine, expectedCount);
        await AssertVoucherMatrixAsync(engine, expectedCount);
        await AssertRowMatrixAsync(engine, expectedCount);
    }

    private static async Task AssertNumericPageAsync(PaginationBoundaryEngine engine, int expectedCount)
    {
        var seen = new List<long>();
        string? cursor = null;
        do
        {
            var page = await engine.FilterHits.GetPageAsync(
                engine.ProjectId, 1, 10_000, new PageRequest(cursor, PageSize), CancellationToken.None);
            AssertPageContract("numeric entry_id", page.Rows.Select(row => row.EntryId).ToList(),
                page.NextCursor, seen.Count, expectedCount, key => key.ToString());
            seen.AddRange(page.Rows.Select(row => row.EntryId));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(expectedCount, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    private static async Task AssertStringPageAsync(PaginationBoundaryEngine engine, int expectedCount)
    {
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await engine.DocBalance.GetPageAsync(
                engine.ProjectId, 10_000, "2025-01-01", "2025-12-31",
                new PageRequest(cursor, PageSize), CancellationToken.None);
            var keys = page.Rows.Select(row => row.DocumentNumber!).ToList();
            AssertPageContract("string document_number", keys, page.NextCursor,
                seen.Count, expectedCount, key => key);
            seen.AddRange(keys);
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(ExpectedDocuments(expectedCount), seen);
    }

    private static async Task AssertVoucherMatrixAsync(PaginationBoundaryEngine engine, int expectedCount)
    {
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var result = await engine.VoucherMatrix.GetPageAsync(
                engine.ProjectId, Population, new PageRequest(cursor, PageSize), [1], CancellationToken.None);
            var keys = result.Page.Rows.Select(row => row.DocumentNumber!).ToList();
            AssertPageContract("tag voucher document_number", keys, result.Page.NextCursor,
                seen.Count, expectedCount, key => key);
            Assert.Equal(keys.OrderBy(key => key, StringComparer.Ordinal),
                result.PositionsByDoc.Keys.OrderBy(key => key, StringComparer.Ordinal));
            seen.AddRange(keys);
            cursor = result.Page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(ExpectedDocuments(expectedCount), seen);
    }

    private static async Task AssertRowMatrixAsync(PaginationBoundaryEngine engine, int expectedCount)
    {
        var seen = new List<long>();
        string? cursor = null;
        do
        {
            var result = await engine.RowMatrix.GetPageAsync(
                engine.ProjectId, Population, new PageRequest(cursor, PageSize), [1], CancellationToken.None);
            AssertPageContract("tag row entry_id", result.EntryIds, result.Page.NextCursor,
                seen.Count, expectedCount, key => key.ToString());
            Assert.Equal(result.EntryIds.OrderBy(key => key),
                result.PositionsByEntry.Keys.OrderBy(key => key));
            seen.AddRange(result.EntryIds);
            cursor = result.Page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(expectedCount, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    private static void AssertPageContract<T>(
        string family,
        IReadOnlyList<T> keys,
        string? nextCursor,
        int alreadySeen,
        int expectedCount,
        Func<T, string> cursorKey)
    {
        Assert.True(keys.Count <= PageSize, $"{family}: page exceeded {PageSize} rows.");
        var hasMore = alreadySeen + keys.Count < expectedCount;
        Assert.Equal(hasMore, nextCursor is not null);

        if (nextCursor is not null)
        {
            Assert.NotEmpty(keys);
            Assert.True(PageCursor.TryDecode(nextCursor, out var decoded),
                $"{family}: nextCursor was not decodable.");
            Assert.Equal(cursorKey(keys[^1]), decoded);
        }
    }

    private static IReadOnlyList<string> ExpectedDocuments(int count) =>
        Enumerable.Range(1, count).Select(index => $"DOC-{index:000}").ToList();
}

internal sealed class PaginationBoundaryEngine : IAsyncDisposable
{
    private readonly TempProjectRoot? _localRoot;
    private readonly TempSqlServerProject? _sqlServer;

    private PaginationBoundaryEngine(
        string projectId,
        IFilterHitsPageRepository filterHits,
        IDocBalancePageRepository docBalance,
        ITagMatrixVoucherPageRepository voucherMatrix,
        ITagMatrixRowPageRepository rowMatrix,
        TempProjectRoot? localRoot = null,
        TempSqlServerProject? sqlServer = null)
    {
        ProjectId = projectId;
        FilterHits = filterHits;
        DocBalance = docBalance;
        VoucherMatrix = voucherMatrix;
        RowMatrix = rowMatrix;
        _localRoot = localRoot;
        _sqlServer = sqlServer;
    }

    public string ProjectId { get; }
    public IFilterHitsPageRepository FilterHits { get; }
    public IDocBalancePageRepository DocBalance { get; }
    public ITagMatrixVoucherPageRepository VoucherMatrix { get; }
    public ITagMatrixRowPageRepository RowMatrix { get; }

    public static async Task<PaginationBoundaryEngine> CreateLocalAsync(string provider, int rowCount)
    {
        var root = new TempProjectRoot();
        var database = provider switch
        {
            "sqlite" => (ILocalProjectDatabase)new SqliteProjectDatabase(new JetProjectFolder(root.Path)),
            "duckdb" => new DuckDbProjectDatabase(new JetProjectFolder(root.Path)),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown local provider.")
        };
        var projectId = Guid.NewGuid().ToString("N");
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await SeedAsync(database.CreateConnection(projectId), string.Empty, rowCount);

        return new PaginationBoundaryEngine(
            projectId,
            new LocalFilterHitsPageRepository(database),
            new LocalDocBalancePageRepository(database),
            new LocalTagMatrixVoucherPageRepository(database),
            new LocalTagMatrixRowPageRepository(database),
            localRoot: root);
    }

    public static async Task<PaginationBoundaryEngine> CreateSqlServerAsync(int rowCount)
    {
        var sql = Assert.IsType<TempSqlServerProject>(await TempSqlServerProject.TryCreateAsync());
        await SeedAsync(
            sql.Database.CreateConnection(sql.ProjectId),
            SqlServerProjectSchema.QualifierFor(sql.ProjectId),
            rowCount);

        return new PaginationBoundaryEngine(
            sql.ProjectId,
            new SqlServerFilterHitsPageRepository(sql.Database),
            new SqlServerDocBalancePageRepository(sql.Database),
            new SqlServerTagMatrixVoucherPageRepository(sql.Database),
            new SqlServerTagMatrixRowPageRepository(sql.Database),
            sqlServer: sql);
    }

    private static async Task SeedAsync(DbConnection connection, string prefix, int rowCount)
    {
        await using (connection)
        {
            await connection.OpenAsync();
            if (rowCount == 0)
            {
                return;
            }

            var values = Enumerable.Range(1, rowCount)
                .Select(index =>
                    $"('boundary', {index}, 'DOC-{index:000}', '{index}', '2025-06-01', " +
                    $"'A{index:000}', 'Account {index}', 'Boundary {index}', 1, 10000, 10000, 0, 'DEBIT')")
                .ToList();

            await using var command = connection.CreateCommand();
            command.CommandText =
                $"INSERT INTO {prefix}target_gl_entry " +
                "(batch_id, source_row_number, document_number, line_item, post_date, account_code, " +
                " account_name, document_description, is_effective, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr) VALUES " +
                string.Join(",", values) + ";" +
                $"INSERT INTO {prefix}result_filter_run (scenario_position, entry_id) " +
                $"SELECT 1, entry_id FROM {prefix}target_gl_entry;";
            await command.ExecuteNonQueryAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_sqlServer is not null)
        {
            await _sqlServer.DisposeAsync();
        }

        _localRoot?.Dispose();
    }
}
