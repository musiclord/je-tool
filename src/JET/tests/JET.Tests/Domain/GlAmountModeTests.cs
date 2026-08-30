using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class GlAmountModeTests
{
    [Theory]
    [InlineData(" signed ", GlAmountMode.SignedAmount)]
    [InlineData("SIDE", GlAmountMode.AmountWithSide)]
    [InlineData("flag", GlAmountMode.AmountWithFlag)]
    [InlineData("DUAL", GlAmountMode.DualAmount)]
    public void TryParse_KnownWireName_ReturnsExpectedMode(string wireName, GlAmountMode expected)
    {
        var parsed = GlAmountModeNames.TryParse(wireName, out var mode);

        Assert.True(parsed);
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void TryParse_UnknownWireName_ReturnsFalseAndDefaultMode(string? wireName)
    {
        var parsed = GlAmountModeNames.TryParse(wireName, out var mode);

        Assert.False(parsed);
        Assert.Equal(default, mode);
    }

    [Theory]
    [InlineData(GlAmountMode.SignedAmount, "signed")]
    [InlineData(GlAmountMode.AmountWithSide, "side")]
    [InlineData(GlAmountMode.AmountWithFlag, "flag")]
    [InlineData(GlAmountMode.DualAmount, "dual")]
    public void ToWireName_KnownMode_ReturnsExpectedName(GlAmountMode mode, string expected)
    {
        Assert.Equal(expected, GlAmountModeNames.ToWireName(mode));
    }

    [Fact]
    public void ToWireName_InvalidMode_ThrowsArgumentOutOfRangeException()
    {
        const GlAmountMode invalidMode = (GlAmountMode)999;

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => GlAmountModeNames.ToWireName(invalidMode));

        Assert.Equal("mode", exception.ParamName);
        Assert.Equal(invalidMode, exception.ActualValue);
    }
}
