using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// CsvDialectDetector 測試（guide §3.1.1 分隔符偵測規則）。
/// oracle：手工 fixture（取樣文字可人工數出各候選的引號外出現次數）。
/// 設計技術：決策表——候選 × 引號干擾 × 一致性 × 並列。
/// </summary>
public sealed class CsvDialectDetectorTests
{
    [Theory]
    [InlineData("a,b,c\n1,2,3\n4,5,6", ',')]
    [InlineData("a\tb\tc\n1\t2\t3", '\t')]
    [InlineData("a;b;c\n1;2;3", ';')]
    [InlineData("a|b|c\n1|2|3", '|')]
    public void DetectDelimiter_ConsistentCandidate_Wins(string sample, char expected)
    {
        Assert.Equal(expected, CsvDialectDetector.DetectDelimiter(sample));
    }

    [Fact]
    public void DetectDelimiter_QuotedCommasDoNotCount()
    {
        // 真分隔符是分號；引號內的逗號（千分位金額）每列數量不一，不得勝出
        var sample = "科目;金額;摘要\n1101;\"1,234,567.00\";期初\n1102;\"99.00\";\"調整, 沖銷\"";

        Assert.Equal(';', CsvDialectDetector.DetectDelimiter(sample));
    }

    [Fact]
    public void DetectDelimiter_QuotedNewlineKeepsLogicalLineTogether()
    {
        // 第二列的摘要含換行：仍是一個邏輯列，tab 每列 2 個 → tab 勝出
        var sample = "a\tb\tc\n1\t\"x\ny\"\t3";

        Assert.Equal('\t', CsvDialectDetector.DetectDelimiter(sample));
    }

    [Fact]
    public void DetectDelimiter_TieBreaksByFixedPriority()
    {
        // 逗號與分號每列各 2 個且皆一致：依固定優先序 , > \t > ; > | 取逗號
        var sample = "a,b;c,d;e\n1,2;3,4;5";

        Assert.Equal(',', CsvDialectDetector.DetectDelimiter(sample));
    }

    [Fact]
    public void DetectDelimiter_InconsistentCounts_FallsBackToHeaderMax()
    {
        // 逗號每列數量不一致（2,1）→ 一致性檢查失敗；fallback 取標頭列出現最多者（逗號 2）
        var sample = "a,b,c\n1,2";

        Assert.Equal(',', CsvDialectDetector.DetectDelimiter(sample));
    }

    [Fact]
    public void DetectDelimiter_SingleColumn_ReturnsNull()
    {
        Assert.Null(CsvDialectDetector.DetectDelimiter("日期\n2025-01-01\n2025-01-02"));
    }

    [Fact]
    public void DetectDelimiter_EmptySample_ReturnsNull()
    {
        Assert.Null(CsvDialectDetector.DetectDelimiter(""));
    }
}
