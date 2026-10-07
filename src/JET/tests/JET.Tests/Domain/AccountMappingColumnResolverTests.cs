using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// 科目配對欄位辨識（manifest import.accountMapping.fromFile 細節）：
/// 鎖定事務所英文標頭、中文標頭由關鍵字命中,未知標頭退位次 1/2/3 fallback。
/// oracle：欄位辨識規格（關鍵字命中優先,三者互異才採用,否則整組退位次）。
/// </summary>
public sealed class AccountMappingColumnResolverTests
{
    public static IEnumerable<object[]> K7HeaderOrders()
    {
        int[][] orders = [[0,1,2], [0,2,1], [1,0,2], [1,2,0], [2,0,1], [2,1,0]];
        string[][] names =
        [
            ["GL_Number", "GL_Name", "Standardized Account Name*"],
            ["GL_NUMBER", "GL_NAME", "STANDARDIZED_ACCOUNT_NAME"],
            ["科目編號", "科目名稱", "科目分類"]
        ];
        foreach (var set in names)
        foreach (var order in orders)
            yield return [order.Select(i => set[i]).ToArray(), set[0], set[1], set[2]];
        yield return [new[] { "Comment account name", "Standardized Account Name*", " gl_NAME ", "　GL_Number　" }, "　GL_Number　", " gl_NAME ", "Standardized Account Name*"];
        yield return [new[] { "Other", "Standardized Account Name*", "GL_Number" }, "GL_Number", "Other", "Standardized Account Name*"];
        yield return [new[] { "c1", "c2", "c3", "c4" }, "c1", "c2", "c3"];
    }

    [Theory]
    [MemberData(nameof(K7HeaderOrders))]
    public void K7_ExactNamesThenUnclaimedKeywordsThenPositions(
        string[] columns, string code, string name, string category)
    {
        var result = AccountMappingColumnResolver.Resolve(columns);
        Assert.Equal(new AccountMappingColumnResolver.Resolution(code, name, category), result);
    }

    [Fact]
    public void Resolve_FirmEnglishHeaders_MatchByKeyword()
    {
        var resolution = AccountMappingColumnResolver.Resolve(
            ["GL_NUMBER", "GL_NAME", "STANDARDIZED_ACCOUNT_NAME"]);

        Assert.Equal("GL_NUMBER", resolution.CodeColumn);
        Assert.Equal("GL_NAME", resolution.NameColumn);
        Assert.Equal("STANDARDIZED_ACCOUNT_NAME", resolution.CategoryColumn);
    }

    [Fact]
    public void Resolve_ChineseHeaders_StillMatch()
    {
        var resolution = AccountMappingColumnResolver.Resolve(["科目代號", "科目名稱", "標準化分類"]);

        Assert.Equal("科目代號", resolution.CodeColumn);
        Assert.Equal("科目名稱", resolution.NameColumn);
        Assert.Equal("標準化分類", resolution.CategoryColumn);
    }

    [Fact]
    public void Resolve_UnknownHeaders_FallsBackToPositional()
    {
        var resolution = AccountMappingColumnResolver.Resolve(["c1", "c2", "c3"]);

        Assert.Equal("c1", resolution.CodeColumn);
        Assert.Equal("c2", resolution.NameColumn);
        Assert.Equal("c3", resolution.CategoryColumn);
    }

    // 關鍵字由最專一的分類先認領，含「name」的分類欄不會被科目名稱的寬鬆關鍵字搶走。
    [Theory]
    [InlineData("code", "description", "standardized account name")]
    [InlineData("code", "description", "category name")]
    public void SpecificCategoryKeywordClaimsBeforeBroadName(string code, string name, string category)
    {
        Assert.Equal(new AccountMappingColumnResolver.Resolution(code, name, category),
            AccountMappingColumnResolver.Resolve([code, name, category]));
    }

    [Fact]
    public void ResolutionRecordsEachColumnsIndependentEvidence()
    {
        var result = AccountMappingColumnResolver.ResolveDetailed(["GL_Number", "description", "category name"]);
        Assert.Equal(AccountMappingColumnResolver.MatchMethod.ExactName, result.Code.Method);
        Assert.Equal(AccountMappingColumnResolver.MatchMethod.Position, result.Name.Method);
        Assert.Equal(AccountMappingColumnResolver.MatchMethod.Keyword, result.Category.Method);
        Assert.Equal("description", result.Name.Column);
    }
}
