using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class CalendarContractsTests
{
    [Theory]
    [InlineData(CalendarDayType.Holiday, "holiday")]
    [InlineData(CalendarDayType.Makeup, "makeup")]
    public void ToStorageName_KnownCalendarDayType_ReturnsStorageName(CalendarDayType type, string expected)
    {
        var storageName = type.ToStorageName();

        Assert.Equal(expected, storageName);
    }

    [Fact]
    public void ToStorageName_InvalidEnumValue_ThrowsArgumentOutOfRange()
    {
        var invalidType = (CalendarDayType)42;

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => invalidType.ToStorageName());

        Assert.Equal("type", ex.ParamName);
        Assert.Equal(invalidType, ex.ActualValue);
    }
}
