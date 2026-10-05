using System.Text.Json;
using ClosedXML.Excel;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch9DuplicateSourceTests
{
    public static TheoryData<string, string, bool, bool, bool, int> Cases => new()
    {
        { "sqlite", "append", true, true, true, 1 }, { "duckdb", "append", true, true, true, 1 },
        { "sqlite", "append", false, true, true, 0 }, { "duckdb", "append", false, true, true, 0 },
        { "sqlite", "append", true, false, true, 0 }, { "duckdb", "append", true, false, true, 0 },
        { "sqlite", "append", true, true, false, 0 }, { "duckdb", "append", true, true, false, 0 },
        { "sqlite", "replace", true, true, true, 0 }, { "duckdb", "replace", true, true, true, 0 }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Append_UsesActualFileSheetAndRowCounts_WarnsButStillCommits(string provider, string mode,
        bool sameName, bool sameSheet, bool sameRows, int warnings)
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        { caseName = "batch9-duplicates", databaseProvider = provider, periodStart = "2025-01-01", periodEnd = "2025-12-31" }));
        var path = Path.Combine(host.ProjectsRoot, "synthetic-duplicates.xlsx");
        WriteWorkbook(path, 1);
        await host.DispatchAsync("import.gl.fromFile", Payload(path, "replace", "same.xlsx", "First"));
        WriteWorkbook(path, sameRows ? 1 : 2);
        var result = await host.DispatchAsync("import.gl.fromFile", Payload(path, mode,
            sameName ? "same.xlsx" : "different.xlsx", sameSheet ? "First" : "Second"));
        var actual = result.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal(warnings, actual.Length);
        if (warnings == 1)
        {
            Assert.Contains("可能已匯入過", actual[0]);
            Assert.Contains("same.xlsx", actual[0]);
            Assert.Contains("First", actual[0]);
            Assert.DoesNotContain(host.ProjectsRoot, actual[0]);
        }
        Assert.Equal(sameRows ? 1 : 2, result.GetProperty("addedRowCount").GetInt32());
        Assert.Equal((mode == "append" ? 1 : 0) + (sameRows ? 1 : 2), result.GetProperty("rowCount").GetInt32());
        Assert.Equal(mode == "append" ? 2 : 1, result.GetProperty("sources").GetArrayLength());
    }

    private static string Payload(string path, string mode, string fileName, string sheetName) => JsonSerializer.Serialize(new
    { mode, sources = new[] { new { filePath = path, fileName, sheetName } } });

    private static void WriteWorkbook(string path, int rows)
    {
        using var workbook = new XLWorkbook();
        foreach (var name in new[] { "First", "Second" })
        {
            var sheet = workbook.AddWorksheet(name);
            sheet.Cell(1, 1).Value = "SyntheticColumn";
            for (var row = 1; row <= rows; row++) sheet.Cell(row + 1, 1).Value = row;
        }
        workbook.SaveAs(path);
    }
}
