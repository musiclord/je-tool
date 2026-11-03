using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class AuthorizedPreparerColumnResolverTests
{
    [Theory]
    [InlineData("AUTHORIZED_PREPARER")]
    [InlineData("編製人員")]
    [InlineData("姓名")]
    public void Resolve_ReturnsExplicitlySelectedNameColumn(string header)
    {
        // sourceColumn 改成必填，後端不再猜人員欄；保留三種人員欄名都可使用的檢查。
        Assert.Equal(header, AuthorizedPreparerColumnResolver.Resolve([header], header));
    }

    [Fact]
    public void Resolve_MissingSelection_RejectsInsteadOfFallingBackToFirstColumn()
    {
        // sourceColumn 改成必填，後端不再猜人員欄；原第一欄退路改為明確拒絕，不能猜欄。
        var error = Assert.Throws<JetActionException>(() =>
            AuthorizedPreparerColumnResolver.Resolve(["欄A", "欄B"]));
        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
        Assert.Equal("sourceColumn", error.Field);
    }

    [Fact]
    public void Resolve_EmptyColumns_Throws()
    {
        Assert.Throws<JetActionException>(() => AuthorizedPreparerColumnResolver.Resolve([]));
    }
}
