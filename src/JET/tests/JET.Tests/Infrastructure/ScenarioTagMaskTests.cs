using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 底稿第 4 與 4-1 頁共用的情境位置遮罩：SQL 片段與解碼都是固定答案，位置上限跟著已存情境上限。
/// 2026-10-22 技術債一輪把兩份一樣的寫法收成這一個類別。
/// </summary>
public sealed class ScenarioTagMaskTests
{
    [Fact]
    public void MaxPositions_FollowsSavedScenarioLimit()
    {
        Assert.Equal(FilterScenarioLimits.MaxSavedScenarios, ScenarioTagMask.MaxPositions);
        Assert.Equal(10, ScenarioTagMask.MaxPositions);
    }

    [Fact]
    public void SumSql_EncodesEachPositionAsItsOwnBit()
    {
        const string expected =
            "SUM(CASE scenario_position " +
            "WHEN 1 THEN 1 WHEN 2 THEN 2 WHEN 3 THEN 4 WHEN 4 THEN 8 WHEN 5 THEN 16 " +
            "WHEN 6 THEN 32 WHEN 7 THEN 64 WHEN 8 THEN 128 WHEN 9 THEN 256 WHEN 10 THEN 512 " +
            "ELSE 0 END)";

        Assert.Equal(expected, ScenarioTagMask.SumSql("scenario_position"));
    }

    [Fact]
    public void SumSql_KeepsQualifiedExpressionVerbatim()
    {
        Assert.StartsWith("SUM(CASE tag_source.scenario_position WHEN 1 THEN 1 ", ScenarioTagMask.SumSql("tag_source.scenario_position"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SumSql_RejectsBlankExpression(string expression)
    {
        Assert.ThrowsAny<ArgumentException>(() => ScenarioTagMask.SumSql(expression));
    }

    [Theory]
    [InlineData(0L, new int[0])]
    [InlineData(1L, new[] { 1 })]
    [InlineData(2L, new[] { 2 })]
    [InlineData(5L, new[] { 1, 3 })]
    [InlineData(512L, new[] { 10 })]
    [InlineData(1023L, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })]
    public void DecodePositions_ReturnsAscendingPositions(long tagMask, int[] expected)
    {
        Assert.Equal(expected, ScenarioTagMask.DecodePositions(tagMask));
    }

    [Fact]
    public void DecodePositions_IgnoresBitsAboveTheLimit()
    {
        // 第 11 位以上不是合法情境位置；遮罩多出來的位元不產生位置。
        Assert.Equal(new[] { 1 }, ScenarioTagMask.DecodePositions(1L | (1L << 10) | (1L << 40)));
    }
}
