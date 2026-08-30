using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class SuspiciousKeywordDefaultsTests
{
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
