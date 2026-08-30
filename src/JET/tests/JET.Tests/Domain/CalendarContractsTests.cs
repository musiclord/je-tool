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

    [Fact]
    public void Project_MissingDateColumn_FailsWithBlankDateError()
    {
        var resolution = new CalendarImportColumnResolution("Date_of_Holiday", "Holiday_Name", "IS_Holiday");
        var row = Row(8, ("Holiday_Name", "元旦"), ("IS_Holiday", "Y"));

        var outcome = CalendarDayProjector.Project(
            row,
            CalendarDayType.Holiday,
            resolution,
            out _,
            out var error);

        Assert.Equal(CalendarRowOutcome.Failed, outcome);
        Assert.Equal(8, error.SourceRowNumber);
        Assert.Equal("第 8 列:日期空白。", error.Message);
    }

    [Fact]
    public void Project_WhitespaceDate_FailsWithBlankDateError()
    {
        var resolution = new CalendarImportColumnResolution("Date_of_Holiday", "Holiday_Name", "IS_Holiday");
        var row = Row(9, ("Date_of_Holiday", "   "), ("Holiday_Name", "元旦"), ("IS_Holiday", "Y"));

        var outcome = CalendarDayProjector.Project(
            row,
            CalendarDayType.Holiday,
            resolution,
            out _,
            out var error);

        Assert.Equal(CalendarRowOutcome.Failed, outcome);
        Assert.Equal(9, error.SourceRowNumber);
        Assert.Equal("第 9 列:日期空白。", error.Message);
    }

    [Fact]
    public void Project_ValidRow_TrimsDateAndName()
    {
        var resolution = new CalendarImportColumnResolution("Date_of_Holiday", "Holiday_Name", "IS_Holiday");
        var row = Row(10, ("Date_of_Holiday", " 2025-01-01 "), ("Holiday_Name", " 元旦 "), ("IS_Holiday", " y "));

        var outcome = CalendarDayProjector.Project(
            row,
            CalendarDayType.Holiday,
            resolution,
            out var entry,
            out _);

        Assert.Equal(CalendarRowOutcome.Included, outcome);
        Assert.Equal(new CalendarDayEntry("2025-01-01", "元旦"), entry);
    }

    private static StagingRow Row(int n, params (string Key, string Value)[] cells)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in cells)
        {
            values[key] = value;
        }

        return new StagingRow(n, values);
    }
}
