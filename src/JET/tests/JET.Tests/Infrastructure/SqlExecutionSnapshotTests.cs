using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class SqlExecutionSnapshotTests
{
    [Fact]
    public void NormalizeSql_CollapsesOnlyWhitespaceOutsideLiteralsAndQuotedIdentifiers()
    {
        var sql =
            " SELECT  [column  name], 'A  B', \"C  D\" \r\n FROM  [table  name] ";

        var normalized = SqlExecutionSnapshot.NormalizeSql(sql);

        Assert.Equal(
            "SELECT [column  name], 'A  B', \"C  D\" FROM [table  name]",
            normalized);
    }

    [Fact]
    public void NormalizeSql_NormalizesOnlyProjectSchemaQualifier()
    {
        var normalized = SqlExecutionSnapshot.NormalizeSql(
            "SELECT '[prj_literal].' FROM [prj_0123456789abcdef].gl;",
            "[prj_0123456789abcdef].");

        Assert.Equal(
            "SELECT '[prj_literal].' FROM {s}.gl;",
            normalized);
    }

    [Fact]
    public void NormalizeSql_DoesNotNormalizeDifferentProjectSchemaQualifier()
    {
        var normalized = SqlExecutionSnapshot.NormalizeSql(
            "SELECT * FROM [prj_fedcba9876543210].gl;",
            "[prj_0123456789abcdef].");

        Assert.Equal(
            "SELECT * FROM [prj_fedcba9876543210].gl;",
            normalized);
    }

    [Theory]
    [InlineData("@p2=A  B")]
    [InlineData("$p2=A  B")]
    [InlineData("@p2= A")]
    public void PreserveParameters_KeepsMarkerNameValueAndWhitespaceExact(string value)
    {
        Assert.Equal(value, SqlExecutionSnapshot.PreserveParameters(value));
    }
}
