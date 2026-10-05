using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class ImportCalendarHandlersTests
{
    private const string CreatePayload =
        """
        {
          "projectCode": "CAL-001",
          "entityName": "假日測試公司",
          "operatorId": "dev",
          "periodStart": "2025-01-01",
          "periodEnd": "2025-12-31"
        }
        """;

    [Fact]
    public async Task WithoutActiveProject_ThrowsNoActiveProject()
    {
        using var host = new HandlerTestHost();

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("import.holiday", """{ "dates": ["2025-01-01"] }"""));

        Assert.Equal(JetErrorCodes.NoActiveProject, ex.Code);
    }

    [Fact]
    public async Task InvalidDateFormat_ThrowsInvalidPayloadNamingTheValue()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("import.holiday", """{ "dates": ["2025/01/01"] }"""));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
        Assert.Contains("2025/01/01", ex.Message);
    }

    [Fact]
    public async Task ImportTwice_ReplacesPriorSet()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        await host.DispatchAsync(
            "import.holiday",
            """{ "dates": ["2025-01-01", "2025-02-28", "2025-10-10"] }""");

        var second = await host.DispatchAsync(
            "import.holiday",
            """{ "dates": ["2025-05-01"] }""");

        Assert.Equal(1, second.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task DuplicateDates_AreStoredOnce()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var result = await host.DispatchAsync(
            "import.makeupDay",
            """{ "dates": ["2025-02-08", "2025-02-08"] }""");

        Assert.Equal(1, result.GetProperty("count").GetInt32());
    }

    private static string WriteHolidayWorkbook()
    {
        return TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 2).Value = "Holiday Table";                 // 第 1 列樣式標題
            ws.Cell(2, 1).Value = "Date_of_Holiday";               // 第 2 列標頭
            ws.Cell(2, 2).Value = "Holiday_Name";
            ws.Cell(2, 3).Value = "IS_Holiday";
            ws.Cell(3, 1).Value = new DateTime(2024, 1, 1);        // 跨年度(2024)
            ws.Cell(3, 2).Value = "元旦";
            ws.Cell(3, 3).Value = "Y";
            ws.Cell(4, 1).Value = new DateTime(2025, 2, 28);
            ws.Cell(4, 2).Value = "228 紀念日";
            ws.Cell(4, 3).Value = "Y";
            ws.Cell(5, 1).Value = new DateTime(2025, 3, 1);        // IS_Holiday=N → 略過
            ws.Cell(5, 2).Value = "非假日";
            ws.Cell(5, 3).Value = "N";
        });
    }

    [Fact]
    public async Task ImportHolidayFromFile_KeepsYRowsAcrossYears_SkipsN()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var path = WriteHolidayWorkbook();
        try
        {
            var data = await host.DispatchAsync(
                "import.holiday.fromFile", JsonSerializer.Serialize(new { filePath = path }));

            // 2 筆 Y(跨 2024/2025);N 列略過。
            Assert.Equal(2, data.GetProperty("count").GetInt32());
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ImportHolidayFromFile_ValidZeroRows_PersistsSuccessfulImportMarker()
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", CreatePayload);
        var projectId = created.GetProperty("projectId").GetString()!;
        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 2).Value = "Holiday Table";
            ws.Cell(2, 1).Value = "Date_of_Holiday";
            ws.Cell(2, 2).Value = "Holiday_Name";
            ws.Cell(2, 3).Value = "IS_Holiday";
            ws.Cell(3, 1).Value = new DateTime(2025, 1, 1);
            ws.Cell(3, 2).Value = "非假日";
            ws.Cell(3, 3).Value = "N";
        });

        try
        {
            var imported = await host.DispatchAsync(
                "import.holiday.fromFile",
                JsonSerializer.Serialize(new { filePath = path }));
            Assert.Equal(0, imported.GetProperty("count").GetInt32());

            var loaded = await host.DispatchAsync(
                "project.load",
                JsonSerializer.Serialize(new { projectId }));
            var calendar = loaded.GetProperty("importState").GetProperty("calendar");
            Assert.Equal(0, calendar.GetProperty("holidayCount").GetInt32());
            Assert.Equal(0, calendar.GetProperty("makeupDayCount").GetInt32());
            Assert.True(calendar.GetProperty("calendarImported").GetBoolean());
            Assert.False(calendar.GetProperty("nonWorkingDaysConfigured").GetBoolean());
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ImportHolidayFromFile_NonXlsx_ThrowsUnsupportedFileType()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var path = Path.Combine(Path.GetTempPath(), "jet-cal-tests", Guid.NewGuid().ToString("N") + ".csv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "Date_of_Holiday,Holiday_Name,IS_Holiday\n2025-01-01,元旦,Y\n");

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "import.holiday.fromFile", JsonSerializer.Serialize(new { filePath = path })));

        Assert.Equal(JetErrorCodes.UnsupportedFileType, ex.Code);
    }

    [Fact]
    public async Task ImportHolidayFromFile_MissingDateColumn_ThrowsProjectionFailed()
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", CreatePayload);
        var projectId = created.GetProperty("projectId").GetString()!;

        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 2).Value = "Holiday Table";
            ws.Cell(2, 1).Value = "不是日期欄";
            ws.Cell(2, 2).Value = "Holiday_Name";
            ws.Cell(3, 1).Value = "x";
            ws.Cell(3, 2).Value = "元旦";
        });

        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
                "import.holiday.fromFile", JsonSerializer.Serialize(new { filePath = path })));

            Assert.Equal(JetErrorCodes.ProjectionFailed, ex.Code);
            Assert.Contains("Date_of_Holiday", ex.Message);

            var loaded = await host.DispatchAsync(
                "project.load",
                JsonSerializer.Serialize(new { projectId }));
            Assert.False(
                loaded.GetProperty("importState").GetProperty("calendar")
                    .GetProperty("calendarImported").GetBoolean());
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ImportHolidayFromFile_BadDateRow_ThrowsProjectionFailedNamingValue()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 2).Value = "Holiday Table";
            ws.Cell(2, 1).Value = "Date_of_Holiday";
            ws.Cell(2, 2).Value = "Holiday_Name";
            ws.Cell(2, 3).Value = "IS_Holiday";
            ws.Cell(3, 1).Value = "2025/01/01";   // 文字、非 yyyy-MM-dd
            ws.Cell(3, 2).Value = "元旦";
            ws.Cell(3, 3).Value = "Y";
        });

        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
                "import.holiday.fromFile", JsonSerializer.Serialize(new { filePath = path })));

            Assert.Equal(JetErrorCodes.ProjectionFailed, ex.Code);
            Assert.Equal(
                "行事曆檔有 1 列無法解析:第 3 列:日期「2025/01/01」非 yyyy-MM-dd 格式。",
                ex.Message);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ImportMakeupDayFromFile_LoadsDescAsName()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 2).Value = "補班日/結帳日Table";
            ws.Cell(2, 1).Value = "Date_of_MakeUpday";
            ws.Cell(2, 2).Value = "MakeUpDay_Desc";
            ws.Cell(3, 1).Value = new DateTime(2025, 2, 8);
            ws.Cell(3, 2).Value = "春節補班";
        });

        try
        {
            var data = await host.DispatchAsync(
                "import.makeupDay.fromFile", JsonSerializer.Serialize(new { filePath = path }));

            Assert.Equal(1, data.GetProperty("count").GetInt32());
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ImportHolidayFromFile_MissingFile_ThrowsFileNotFound()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);
        var path = Path.Combine(Path.GetTempPath(), "jet-cal-tests", Guid.NewGuid().ToString("N") + ".xlsx");

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "import.holiday.fromFile", JsonSerializer.Serialize(new { filePath = path })));

        Assert.Equal(JetErrorCodes.FileNotFound, ex.Code);
        // 第9批中低12；Public首敗105344715：錯誤只顯示檔名，保留FileNotFound並新增完整路徑不得外洩的斷言。
        Assert.Contains(Path.GetFileName(path), ex.Message);
        Assert.DoesNotContain(path, ex.Message);
    }

}
