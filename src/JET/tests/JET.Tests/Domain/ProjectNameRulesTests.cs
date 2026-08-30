using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// 案件名稱 / projectId 字元規則(ProjectNameRules)。同時是 path-traversal 守衛;
/// 白名單須為舊 32-hex GUID 的超集(既有專案零遷移)。
/// </summary>
public sealed class ProjectNameRulesTests
{
    [Theory]
    [InlineData("2025年度A公司查核")]
    [InlineData("Audit Case (2025)")]
    [InlineData("查核案 （甲）")]
    [InlineData("案件_2025-Q1")]
    [InlineData("a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6")] // 舊 32-hex GUID：必須仍合法(零遷移)
    public void IsValid_AcceptsLegalNamesAndLegacyGuids(string name)
        => Assert.True(ProjectNameRules.IsValid(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" 前導空白")]
    [InlineData("結尾空白 ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a..b")]
    [InlineData("C:name")]
    [InlineData("name*?")]
    [InlineData("a<b>c")]
    [InlineData("a|b")]
    [InlineData("a\"b")]
    [InlineData("有.句點")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public void IsValid_RejectsIllegalReservedAndTraversal(string? name)
        => Assert.False(ProjectNameRules.IsValid(name));

    [Fact]
    public void IsValid_RejectsOverLength()
        => Assert.False(ProjectNameRules.IsValid(new string('字', ProjectNameRules.MaxLength + 1)));

    [Fact]
    public void IsValid_AcceptsMaxLength()
        => Assert.True(ProjectNameRules.IsValid(new string('字', ProjectNameRules.MaxLength)));

    [Fact]
    public void Validate_ReturnsNullForLegal()
        => Assert.Null(ProjectNameRules.Validate("正常案件"));

    [Fact]
    public void Validate_ReturnsReasonForIllegal()
        => Assert.NotNull(ProjectNameRules.Validate("a/b"));
}
