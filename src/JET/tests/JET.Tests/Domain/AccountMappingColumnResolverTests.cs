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
}
