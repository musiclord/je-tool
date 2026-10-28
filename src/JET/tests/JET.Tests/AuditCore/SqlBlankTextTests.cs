using JET.AuditCore;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.AuditCore;

/// <summary>
/// 文字欄「是空白」「不是空白」的唯一 SQL 寫法。2026-10-22 技術債一輪把散在各規則與配對查詢裡的同一句收成這一個類別：
/// 空白指 NULL 或經方言去空白後是空字串，兩個片段都帶外層括號。
/// </summary>
public sealed class SqlBlankTextTests
{
    [Fact]
    public void IsBlank_IsNullOrTrimmedEmpty_WrappedInParentheses()
    {
        Assert.Equal(
            "(description IS NULL OR TRIM(description) = '')",
            SqlBlankText.IsBlank(new PlainTrimDialect(), "description"));
    }

    [Fact]
    public void IsNotBlank_IsNotNullAndTrimmedNotEmpty_WrappedInParentheses()
    {
        Assert.Equal(
            "(g.document_number IS NOT NULL AND TRIM(g.document_number) <> '')",
            SqlBlankText.IsNotBlank(new PlainTrimDialect(), "g.document_number"));
    }

    [Fact]
    public void EachDatabase_TrimsWithItsOwnWhitespaceList()
    {
        // 三種資料庫的去空白字元集合不同，這裡只確認片段經過方言的去空白方法，不比對字元清單本身。
        foreach (ISqlDialect dialect in new ISqlDialect[] { SqliteDialect.Instance, DuckDbDialect.Instance, SqlServerDialect.Instance })
        {
            Assert.Equal(
                $"(created_by IS NULL OR {dialect.Trim("created_by")} = '')",
                SqlBlankText.IsBlank(dialect, "created_by"));
            Assert.Equal(
                $"(created_by IS NOT NULL AND {dialect.Trim("created_by")} <> '')",
                SqlBlankText.IsNotBlank(dialect, "created_by"));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void BlankExpression_IsRejected(string expression)
    {
        Assert.ThrowsAny<ArgumentException>(() => SqlBlankText.IsBlank(new PlainTrimDialect(), expression));
        Assert.ThrowsAny<ArgumentException>(() => SqlBlankText.IsNotBlank(new PlainTrimDialect(), expression));
    }

    [Fact]
    public void NullDialect_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => SqlBlankText.IsBlank(null!, "description"));
        Assert.Throws<ArgumentNullException>(() => SqlBlankText.IsNotBlank(null!, "description"));
    }

    /// <summary>只會用到去空白的方言替身，讓固定答案不必寫出三種資料庫的空白字元清單。</summary>
    private sealed class PlainTrimDialect : ISqlDialect
    {
        public string Trim(string expr) => $"TRIM({expr})";

        public string ParameterName(int index) => throw new NotSupportedException();

        public string WeekendPredicate(string dateExpr, IReadOnlyCollection<int> nonWorkingDays) => throw new NotSupportedException();

        public string ContainsIgnoreCase(string columnExpr, string parameterName) => throw new NotSupportedException();

        public string Upper(string expr) => throw new NotSupportedException();

        public string IntegerQuotient(string dividendExpression, string divisorExpression) => throw new NotSupportedException();

        public string DayOfMonth(string dateExpr) => throw new NotSupportedException();

        public string DaysInMonth(string dateExpr) => throw new NotSupportedException();

        public string InfSampleOrderingKey(string sourceRowNumberExpression, string seedExpression) => throw new NotSupportedException();

        public string LimitClause(string parameterName) => throw new NotSupportedException();

        public string ListTablesSql => throw new NotSupportedException();

        public string EngineVersionSql => throw new NotSupportedException();
    }
}
