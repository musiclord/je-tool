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
        // 匯入人員清單必須指定 sourceColumn；三種人員欄名都可使用。
        Assert.Equal(header, AuthorizedPreparerColumnResolver.Resolve([header], header));
    }

    [Fact]
    public void Resolve_MissingSelection_RejectsInsteadOfFallingBackToFirstColumn()
    {
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
