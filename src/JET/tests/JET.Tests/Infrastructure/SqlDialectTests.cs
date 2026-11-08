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

    [Fact]
    public void ContainsIgnoreCase_SqliteColumnExpression_ReturnsUpperCoalesceInstrPredicate()
    {
        ISqlDialect dialect = SqliteDialect.Instance;

        var actual = dialect.ContainsIgnoreCase("gl.description", "@p2");

        Assert.Equal("instr(UPPER(COALESCE(gl.description, '')), @p2) > 0", actual);
    }

    [Fact]
    public void LimitClause_SqliteParameterName_ReturnsLimitClause()
    {
        ISqlDialect dialect = SqliteDialect.Instance;

        var actual = dialect.LimitClause("@limit");

        Assert.Equal("LIMIT @limit", actual);
    }
}
