using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;
using Xunit;

namespace JET.Tests.Application;

public sealed class CalendarTemplateRoundTripTests
{
    // jet-guide section 2 and test-environment-notes: row 1 is instructions,
    // row 2 contains headers, and only Y rows from IS_Holiday are imported.
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task CopiedBundledCalendarTemplates_ImportTheirDocumentedRows(string provider)
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        { caseName = "Synthetic calendar round trip", periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider }));
        var id = created.GetProperty("projectId").GetString()!;
        var copied = await host.DispatchAsync("export.calendarTemplates");
        Assert.Equal(new[] { "Holiday2025TW.xlsx", "MakeUpDay2025TW.xlsx" }, copied.GetProperty("files").EnumerateArray().Select(file => file.GetProperty("fileName").GetString()));
        var expected = new Dictionary<string, string[]>();
        foreach (var (fileName, column, action) in new[]
        {
            ("Holiday2025TW.xlsx", "Date_of_Holiday", "import.holiday.fromFile"),
            ("MakeUpDay2025TW.xlsx", "Date_of_MakeUpday", "import.makeupDay.fromFile")
        })
        {
            var path = Path.Combine(host.ProjectsRoot, id, fileName);
            using var book = new XLWorkbook(path);
            var sheet = book.Worksheet(1);
            Assert.False(sheet.Row(1).IsEmpty());
            var dateColumn = Assert.Single(sheet.Row(2).CellsUsed(), cell => cell.GetString() == column).Address.ColumnNumber;
            var flagColumn = sheet.Row(2).CellsUsed().SingleOrDefault(cell => cell.GetString() == "IS_Holiday")?.Address.ColumnNumber;
            if (action == "import.holiday.fromFile") Assert.NotNull(flagColumn);
            var dates = sheet.RowsUsed().Where(row => row.RowNumber() > 2)
                .Where(row => !row.Cell(dateColumn).IsEmpty() && (flagColumn is null || row.Cell(flagColumn.Value).GetString().Trim() == "Y"))
                .Select(row => ReadDate(row.Cell(dateColumn))).Distinct().Order().ToArray();
            Assert.NotEmpty(dates);
            expected[action] = dates;
            var imported = await host.DispatchAsync(action, JsonSerializer.Serialize(new { filePath = path }));
            Assert.Equal(dates.Length, imported.GetProperty("count").GetInt32());
        }
        var reopened = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        var calendar = reopened.GetProperty("importState").GetProperty("calendar");
        Assert.Equal(expected["import.holiday.fromFile"].Length, calendar.GetProperty("holidayCount").GetInt32());
        Assert.Equal(expected["import.makeupDay.fromFile"].Length, calendar.GetProperty("makeupDayCount").GetInt32());
        var preview = await host.DispatchAsync("query.dataPreview", """{"dataset":"dateDimension","pageSize":500}""");
        Assert.Contains("2025-01-01", expected["import.holiday.fromFile"]);
        Assert.Contains(preview.GetProperty("rows").EnumerateArray(), row =>
            row[0].GetString() == "2025-01-01" && row[1].GetString() == "holiday");
        var stored = await UsageScenarioMutationRollbackTests.ReadTableAsync(host, id, provider, "staging_calendar_raw_day");
        var rows = stored.Select(row => JsonSerializer.Deserialize<string?[]>(row)!).ToArray();
        Assert.Equal(expected["import.holiday.fromFile"], rows.Where(row => row[0] == "holiday").Select(row => row[1]).Order());
        Assert.Equal(expected["import.makeupDay.fromFile"], rows.Where(row => row[0] == "makeup").Select(row => row[1]).Order());
    }

    private static string ReadDate(IXLCell cell) => cell.DataType == XLDataType.DateTime
        ? cell.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : DateOnly.ParseExact(cell.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
