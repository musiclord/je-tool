using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class TbChangeModeTests
{
    [Theory]
    [InlineData(" DIRECT ", TbChangeMode.DirectChange)]
    [InlineData(" DEBITCREDIT ", TbChangeMode.DebitCredit)]
    [InlineData(" OPENCLOSE ", TbChangeMode.OpenClose)]
    [InlineData(" OPENCLOSEBYSIDE ", TbChangeMode.OpenCloseBySide)]
    public void TryParse_KnownWireName_ReturnsExpectedMode(string wireName, TbChangeMode expected)
    {
        var parsed = TbChangeModeNames.TryParse(wireName, out var mode);

        Assert.True(parsed);
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void TryParse_UnknownWireName_ReturnsFalseAndDefaultMode(string? wireName)
    {
        var parsed = TbChangeModeNames.TryParse(wireName, out var mode);

        Assert.False(parsed);
        Assert.Equal(default, mode);
    }

    [Theory]
    [InlineData(TbChangeMode.DirectChange, "direct")]
    [InlineData(TbChangeMode.DebitCredit, "debitCredit")]
    [InlineData(TbChangeMode.OpenClose, "openClose")]
    [InlineData(TbChangeMode.OpenCloseBySide, "openCloseBySide")]
    public void ToWireName_KnownMode_ReturnsExpectedName(TbChangeMode mode, string expected)
    {
        Assert.Equal(expected, TbChangeModeNames.ToWireName(mode));
    }

    [Fact]
    public void ToWireName_InvalidMode_ThrowsArgumentOutOfRangeException()
    {
        const TbChangeMode invalidMode = (TbChangeMode)999;

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => TbChangeModeNames.ToWireName(invalidMode));

        Assert.Equal("mode", exception.ParamName);
        Assert.Equal(invalidMode, exception.ActualValue);
    }
}
