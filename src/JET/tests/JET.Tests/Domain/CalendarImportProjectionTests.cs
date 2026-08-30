using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class CalendarImportProjectionTests
{
    private static StagingRow Row(int n, params (string Key, string Value)[] cells)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in cells)
        {
            values[key] = value;
        }

        return new StagingRow(n, values);
    }

    [Fact]
    public void Resolve_HolidayColumns_ByKeyword()
    {
        var resolution = CalendarImportColumnResolver.Resolve(
            ["Date_of_Holiday", "Holiday_Name", "IS_Holiday"], CalendarDayType.Holiday);

        Assert.Equal("Date_of_Holiday", resolution.DateColumn);
        Assert.Equal("Holiday_Name", resolution.NameColumn);
        Assert.Equal("IS_Holiday", resolution.IsHolidayColumn);
    }

    [Fact]
    public void Resolve_MakeupColumns_ByKeyword()
    {
        var resolution = CalendarImportColumnResolver.Resolve(
            ["Date_of_MakeUpday", "MakeUpDay_Desc"], CalendarDayType.Makeup);

        Assert.Equal("Date_of_MakeUpday", resolution.DateColumn);
        Assert.Equal("MakeUpDay_Desc", resolution.NameColumn);
        Assert.Null(resolution.IsHolidayColumn);
    }

    [Fact]
    public void Resolve_MissingDateColumn_ThrowsProjectionFailed()
    {
        var ex = Assert.Throws<JetActionException>(
            () => CalendarImportColumnResolver.Resolve(["something", "else"], CalendarDayType.Holiday));

        Assert.Equal(JetErrorCodes.ProjectionFailed, ex.Code);
        Assert.Contains("Date_of_Holiday", ex.Message);
    }

    [Fact]
    public void Project_ValidHolidayRow_Included()
    {
        var resolution = new CalendarImportColumnResolution("Date_of_Holiday", "Holiday_Name", "IS_Holiday");
        var outcome = CalendarDayProjector.Project(
            Row(3, ("Date_of_Holiday", "2025-01-01"), ("Holiday_Name", "元旦"), ("IS_Holiday", "Y")),
            CalendarDayType.Holiday, resolution, out var entry, out _);

        Assert.Equal(CalendarRowOutcome.Included, outcome);
        Assert.Equal("2025-01-01", entry.Date);
        Assert.Equal("元旦", entry.Name);
    }

    [Fact]
    public void Project_IsHolidayN_Skipped()
    {
        var resolution = new CalendarImportColumnResolution("Date_of_Holiday", "Holiday_Name", "IS_Holiday");
        var outcome = CalendarDayProjector.Project(
            Row(4, ("Date_of_Holiday", "2025-03-01"), ("Holiday_Name", "非假日"), ("IS_Holiday", "N")),
            CalendarDayType.Holiday, resolution, out _, out _);

        Assert.Equal(CalendarRowOutcome.Skipped, outcome);
    }

    [Fact]
    public void Project_IsHolidayColumnAbsent_KeepsRow()
    {
        // IS_Holiday 欄缺席 → 不過濾(全收)。
        var resolution = new CalendarImportColumnResolution("Date_of_Holiday", "Holiday_Name", null);
        var outcome = CalendarDayProjector.Project(
            Row(3, ("Date_of_Holiday", "2025-01-01"), ("Holiday_Name", "元旦")),
            CalendarDayType.Holiday, resolution, out var entry, out _);

        Assert.Equal(CalendarRowOutcome.Included, outcome);
        Assert.Equal("元旦", entry.Name);
    }

    [Fact]
    public void Project_NameColumnAbsentOrBlank_NameIsNull()
    {
        var resolution = new CalendarImportColumnResolution("Date_of_Holiday", null, null);
        var outcome = CalendarDayProjector.Project(
            Row(3, ("Date_of_Holiday", "2025-01-01")),
            CalendarDayType.Holiday, resolution, out var entry, out _);

        Assert.Equal(CalendarRowOutcome.Included, outcome);
        Assert.Null(entry.Name);
    }

    [Fact]
    public void Project_BadDate_Failed()
    {
        var resolution = new CalendarImportColumnResolution("Date_of_Holiday", "Holiday_Name", "IS_Holiday");
        var outcome = CalendarDayProjector.Project(
            Row(5, ("Date_of_Holiday", "2025/01/01"), ("Holiday_Name", "元旦"), ("IS_Holiday", "Y")),
            CalendarDayType.Holiday, resolution, out _, out var error);

        Assert.Equal(CalendarRowOutcome.Failed, outcome);
        Assert.Contains("2025/01/01", error.Message);
        Assert.Equal(5, error.SourceRowNumber);
    }

    [Fact]
    public void Project_MakeupRow_UsesDescAsName_NoIsHolidayFilter()
    {
        var resolution = new CalendarImportColumnResolution("Date_of_MakeUpday", "MakeUpDay_Desc", null);
        var outcome = CalendarDayProjector.Project(
            Row(3, ("Date_of_MakeUpday", "2025-02-08"), ("MakeUpDay_Desc", "春節補班")),
            CalendarDayType.Makeup, resolution, out var entry, out _);

        Assert.Equal(CalendarRowOutcome.Included, outcome);
        Assert.Equal("2025-02-08", entry.Date);
        Assert.Equal("春節補班", entry.Name);
    }
}
