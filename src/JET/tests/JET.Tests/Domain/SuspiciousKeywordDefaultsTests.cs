using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class SuspiciousKeywordDefaultsTests
{
    // 裁定 2026-11-05-43：預設關鍵字回到舊版 R2 的 24 個詞，拿掉 Agent 在 2026-06-20 加的「帳外」，
    // 所以這兩個測試不再要求「帳外」，改成確認它不在清單裡。第一次失敗：Focused 收據 20261105-105902221-f810f5ee51144db6ac8a225084bd4509。
    [Fact]
    public void Defaults_RetainLegacySimplifiedVariants_WithoutAgentAddedKeyword()
    {
        // IDEA R2 的固定字串集合；不把繁簡轉換推廣到其他人員或科目識別值。
        foreach (var keyword in new[] { "调整", "回转", "冲销", "重分类", "避险", "重编", "错误", "计画外", "预算外" })
            Assert.Contains(keyword, SuspiciousKeywordDefaults.Defaults);
        Assert.DoesNotContain("帳外", SuspiciousKeywordDefaults.Defaults);
    }

    [Fact]
    public void Defaults_CoverMethodologyMinimumSet()
    {
        // 方法學最低標:調整、錯誤、迴轉、沖銷。
        foreach (var keyword in new[] { "調整", "錯誤", "迴轉", "沖銷" })
        {
            Assert.Contains(keyword, SuspiciousKeywordDefaults.Defaults);
        }
    }

    // 舊版 idea-tool.bas 第 8215 行的 24 個詞，順序也相同。
    [Fact]
    public void Defaults_MatchLegacyR2KeywordsInOrder()
    {
        Assert.Equal(
            new[]
            {
                "ADJ", "REV", "RECLASS", "SUSPENSE", "ERROR", "WRONG",
                "調整", "迴轉", "沖銷", "重分類", "避險", "重編", "錯誤", "計畫外", "預算外",
                "调整", "回转", "冲销", "重分类", "避险", "重编", "错误", "计画外", "预算外"
            },
            SuspiciousKeywordDefaults.Defaults);
    }
}
