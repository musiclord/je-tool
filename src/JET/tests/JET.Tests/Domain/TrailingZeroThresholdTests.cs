using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class TrailingZeroThresholdTests
{
    [Fact]
    public void DefaultZerosThreshold_IsSix()
    {
        // 方法學預設:連續 6 個 0(= 1,000,000 的倍數)。
        Assert.Equal(6, TrailingZeroThreshold.DefaultZerosThreshold);
    }

    // 預篩選收斂計畫階段九：尾零規則改由尾數述詞比對全 0 樣態，UnitModulus 隨之移除，原本兩則模數測試改鎖住樣態與位數範圍。
    // 第一次失敗：Build 收據 20261024-195205380-7ff4a86d8e454b30b80f473242fceabd（找不到 UnitModulus）。
    [Fact]
    public void ZeroPattern_HasOneZeroPerDigit()
    {
        Assert.Equal("000", TrailingZeroThreshold.ZeroPattern(3));
        Assert.Equal("000000", TrailingZeroThreshold.ZeroPattern(TrailingZeroThreshold.DefaultZerosThreshold));
    }

    [Fact]
    public void ZeroPattern_RejectsDigitsOutsideTheAllowedRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrailingZeroThreshold.ZeroPattern(TrailingZeroThreshold.MinCustomDigits - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrailingZeroThreshold.ZeroPattern(TrailingZeroThreshold.MaxCustomDigits + 1));
    }
}
