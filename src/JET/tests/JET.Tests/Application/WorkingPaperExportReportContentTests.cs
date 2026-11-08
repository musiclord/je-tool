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
// 使用者要求封面 A3 留白；首次失敗收據 20261106-130302916-209cba23338f4b12a0fd28bcaf09480b。
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
            string.Empty,
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

    // 裁定 2026-11-05-31：底稿的週末表照案件設定的每週休息日寫 Y，不再固定寫星期六、星期日。
    // 裁定 2026-11-05-59：Step 3 的 B7、B9 寫回舊版原文，JET 原本寫的查核期間與排除範圍另起一行接在後面。
    [Fact]
    public async Task Export_WeekendTableFollowsProjectNonWorkingDays_AndStep3WritesLegacyScopeText()
    {
        using var host = new HandlerTestHost();
        var project = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("calendar.setNonWorkingDays", """{ "days": [5, 0] }""");
        var validation = await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        var filter = await host.DispatchAsync("filter.commit", ScenarioPayload());
        var prepared = new Prepared(
            project.ProjectId,
            validation.GetProperty("resultRef").GetProperty("resultId").GetString()!,
            prescreen.GetProperty("resultRef").GetProperty("resultId").GetString()!,
            filter.GetProperty("resultRef").GetProperty("revision").GetString()!,
            [1, 2]);
        await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new
            {
                validationResultId = prepared.ValidationResultId,
                revision = prepared.Revision
            }));

        var response = await host.DispatchAsync("export.workingPaperStream", Payload(prepared));
        var path = Path.Combine(
            host.ProjectsRoot,
            prepared.ProjectId,
            response.GetProperty("publishedReport").GetProperty("fileName").GetString()!);

        using var workbook = new XLWorkbook(path);
        var calendar = workbook.Worksheet(WorkingPaperSheetCatalog.CalendarInfo);
        Assert.Equal(
            new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" },
            Enumerable.Range(2, 7).Select(row => calendar.Cell(row, 1).GetString()).ToArray());
        Assert.Equal(
            new[] { "N", "N", "N", "N", "Y", "N", "Y" },
            Enumerable.Range(2, 7).Select(row => calendar.Cell(row, 2).GetString()).ToArray());

        var demoProject = project.Demo.GetProperty("project");
        var period = $"{demoProject.GetProperty("periodStart").GetString()} ~ {demoProject.GetProperty("periodEnd").GetString()}";
        var step3 = workbook.Worksheet(WorkingPaperSheetCatalog.Step3);
        // ClosedXML 在 Windows 讀回時把儲存格裡的換行改成 \r\n，比對前統一成底稿實際寫的 \n。
        Assert.Equal(
            "期末財務報表準備期間與整個財務報導期間之會計分錄\n" + $"查核期間內（{period}）之會計分錄",
            step3.Cell("B7").GetString().ReplaceLineEndings("\n"));
        Assert.Equal(
            "查核團隊考量舞弊風險相關之分錄，除了會於期末財務報表準備期間發生外，某些與舞弊風險因子相關之分錄類型(例如：收入)"
            + "亦會在整個財務報導期間中被記錄。這些都將會對財務報表的正確性造成影響。因此，除測試期末財務報表準備期間之分錄外，"
            + " 亦將對整個財務報表期間之會計分錄母體，篩選與舞弊風險類型相關之分錄。\n"
            + "本次高風險條件以查核期間內的會計分錄為母體；查核期間外與【總帳日期】空白的分錄，不列入本版各情境的結果與彙總。",
            step3.Cell("B9").GetString().ReplaceLineEndings("\n"));
    }
}
