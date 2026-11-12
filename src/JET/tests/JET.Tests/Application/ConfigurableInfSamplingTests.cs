using System.Text.Json;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class ConfigurableInfSamplingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(120)]
    public async Task RequestedCount_IsStoredAndExportedWithoutFiftyNineRowTruncation(int count)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        var projectId = context.ProjectId;
        var validation = await host.DispatchAsync("validate.run", JsonSerializer.Serialize(new { sampleSize = count }));
        var sample = validation.GetProperty("infSamplingTest");
        Assert.Equal(count, sample.GetProperty("sampleSize").GetInt32());
        Assert.Equal(count, sample.GetProperty("requestedSampleSize").GetInt32());
        var resultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString();
        var repeated = await host.DispatchAsync("validate.run", JsonSerializer.Serialize(new { sampleSize = count }));
        Assert.Equal(sample.GetProperty("seed").GetInt64(), repeated.GetProperty("infSamplingTest").GetProperty("seed").GetInt64());
        resultId = repeated.GetProperty("resultRef").GetProperty("resultId").GetString();
        var exports = await host.DispatchAsync("export.validationReports", JsonSerializer.Serialize(new { resultId }));
        var report = exports.GetProperty("publishedReports").EnumerateArray()
            .Single(item => item.GetProperty("kind").GetString() == "infReport");
        using var book = new XLWorkbook(Path.Combine(host.ProjectsRoot, projectId, report.GetProperty("fileName").GetString()!));
        var main = book.Worksheet("INF Testing 可靠性測試");
        Assert.Equal(count, main.Range($"B53:B{52 + count}").Cells().Count(cell => !cell.IsEmpty()));
        Assert.False(main.Cell(52 + count, 2).IsEmpty());
        Assert.Equal(count, book.Worksheet("可靠性樣本_所有欄位").RowsUsed().Count() - 1);
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(count, loaded.GetProperty("validationResult").GetProperty("infSamplingTest").GetProperty("requestedSampleSize").GetInt32());
        var paper = await host.DispatchAsync("export.workingPaperStream", JsonSerializer.Serialize(new
        {
            validationResultId = resultId,
            scenarioPositions = Array.Empty<int>()
        }));
        var workingPaperPath = Path.Combine(host.ProjectsRoot, projectId,
            paper.GetProperty("publishedReport").GetProperty("fileName").GetString()!);
        using (var workingPaper = new XLWorkbook(workingPaperPath))
        {
            var step2 = workingPaper.Worksheet(WorkingPaperSheetCatalog.Step2);
            Assert.Equal(count, step2.Range($"B53:B{52 + count}").Cells().Count(cell => !cell.IsEmpty()));
            Assert.False(step2.Cell(52 + count, 2).IsEmpty());
            Assert.Contains(step2.DataValidations.SelectMany(v => v.Ranges), range => range.Contains(step2.Cell(52 + count, 13)));
        }

        // step2 的下拉選單照範本的兩組選項。樣本超過範本第 111 列時，
        // M 到 S 的範圍延伸到最後一個樣本；B 欄的選單和範本相同。step2 照舊版不保護。
        using var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(workingPaperPath, false);
        var workbookPart = document.WorkbookPart!;
        var sheet = workbookPart.Workbook!.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>()
            .Single(item => item.Name?.Value == WorkingPaperSheetCatalog.Step2);
        var worksheet = ((DocumentFormat.OpenXml.Packaging.WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!)).Worksheet!;
        Assert.Empty(worksheet.Elements<DocumentFormat.OpenXml.Spreadsheet.SheetProtection>());
        Assert.Equal(
            new (string?, string?)[]
            {
                ($"M53:S{Math.Max(111, 52 + count)}", "\"相符,不符合,N/A\""),
                ("B40:B42 B20:B25 B27:B32 B36:B38 B15", "\"V,NA\"")
            },
            worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.DataValidation>()
                .Select(validation => (validation.SequenceOfReferences?.InnerText, validation.Formula1?.Text)));
    }

    [Fact]
    public async Task RequestedCountAboveFiftyNine_ExtendsInfReportDropdownsToLastSample()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        var validation = await host.DispatchAsync("validate.run", JsonSerializer.Serialize(new { sampleSize = 120 }));
        Assert.Equal(120, validation.GetProperty("infSamplingTest").GetProperty("sampleSize").GetInt32());
        var resultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString();
        var exports = await host.DispatchAsync("export.validationReports", JsonSerializer.Serialize(new { resultId }));
        var report = exports.GetProperty("publishedReports").EnumerateArray()
            .Single(item => item.GetProperty("kind").GetString() == "infReport");
        using var book = new XLWorkbook(Path.Combine(host.ProjectsRoot, context.ProjectId, report.GetProperty("fileName").GetString()!));
        var main = book.Worksheet("INF Testing 可靠性測試");
        // 範本 INFReport.xlsx 的下拉選單是 M53:S111，只容納預設的 59 筆。樣本從第 53 列開始，
        // 抽 120 筆時最後一個樣本在第 172 列，下拉選單要延伸到這一列，下一列不再有選單。
        Assert.Single(main.DataValidations.GetAllInRange(main.Cell("M172").AsRange().RangeAddress));
        Assert.Single(main.DataValidations.GetAllInRange(main.Cell("S172").AsRange().RangeAddress));
        Assert.Empty(main.DataValidations.GetAllInRange(main.Cell("S173").AsRange().RangeAddress));
    }

    [Fact]
    public async Task RequestedCountAbovePopulation_SelectsEveryEligibleEntry()
    {
        using var host = new HandlerTestHost();
        // 固定兩列期間內、一列期間外，直接核對全部可抽樣分錄，避免只驗證筆數小於要求值。
        await InlineWorkbookProject.SetupAsync(host, gl => gl
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標")
            .AddRow("S1", "2025-03-01", "1101", "合成科目一", "合成分錄", "甲", 100, 1)
            .AddRow("S1", "2025-03-01", "6101", "合成科目二", "合成分錄", "甲", 100, 0)
            .AddRow("S0", "2024-12-31", "1101", "合成科目一", "期間外", "甲", 100, 1));
        var result = await host.DispatchAsync("validate.run", "{\"sampleSize\":1000000}");
        var sample = result.GetProperty("infSamplingTest");
        Assert.Equal(1000000, sample.GetProperty("requestedSampleSize").GetInt32());
        Assert.Equal(2, sample.GetProperty("sampleSize").GetInt32());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("null")]
    [InlineData("\"59\"")]
    [InlineData("1048525")]
    public async Task InvalidCount_IsRejected(string value)
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var error = await Assert.ThrowsAsync<JetActionException>(() =>
            host.DispatchAsync("validate.run", "{\"sampleSize\":" + value + "}"));
        Assert.Equal("invalid_payload", error.Code);
        Assert.Equal("sampleSize", error.Field);
    }

    [Fact]
    public async Task MissingCount_DefaultsToFiftyNine()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var result = await host.DispatchAsync("validate.run");
        Assert.Equal(59, result.GetProperty("infSamplingTest").GetProperty("requestedSampleSize").GetInt32());
        Assert.Equal(59, result.GetProperty("infSamplingTest").GetProperty("sampleSize").GetInt32());
    }
}
