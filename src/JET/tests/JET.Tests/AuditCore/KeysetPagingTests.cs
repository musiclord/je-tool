using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.AuditCore;

/// <summary>
/// 排序、搜尋與複合游標的組譯守衛（2026-09-07 工作包 G）。這裡只鎖 SQL 文字的形狀與游標的往返，
/// 真正跑在 SQLite 與 DuckDB 上的結果由 Application 與 Infrastructure 的走訪測試負責。
/// </summary>
public sealed class KeysetPagingTests
{
    private static readonly PageSortCatalog Catalog = new(
        new SortColumn("entryId", "g.entry_id", SortValueKind.Integer),
        [
            new SortColumn("amount", "g.amount_scaled", SortValueKind.Integer),
            new SortColumn("approvedBy", "g.approved_by", SortValueKind.Text)
        ],
        "g.document_number");

    [Fact]
    public void NoSort_KeepsStableKeyOrderAndPlainCursor()
    {
        var plan = KeysetPaging.Plan(SqliteDialect.Instance, new PageRequest(null, 10), Catalog);

        Assert.Equal("ORDER BY (g.entry_id)", plan.OrderBy);
        Assert.Equal(string.Empty, plan.Predicate);
        Assert.Equal(string.Empty, plan.SelectSuffix);
        Assert.False(plan.HasSort);
        Assert.Empty(plan.Parameters);

        var next = plan.NextCursor(null, 42L);
        Assert.True(PageCursor.TryDecode(next, out var key));
        Assert.Equal("42", key);

        var continued = KeysetPaging.Plan(SqliteDialect.Instance, new PageRequest(next, 10), Catalog);
        Assert.Equal(" AND ((g.entry_id) > @keyCursor)", continued.Predicate);
        Assert.Equal(42L, Assert.Single(continued.Parameters).Value);
    }

    [Fact]
    public void Sorted_PutsNullsLastAndCarriesSortValueInCursor()
    {
        var request = new PageRequest(null, 10, new PageSort("approvedBy", PageSortDirection.Descending));
        var plan = KeysetPaging.Plan(DuckDbDialect.Instance, request, Catalog);

        Assert.Equal(
            "ORDER BY CASE WHEN (g.approved_by) IS NULL THEN 1 ELSE 0 END, (g.approved_by) DESC, (g.entry_id) DESC",
            plan.OrderBy);
        Assert.Equal(", (g.approved_by) AS page_sort_value", plan.SelectSuffix);
        Assert.Equal(string.Empty, plan.Predicate);

        var next = plan.NextCursor("alice", 7L);
        Assert.True(PageCursor.TryDecodeComposite(next, out var cursor));
        Assert.Equal(new PageCursorKey("approvedBy", PageSortDirection.Descending, false, "alice", "7"), cursor);

        var continued = KeysetPaging.Plan(DuckDbDialect.Instance, request with { Cursor = next }, Catalog);
        Assert.Equal(
            " AND (((g.approved_by) IS NULL OR (g.approved_by) < @sortCursor OR ((g.approved_by) = @sortCursor AND (g.entry_id) < @keyCursor)))",
            continued.Predicate);
        Assert.Equal(7L, continued.Parameters.Single(item => item.Key == "@keyCursor").Value);
        Assert.Equal("alice", continued.Parameters.Single(item => item.Key == "@sortCursor").Value);

        // 上一頁末列的排序值是 NULL：後面只剩 NULL 段，只比穩定鍵。
        var afterNull = plan.NextCursor(DBNull.Value, 9L);
        var tail = KeysetPaging.Plan(DuckDbDialect.Instance, request with { Cursor = afterNull }, Catalog);
        Assert.Equal(" AND (((g.approved_by) IS NULL AND (g.entry_id) < @keyCursor))", tail.Predicate);
        Assert.Equal(9L, Assert.Single(tail.Parameters).Value);
    }

