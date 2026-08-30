using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class SqlServerProjectSchemaTests
{
    [Fact]
    public void For_IsDeterministic()
        => Assert.Equal(SqlServerProjectSchema.For("ACME-FY2025"), SqlServerProjectSchema.For("ACME-FY2025"));

    [Fact]
    public void For_MatchesWhitelist()
    {
        foreach (var id in new[] { "ACME-FY2025", "光寶2025", "合併案", "A_B (1)" })
            Assert.Matches("^prj_[a-z0-9]*_[0-9a-f]{8}$", SqlServerProjectSchema.For(id));
    }

    [Fact]
    public void For_NoCollision_WhenSanitizedEqual()
        => Assert.NotEqual(SqlServerProjectSchema.For("A-B"), SqlServerProjectSchema.For("AB"));

    [Fact]
    public void IsValid_RejectsInjection()
    {
        Assert.False(SqlServerProjectSchema.IsValid("prj_x]; DROP TABLE t;--"));
        Assert.False(SqlServerProjectSchema.IsValid("dbo"));
        Assert.True(SqlServerProjectSchema.IsValid(SqlServerProjectSchema.For("光寶2025")));
    }

    [Fact]
    public void QualifierFor_ReturnsBracketedSchemaWithDot()
    {
        var schema = SqlServerProjectSchema.For("ACME-FY2025");
        Assert.Equal($"[{schema}].", SqlServerProjectSchema.QualifierFor("ACME-FY2025"));
    }
}
