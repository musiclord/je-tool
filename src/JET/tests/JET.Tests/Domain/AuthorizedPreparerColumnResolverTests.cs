using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class AuthorizedPreparerColumnResolverTests
{
    [Theory]
    [InlineData("AUTHORIZED_PREPARER")]
    [InlineData("編製人員")]
    [InlineData("姓名")]
    public void Resolve_FindsNameColumnByKeyword(string header)
    {
        Assert.Equal(header, AuthorizedPreparerColumnResolver.Resolve([header]));
    }

    [Fact]
    public void Resolve_FallsBackToFirstColumn()
    {
        Assert.Equal("欄A", AuthorizedPreparerColumnResolver.Resolve(["欄A", "欄B"]));
    }

    [Fact]
    public void Resolve_EmptyColumns_Throws()
    {
        Assert.Throws<JetActionException>(() => AuthorizedPreparerColumnResolver.Resolve([]));
    }
}
