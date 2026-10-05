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
        // 2026-10-04 第 3 批 L12 裁定 sourceColumn 必填；保留三種人員欄名都可使用的檢查。
        Assert.Equal(header, AuthorizedPreparerColumnResolver.Resolve([header], header));
    }

    [Fact]
    public void Resolve_MissingSelection_RejectsInsteadOfFallingBackToFirstColumn()
    {
        // 2026-10-04 第 3 批 L12 裁定 sourceColumn 必填；原第一欄退路改為明確拒絕，不能猜欄。
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
