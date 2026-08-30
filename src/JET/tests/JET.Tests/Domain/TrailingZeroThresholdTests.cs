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

    [Fact]
    public void UnitModulus_UsesOnlyPowerOfTen()
    {
        // threshold 3 → 主單位整數 % 1,000；MoneyScale 不屬尾數模數。
        Assert.Equal(1_000L, TrailingZeroThreshold.UnitModulus(3));
    }

    [Fact]
    public void UnitModulus_DefaultThreshold_IsOneMillion()
    {
        Assert.Equal(
            1_000_000L,
            TrailingZeroThreshold.UnitModulus(TrailingZeroThreshold.DefaultZerosThreshold));
    }
}
