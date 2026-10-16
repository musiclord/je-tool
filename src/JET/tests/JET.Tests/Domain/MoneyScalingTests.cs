using System.Globalization;
using FsCheck.Xunit;
using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class MoneyScalingTests
{
    [Property(Replay = "20260815,107", MaxTest = 256)]
    public bool TryToScaled_EveryPowerOfTenRoundTripsAndPreservesSign(int rawValue)
    {
        var expectedScaled = rawValue % 1_000_000_000L;

        foreach (var moneyScale in new[] { 1, 10, 100, 1_000, 10_000 })
        {
            var amount = expectedScaled / (decimal)moneyScale;
            if (!MoneyScaling.TryToScaled(amount, moneyScale, out var actualScaled)
                || actualScaled != expectedScaled
                || !MoneyScaling.TryToScaled(-amount, moneyScale, out var negatedScaled)
                || negatedScaled != -expectedScaled)
            {
                return false;
            }
        }

        return true;
    }

    [Fact]
    public void TryToScaled_ScalesWithAwayFromZeroRounding()
    {
        Assert.True(MoneyScaling.TryToScaled(100.50m, 10_000, out var scaled));
        Assert.Equal(1_005_000L, scaled);

        // AwayFromZero：±0.00005 × 10000 = ±0.5 → ±1
        Assert.True(MoneyScaling.TryToScaled(0.00005m, 10_000, out var halfUp));
        Assert.Equal(1L, halfUp);

        Assert.True(MoneyScaling.TryToScaled(-0.00005m, 10_000, out var halfDown));
        Assert.Equal(-1L, halfDown);
    }

    [Fact]
    public void TryParseAmount_AcceptsRealDataAndThousandsSeparators()
    {
        Assert.True(MoneyScaling.TryParseAmount("4353170.00", out var plain));
        Assert.True(MoneyScaling.TryToScaled(plain, 10_000, out var plainScaled));
        Assert.Equal(43_531_700_000L, plainScaled);

        Assert.True(MoneyScaling.TryParseAmount("4,353,170.00", out var grouped));
        Assert.Equal(plain, grouped);

        Assert.True(MoneyScaling.TryParseAmount("-50", out var negative));
        Assert.Equal(-50m, negative);
    }

    [Fact]
    public void TryParseAmount_RejectsGarbage()
    {
        Assert.False(MoneyScaling.TryParseAmount("abc", out _));
        Assert.False(MoneyScaling.TryParseAmount("12..3", out _));
        Assert.False(MoneyScaling.TryParseAmount(null, out _));
        Assert.False(MoneyScaling.TryParseAmount("   ", out _));
    }

    // 會計格式零（guide §3.1.2）：Excel 會計數字格式對 0 的顯示是單獨一個半形連字號，
    // 真實 PBC 的 TB 匯出通篇如此（" -   " 經 CSV cell trim 後為 "-"）。
    // 等價類：恰為 "-"（含前後空白）→ 0；帶號數字不受影響。
    [Theory]
    [InlineData("-")]
    [InlineData("  -  ")]
    public void TryParseAmount_LoneHyphenIsAccountingZero(string text)
    {
        Assert.True(MoneyScaling.TryParseAmount(text, out var value));
        Assert.Equal(0m, value);
    }

    [Fact]
    public void TryParseAmount_SignedZeroAndNegativeUnaffectedByAccountingZeroRule()
    {
        // 鎖定既有行為："-0" 走 decimal 解析（非會計零路徑）仍為 0、負數照常。
        Assert.True(MoneyScaling.TryParseAmount("-0", out var negZero));
        Assert.Equal(0m, negZero);

        Assert.True(MoneyScaling.TryParseAmount("-50", out var negative));
        Assert.Equal(-50m, negative);
    }

    // 會計零的拒絕等價類（guide §3.1.2：僅半形連字號單獨成立）：
    // 連字號變體（全形、em-dash）、多字元組合一律維持拒絕。
    [Theory]
    [InlineData("--")]
    [InlineData("-.")]
    [InlineData("—")]   // em-dash
    [InlineData("－")]  // 全形連字號
    [InlineData("- 0")]
    public void TryParseAmount_RejectsHyphenLookalikes(string text)
    {
        Assert.False(MoneyScaling.TryParseAmount(text, out _));
    }

    // 千分位、會計括號與尾端正負號的合法格式。預期值以不含千分位的純數字文字寫死，
    // 涵蓋首組 1 到 3 位、其後每組 3 位、有無小數點、前後正負號，以及正負號前有空白。
    // 尾端正負號前有空白時，有無千分位都要讀成同一個值（"1234 -" 本來就接受）。
    [Theory]
    [InlineData("(123)", "-123")]
    [InlineData("(1,234.50)", "-1234.50")]
    [InlineData("-1,234.50", "-1234.50")]
    [InlineData("1,234-", "-1234")]
    [InlineData("1,234 -", "-1234")]
    [InlineData("1,234 +", "1234")]
    [InlineData("1,234.50 -", "-1234.50")]
    [InlineData("1,234,567", "1234567")]
    [InlineData("12,345", "12345")]
    [InlineData("123,456", "123456")]
    public void TryParseAmount_AcceptsGroupedParenthesizedAndTrailingSignAmounts(string text, string expected)
    {
        Assert.True(MoneyScaling.TryParseAmount(text, out var value));
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), value);
    }

    // 千分位位置錯誤、括號再疊正負號、小數部分出現逗號，一律回傳 false，不丟例外。
    // 其中千分位位置要靠 MoneyScaling 自己擋：decimal.TryParse 會把 "12,34" 讀成 1234。
    [Theory]
    [InlineData("12,34")]      // 第二組不足 3 位
    [InlineData("1,23,456")]   // 中間一組不足 3 位
    [InlineData("1234,567")]   // 第一組超過 3 位
    [InlineData("1,23 -")]     // 尾端負號前有空白，第二組仍不足 3 位
    [InlineData(".1,5")]       // 逗號出現在小數部分
    [InlineData("(-123)")]     // 括號已是負號，不能再疊正負號
    [InlineData("(+123)")]
    [InlineData("(-1,234)")]
    public void TryParseAmount_RejectsMisplacedGroupsAndSignsWithoutThrowing(string text)
    {
        Assert.False(MoneyScaling.TryParseAmount(text, out _));
    }

    [Fact]
    public void TryToScaled_RejectsOverflow()
    {
        Assert.False(MoneyScaling.TryToScaled(decimal.MaxValue, 10_000, out _));
        Assert.False(MoneyScaling.TryToScaled(9_300_000_000_000_000m, 10_000, out _));
    }

    [Fact]
    public void TryToScaled_AcceptsBothInt64LimitsIncludingRoundingBackToTheLimit()
    {
        // 固定的十進位邊界與答案，不由 MoneyScaling 反算預期值。
        (decimal Amount, int Scale, long Expected)[] cases =
        [
            (9_223_372_036_854_775_807m, 1, long.MaxValue),
            (-9_223_372_036_854_775_808m, 1, long.MinValue),
            (922_337_203_685_477.5807m, 10_000, long.MaxValue),
            (-922_337_203_685_477.5808m, 10_000, long.MinValue),
            (922_337_203_685_477.58074m, 10_000, long.MaxValue),
            (-922_337_203_685_477.58084m, 10_000, long.MinValue),
        ];

        foreach (var item in cases)
        {
            Assert.True(MoneyScaling.TryToScaled(item.Amount, item.Scale, out var scaled));
            Assert.Equal(item.Expected, scaled);
        }
    }

    [Fact]
    public void TryToScaled_RejectsValuesAndRoundingImmediatelyBeyondBothInt64Limits()
    {
        (decimal Amount, int Scale)[] cases =
        [
            (9_223_372_036_854_775_808m, 1),
            (-9_223_372_036_854_775_809m, 1),
            (922_337_203_685_477.5808m, 10_000),
            (-922_337_203_685_477.5809m, 10_000),
            (922_337_203_685_477.58075m, 10_000),
            (-922_337_203_685_477.58085m, 10_000),
        ];

        foreach (var item in cases)
        {
            Assert.False(MoneyScaling.TryToScaled(item.Amount, item.Scale, out _));
        }
    }
}
