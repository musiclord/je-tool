using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

/// <summary>
/// 國定假日清單與補班日清單的檔案怎麼讀成日期清單：找不到日期欄、每一列收不收、名稱怎麼取，以及讀不成的列怎麼回報。
/// 這些都由匯入時實際執行的那一段程式決定，測試直接呼叫它。欄名寫法與日期寫法的測試在 <see cref="IntakeMappingProgramTests"/>。
/// </summary>
public sealed class CalendarFileProjectionTests
{
    private static CalendarPlan Plan(CalendarDayType dayType) =>
        JetAuditProgram.Plan(new CalendarFileRequest(
            dayType == CalendarDayType.Holiday ? "import.holiday.fromFile" : "import.makeupDay.fromFile",
            "project",
            dayType,
            "calendar.xlsx",
            FileExists: true,
            Extension: ".xlsx"));

    private static StagingRow Row(int sourceRowNumber, params (string Key, string Value)[] cells) =>
        new(sourceRowNumber, cells.ToDictionary(cell => cell.Key, cell => cell.Value, StringComparer.Ordinal));

    private static async IAsyncEnumerable<StagingRow> Rows(params StagingRow[] rows)
    {
        await Task.Yield();
        foreach (var row in rows)
        {
            yield return row;
        }
    }

    private static Task<IReadOnlyList<CalendarDayEntry>> ProjectAsync(
        CalendarDayType dayType, IReadOnlyList<string> columns, params StagingRow[] rows) =>
        JetAuditProgram.ProjectCalendarFileAsync(Plan(dayType), columns, Rows(rows), CancellationToken.None);

    [Fact]
    public async Task MissingDateColumn_ThrowsProjectionFailedNamingTheColumn()
    {
        var error = await Assert.ThrowsAsync<JetActionException>(() =>
            ProjectAsync(CalendarDayType.Holiday, ["something", "else"], Row(3, ("something", "2025-01-01"))));

        Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
        Assert.Contains("Date_of_Holiday", error.Message);
    }

    [Fact]
    public async Task ValidHolidayRow_IsIncludedWithItsName()
    {
        var entries = await ProjectAsync(
            CalendarDayType.Holiday,
            ["Date_of_Holiday", "Holiday_Name", "IS_Holiday"],
            Row(3, ("Date_of_Holiday", "2025-01-01"), ("Holiday_Name", "元旦"), ("IS_Holiday", "Y")));

        var entry = Assert.Single(entries);
        Assert.Equal("2025-01-01", entry.Date);
        Assert.Equal("元旦", entry.Name);
    }

    [Fact]
    public async Task HolidayRowMarkedN_IsSkipped()
    {
        var entries = await ProjectAsync(
            CalendarDayType.Holiday,
            ["Date_of_Holiday", "Holiday_Name", "IS_Holiday"],
            Row(4, ("Date_of_Holiday", "2025-03-01"), ("Holiday_Name", "非假日"), ("IS_Holiday", "N")));

        Assert.Empty(entries);
    }

    // 沒有 IS_Holiday 欄時不過濾，清單上每個日期都收。
    [Fact]
    public async Task HolidayFlagColumnAbsent_KeepsTheRow()
    {
        var entries = await ProjectAsync(
            CalendarDayType.Holiday,
            ["Date_of_Holiday", "Holiday_Name"],
            Row(3, ("Date_of_Holiday", "2025-01-01"), ("Holiday_Name", "元旦")));

        var entry = Assert.Single(entries);
        Assert.Equal("元旦", entry.Name);
    }

    [Fact]
    public async Task NameColumnAbsent_NameIsNull()
    {
        var entries = await ProjectAsync(
            CalendarDayType.Holiday,
            ["Date_of_Holiday"],
            Row(3, ("Date_of_Holiday", "2025-01-01")));

        var entry = Assert.Single(entries);
        Assert.Null(entry.Name);
    }

    [Fact]
    public async Task NameCellBlank_NameIsNull()
    {
        var entries = await ProjectAsync(
            CalendarDayType.Holiday,
            ["Date_of_Holiday", "Holiday_Name"],
            Row(3, ("Date_of_Holiday", "2025-01-01"), ("Holiday_Name", "   ")));

        var entry = Assert.Single(entries);
        Assert.Null(entry.Name);
    }

    [Fact]
    public async Task UnreadableDate_FailsNamingTheValueAndTheRow()
    {
        var error = await Assert.ThrowsAsync<JetActionException>(() =>
            ProjectAsync(
                CalendarDayType.Holiday,
                ["Date_of_Holiday", "Holiday_Name", "IS_Holiday"],
                Row(5, ("Date_of_Holiday", "2025/13/01"), ("Holiday_Name", "元旦"), ("IS_Holiday", "Y"))));

        Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
        Assert.Contains("2025/13/01", error.Message);
        Assert.Contains("第 5 列", error.Message);
    }

    [Fact]
    public async Task MakeupRow_UsesTheDescriptionAsName_AndHasNoHolidayFilter()
    {
        var entries = await ProjectAsync(
            CalendarDayType.Makeup,
            ["Date_of_MakeUpday", "MakeUpDay_Desc"],
            Row(3, ("Date_of_MakeUpday", "2025-02-08"), ("MakeUpDay_Desc", "春節補班")));

        var entry = Assert.Single(entries);
        Assert.Equal("2025-02-08", entry.Date);
        Assert.Equal("春節補班", entry.Name);
    }

    // 這一列沒有日期那一格。
    [Fact]
    public async Task RowWithoutADateCell_FailsWithBlankDate()
    {
        var error = await Assert.ThrowsAsync<JetActionException>(() =>
            ProjectAsync(
                CalendarDayType.Holiday,
                ["Date_of_Holiday", "Holiday_Name", "IS_Holiday"],
                Row(8, ("Holiday_Name", "元旦"), ("IS_Holiday", "Y"))));

        Assert.Equal("檔案有 1 列無法讀取：第 8 列：日期空白", error.Message);
    }

    [Fact]
    public async Task WhitespaceDate_FailsWithBlankDate()
    {
        var error = await Assert.ThrowsAsync<JetActionException>(() =>
            ProjectAsync(
                CalendarDayType.Holiday,
                ["Date_of_Holiday", "Holiday_Name", "IS_Holiday"],
                Row(9, ("Date_of_Holiday", "   "), ("Holiday_Name", "元旦"), ("IS_Holiday", "Y"))));

        Assert.Equal("檔案有 1 列無法讀取：第 9 列：日期空白", error.Message);
    }

    [Fact]
    public async Task ValidRow_TrimsDateNameAndFlag()
    {
        var entries = await ProjectAsync(
            CalendarDayType.Holiday,
            ["Date_of_Holiday", "Holiday_Name", "IS_Holiday"],
            Row(10, ("Date_of_Holiday", " 2025-01-01 "), ("Holiday_Name", " 元旦 "), ("IS_Holiday", " y ")));

        Assert.Equal([new CalendarDayEntry("2025-01-01", "元旦")], entries);
    }
}