    [Fact]
    public void IntegerSortValues_AreBoundAsIntegers()
    {
        var request = new PageRequest(null, 10, new PageSort("amount", PageSortDirection.Ascending));
        var first = KeysetPaging.Plan(SqliteDialect.Instance, request, Catalog);
        var next = first.NextCursor(-12500L, 3L);
        var plan = KeysetPaging.Plan(SqliteDialect.Instance, request with { Cursor = next }, Catalog);

        Assert.Equal(-12500L, plan.Parameters.Single(item => item.Key == "@sortCursor").Value);
        Assert.Contains("(g.amount_scaled) > @sortCursor", plan.Predicate, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_UsesTheDialectContainsAndUpperCasesTheText()
    {
        var plan = KeysetPaging.Plan(SqlServerDialect.Instance, new PageRequest(null, 10, null, " v-00a "), Catalog);

        Assert.Equal(" AND (CHARINDEX(@pageSearch, UPPER(COALESCE(g.document_number, N''))) > 0)", plan.Predicate);
        Assert.Equal("V-00A", Assert.Single(plan.Parameters).Value);
    }

    [Fact]
    public void CursorAndSortMismatch_FailLoud()
    {
        var sorted = new PageSort("amount", PageSortDirection.Ascending);
        var sortedCursor = KeysetPaging.Plan(SqliteDialect.Instance, new PageRequest(null, 10, sorted), Catalog).NextCursor(5L, 1L);
        var plainCursor = PageCursor.Encode("1");

        var withoutSort = Assert.Throws<JetActionException>(() =>
            KeysetPaging.Plan(SqliteDialect.Instance, new PageRequest(sortedCursor, 10), Catalog));
        Assert.Equal(JetErrorCodes.InvalidPayload, withoutSort.Code);

        var withPlainCursor = Assert.Throws<JetActionException>(() =>
            KeysetPaging.Plan(SqliteDialect.Instance, new PageRequest(plainCursor, 10, sorted), Catalog));
        Assert.Equal(JetErrorCodes.InvalidPayload, withPlainCursor.Code);

        var otherKey = Assert.Throws<JetActionException>(() =>
            KeysetPaging.Plan(SqliteDialect.Instance, new PageRequest(sortedCursor, 10, new PageSort("approvedBy", PageSortDirection.Ascending)), Catalog));
        Assert.Equal(JetErrorCodes.InvalidPayload, otherKey.Code);

        var otherDirection = Assert.Throws<JetActionException>(() =>
            KeysetPaging.Plan(SqliteDialect.Instance, new PageRequest(sortedCursor, 10, new PageSort("amount", PageSortDirection.Descending)), Catalog));
        Assert.Equal(JetErrorCodes.InvalidPayload, otherDirection.Code);
    }

    [Fact]
    public void UnknownSortKey_ListsTheAllowedKeys()
    {
        var error = Assert.Throws<JetActionException>(() =>
            KeysetPaging.Plan(SqliteDialect.Instance, new PageRequest(null, 10, new PageSort("entry_id; DROP TABLE x", PageSortDirection.Ascending)), Catalog));

        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
        Assert.Contains("amount、approvedBy", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Buffer_DropsTheLookaheadRowBeforeEncodingTheCursor()
    {
        var request = new PageRequest(null, 2, new PageSort("amount", PageSortDirection.Ascending));
        var plan = KeysetPaging.Plan(SqliteDialect.Instance, request, Catalog);
        var buffer = new KeysetPageBuffer<long>();
        buffer.Add(1L, 100L);
        buffer.Add(2L, 200L);
        buffer.Add(3L, 300L);

        var page = buffer.ToPage(request, plan, static id => id);

        Assert.Equal([1L, 2L], page.Rows);
        Assert.True(PageCursor.TryDecodeComposite(page.NextCursor, out var cursor));
        Assert.Equal("200", cursor.SortValue);
        Assert.Equal("2", cursor.StableKey);

        var last = new KeysetPageBuffer<long>();
        last.Add(4L, 400L);
        Assert.Null(last.ToPage(request, plan, static id => id).NextCursor);
    }

    [Fact]
    public void EveryCatalogKey_MatchesItsWireRowName()
    {
        // 目錄鍵名就是前端表頭要用的 wire 欄位名，不能夾雜 SQL 識別字。
        foreach (var catalog in new[]
                 {
                     ResultPageSorting.CompletenessDiff, ResultPageSorting.DocBalance, ResultPageSorting.NullRecords,
                     ResultPageSorting.SourceQuality, ResultPageSorting.InfSample, ResultPageSorting.GlEntryRows,
                     ResultPageSorting.TagMatrixVoucher, ResultPageSorting.TagMatrixRow, ResultPageSorting.FilterVoucher
                 })
        {
            Assert.Equal(catalog.Keys.Count, catalog.Keys.Distinct(StringComparer.Ordinal).Count());
            Assert.All(catalog.Keys, key => Assert.Matches("^[a-z][A-Za-z]*$", key));
            Assert.NotNull(catalog.SearchSql);
        }
    }
}
