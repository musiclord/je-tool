using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;
using static JET.Tests.Application.WorkingPaperExportTestSupport;

namespace JET.Tests.Application;

/// <summary>WorkingPaper 匯出：案件資料夾內的報告檔、固定工作表與情境欄位。</summary>
public sealed class WorkingPaperExportReportContentTests
{
    [Fact]
    public async Task Export_WritesProjectLocalReport_WithoutAbsolutePath_AndFixedSheets()
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);

        var response = await host.DispatchAsync("export.workingPaperStream", Payload(prepared));
        var report = response.GetProperty("publishedReport");
        Assert.False(report.TryGetProperty("outputPath", out _));
        Assert.False(response.TryGetProperty("outputPath", out _));
        Assert.Equal("workingPaper", report.GetProperty("kind").GetString());

        var fileName = report.GetProperty("fileName").GetString()!;
        Assert.Equal(fileName, Path.GetFileName(fileName));
        var path = Path.Combine(host.ProjectsRoot, prepared.ProjectId, fileName);
        Assert.True(File.Exists(path));

        using var workbook = new XLWorkbook(path);
        var sheetNames = workbook.Worksheets.Select(sheet => sheet.Name).ToArray();
        var emittedLegacySheetNames = sheetNames
            .Where(name => !string.Equals(
                name,
                ReportWorkbookMetadataFormat.WorksheetName,
                StringComparison.Ordinal))
            .ToArray();
        var canonicalEmittedOrder = WorkingPaperSheetCatalog.All
            .Where(emittedLegacySheetNames.Contains)
            .ToArray();
        var responseSheetNames = response.GetProperty("sheetStats")
            .EnumerateArray()
            .Select(item => item.GetProperty("sheetName").GetString()!)
            .ToArray();
        Assert.Equal(canonicalEmittedOrder, emittedLegacySheetNames);
        Assert.Equal(
            ReportWorkbookMetadataFormat.WorksheetName,
            Assert.Single(sheetNames.Skip(emittedLegacySheetNames.Length)));
        Assert.Equal(
            XLWorksheetVisibility.VeryHidden,
            workbook.Worksheet(ReportWorkbookMetadataFormat.WorksheetName).Visibility);
        Assert.Equal(emittedLegacySheetNames, responseSheetNames);
        Assert.DoesNotContain("step1-3-1完整性差異調節", sheetNames);
        Assert.Equal(WorkingPaperSheetCatalog.Cover, workbook.Worksheet(1).Name);
        Assert.Equal(
            "篩選測試母體 : 查核期間",
            workbook.Worksheet(WorkingPaperSheetCatalog.Cover).Cell("A3").GetString());
        Assert.Contains(
            "查核期間內",
            workbook.Worksheet(WorkingPaperSheetCatalog.Step3).Cell("B7").GetString(),
            StringComparison.Ordinal);
        foreach (var required in new[]
        {
            WorkingPaperSheetCatalog.Intro,
            WorkingPaperSheetCatalog.Step1,
            WorkingPaperSheetCatalog.Step11,
            WorkingPaperSheetCatalog.Step12,
            WorkingPaperSheetCatalog.Step2,
            WorkingPaperSheetCatalog.Step3,
            WorkingPaperSheetCatalog.Step4,
            WorkingPaperSheetCatalog.Step41,
            WorkingPaperSheetCatalog.Step5,
            WorkingPaperSheetCatalog.FieldInfo,
            WorkingPaperSheetCatalog.CalendarInfo,
            WorkingPaperSheetCatalog.AccountMapping
        })
        {
            Assert.True(workbook.TryGetWorksheet(required, out _), required);
        }
    }

    [Fact]
    public async Task Export_SelectedScenario_ChangesScenarioColumns_NotWorksheetSet()
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);
        var response = await host.DispatchAsync("export.workingPaperStream", Payload(prepared, [2]));
        var path = Path.Combine(
            host.ProjectsRoot,
            prepared.ProjectId,
            response.GetProperty("publishedReport").GetProperty("fileName").GetString()!);

        using var workbook = new XLWorkbook(path);
        var step3 = workbook.Worksheet(WorkingPaperSheetCatalog.Step3);
        Assert.Equal("C2", step3.Cell("B19").GetString());
        Assert.True(step3.Cell("B20").IsEmpty());
        Assert.All(
            step3.Range("F18:F28").Cells(),
            cell => Assert.True(cell.IsEmpty()));
        var step4 = workbook.Worksheet(WorkingPaperSheetCatalog.Step4);
        Assert.Equal("C1", step4.Cell("F11").GetString());
        Assert.Equal("C2", step4.Cell("G11").GetString());
        Assert.Equal("C10", step4.Cell("O11").GetString());
        Assert.True(workbook.TryGetWorksheet(WorkingPaperSheetCatalog.Step1, out _));
        Assert.True(workbook.TryGetWorksheet(WorkingPaperSheetCatalog.Step5, out _));
    }
}
