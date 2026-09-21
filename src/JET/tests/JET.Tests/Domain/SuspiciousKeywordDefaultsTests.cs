using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class SuspiciousKeywordDefaultsTests
{
    [Fact]
    public void Defaults_RetainLegacySimplifiedVariantsAndCurrentAdditionalKeyword()
    {
        // IDEA R2 的固定字串集合；不把繁簡轉換推廣到其他人員或科目識別值。
        foreach (var keyword in new[] { "调整", "回转", "冲销", "重分类", "避险", "重编", "错误", "计画外", "预算外", "帳外" })
            Assert.Contains(keyword, SuspiciousKeywordDefaults.Defaults);
    }

    [Fact]
    public void Defaults_CoverMethodologyMinimumSet()
    {
        // 方法學最低標:調整、錯誤、迴轉、沖銷、帳外(guide §5)。
        foreach (var keyword in new[] { "調整", "錯誤", "迴轉", "沖銷", "帳外" })
        {
            Assert.Contains(keyword, SuspiciousKeywordDefaults.Defaults);
        }
    }
}
