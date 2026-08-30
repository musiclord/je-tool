using JET.Infrastructure;
using JET.AuditCore;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// ISqlDialect 的 provider 差異片段契約：參數命名、週末判定、不分大小寫包含與限制列數子句。
/// </summary>
public sealed class SqlDialectTests
{
    [Theory]
    [InlineData(0, "@p0")]
    [InlineData(7, "@p7")]
    public void ParameterName_SqliteIndex_ReturnsAtPrefixedOrdinal(int index, string expected)
    {
        ISqlDialect dialect = SqliteDialect.Instance;

        var actual = dialect.ParameterName(index);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0, "@p0")]
    [InlineData(7, "@p7")]
    public void ParameterName_SqlServerIndex_ReturnsAtPrefixedOrdinal(int index, string expected)
    {
        ISqlDialect dialect = SqlServerDialect.Instance;

        var actual = dialect.ParameterName(index);

        Assert.Equal(expected, actual);
    }

    // 週末述詞:非工作日以 .NET DayOfWeek 編碼(週日=0…週六=6)。預設 {0,6} 須完全重現舊行為(golden-master)。
    [Fact]
    public void WeekendPredicate_SqliteDefaultSatSun_ReturnsStrftimeInZeroSix()
    {
        ISqlDialect dialect = SqliteDialect.Instance;

        var actual = dialect.WeekendPredicate("gl.posting_date", new[] { 0, 6 });

        Assert.Equal("strftime('%w', gl.posting_date) IN ('0','6')", actual);
    }

    // 中東式週五、週六放假:canonical {5,6} → strftime 同碼。
    [Fact]
    public void WeekendPredicate_SqliteFridaySaturday_ReturnsStrftimeInFiveSix()
    {
        ISqlDialect dialect = SqliteDialect.Instance;

        var actual = dialect.WeekendPredicate("gl.posting_date", new[] { 5, 6 });

        Assert.Equal("strftime('%w', gl.posting_date) IN ('5','6')", actual);
    }

    // 邊界:空集合 → 恆偽(週末規則無命中)。
    [Fact]
    public void WeekendPredicate_SqliteEmpty_ReturnsAlwaysFalse()
    {
        ISqlDialect dialect = SqliteDialect.Instance;

        var actual = dialect.WeekendPredicate("gl.posting_date", System.Array.Empty<int>());

        Assert.Equal("0 = 1", actual);
    }

    // SQL Server 錨點編碼:canonical d → (d+6)%7。預設 {0,6} → {6,5} 排序後 (5, 6),重現舊行為。
    [Fact]
    public void WeekendPredicate_SqlServerDefaultSatSun_ReturnsDateDiffInFiveSix()
    {
        ISqlDialect dialect = SqlServerDialect.Instance;

        var actual = dialect.WeekendPredicate("gl.posting_date", new[] { 0, 6 });

        Assert.Equal("(DATEDIFF(day, '19000101', CONVERT(date, gl.posting_date)) % 7) IN (5, 6)", actual);
    }

    // canonical {5,6}(週五、週六)→ {(5+6)%7,(6+6)%7} = {4,5} 排序後 (4, 5)。
    [Fact]
    public void WeekendPredicate_SqlServerFridaySaturday_ReturnsDateDiffInFourFive()
    {
        ISqlDialect dialect = SqlServerDialect.Instance;

        var actual = dialect.WeekendPredicate("gl.posting_date", new[] { 5, 6 });

        Assert.Equal("(DATEDIFF(day, '19000101', CONVERT(date, gl.posting_date)) % 7) IN (4, 5)", actual);
    }

    [Fact]
    public void WeekendPredicate_SqlServerEmpty_ReturnsAlwaysFalse()
    {
        ISqlDialect dialect = SqlServerDialect.Instance;

        var actual = dialect.WeekendPredicate("gl.posting_date", System.Array.Empty<int>());

        Assert.Equal("0 = 1", actual);
    }

    [Fact]
    public void ContainsIgnoreCase_SqliteColumnExpression_ReturnsUpperCoalesceInstrPredicate()
    {
        ISqlDialect dialect = SqliteDialect.Instance;

        var actual = dialect.ContainsIgnoreCase("gl.description", "@p2");

        Assert.Equal("instr(UPPER(COALESCE(gl.description, '')), @p2) > 0", actual);
    }

    [Fact]
    public void ContainsIgnoreCase_SqlServerColumnExpression_ReturnsUpperCoalesceCharIndexPredicate()
    {
        ISqlDialect dialect = SqlServerDialect.Instance;

        var actual = dialect.ContainsIgnoreCase("gl.description", "@p2");

        Assert.Equal("CHARINDEX(@p2, UPPER(COALESCE(gl.description, N''))) > 0", actual);
    }

    [Fact]
    public void LimitClause_SqliteParameterName_ReturnsLimitClause()
    {
        ISqlDialect dialect = SqliteDialect.Instance;

        var actual = dialect.LimitClause("@limit");

        Assert.Equal("LIMIT @limit", actual);
    }

    [Fact]
    public void LimitClause_SqlServerParameterName_ReturnsOffsetFetchClause()
    {
        ISqlDialect dialect = SqlServerDialect.Instance;

        var actual = dialect.LimitClause("@limit");

        Assert.Equal("OFFSET 0 ROWS FETCH NEXT @limit ROWS ONLY", actual);
    }
}
