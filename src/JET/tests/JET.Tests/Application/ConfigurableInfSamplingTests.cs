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
        using var workingPaper = new XLWorkbook(Path.Combine(host.ProjectsRoot, projectId,
            paper.GetProperty("publishedReport").GetProperty("fileName").GetString()!));
        var step2 = workingPaper.Worksheet(WorkingPaperSheetCatalog.Step2);
        Assert.Equal(count, step2.Range($"B53:B{52 + count}").Cells().Count(cell => !cell.IsEmpty()));
        Assert.False(step2.Cell(52 + count, 2).IsEmpty());
        Assert.Contains(step2.DataValidations.SelectMany(v => v.Ranges), range => range.Contains(step2.Cell(52 + count, 13)));

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
