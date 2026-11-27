using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 資料可靠性抽樣清單的「INF Testing 可靠性測試」與底稿「step2 可靠性測試」的 A4，在範本原句後寫出這批樣本實際使用的隨機種子序號。
/// 抽樣之後有人改了案件設定檔的種子、卻沒有重新執行資料驗證時，樣本仍是原本那一批，A4 也寫原本的種子，不寫設定檔裡改過的值。
/// </summary>
public sealed class InfSampleSeedExportTests
{
    private const string SeedSentencePrefix =
        "此Report將提供測試JE母體攸關資料元素可靠性之樣本，本次抽樣所使用之隨機種子序號為:";

    [Fact]
    public async Task InfReportAndInfSampling_WriteTheSeedUsedForTheSamples_EvenAfterProjectJsonIsEdited()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");
                for (var index = 1; index <= 40; index++)
                {
                    gl.AddRow($"JV-{index:000}", "2025-03-03", "1101", "現金", $"借方 {index}", "甲", "100.00", 1);
                    gl.AddRow($"JV-{index:000}", "2025-03-03", "6101", "費用", $"貸方 {index}", "甲", "100.00", 0);
                }
            },
            validateForDownstream: true);
        var validation = await host.DispatchAsync("validate.run");
        var usedSeed = validation.GetProperty("infSamplingTest").GetProperty("seed").GetInt64();
        var resultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        var projectJsonPath = new JetProjectFolder(host.ProjectsRoot).GetProjectJsonPath(projectId);
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(projectJsonPath))!.AsObject();
        Assert.Equal(usedSeed, settings["sampleSeed"]!.GetValue<long>());

        // 驗證之後，案件設定的種子換成另一個合法的值，但沒有重新執行資料驗證。設定檔與案件資料庫一起改，
        // 匯出時讀到的案件設定就是新的種子；A4 仍要寫抽出這批樣本的種子。
        var editedSeed = usedSeed == 1 ? 2 : usedSeed - 1;
        await ProtectedSettingEdits.ApplyAsync(
            host.ProjectsRoot, projectId, document => document with { SampleSeed = editedSeed });

        var reports = await host.DispatchAsync("export.validationReports", JsonSerializer.Serialize(new { resultId }));
        var infReport = reports.GetProperty("publishedReports").EnumerateArray()
            .Single(item => item.GetProperty("kind").GetString() == "infReport");
        var workingPaper = await host.DispatchAsync("export.workingPaperStream", JsonSerializer.Serialize(new
        {
            validationResultId = resultId,
            scenarioPositions = Array.Empty<int>()
        }));

        var expected = SeedSentencePrefix + usedSeed.ToString(CultureInfo.InvariantCulture);
        using (var book = new XLWorkbook(Path.Combine(
                   host.ProjectsRoot,
                   projectId,
                   infReport.GetProperty("fileName").GetString()!)))
        {
            AssertSeedSentence(book.Worksheet("INF Testing 可靠性測試").Cell("A4"), expected);
        }
        using (var book = new XLWorkbook(Path.Combine(
                   host.ProjectsRoot,
                   projectId,
                   workingPaper.GetProperty("publishedReport").GetProperty("fileName").GetString()!)))
        {
            AssertSeedSentence(book.Worksheet(WorkingPaperSheetCatalog.InfSampling).Cell("A4"), expected);
        }
    }

    // 範本的 A4 是斜體、靠左靠上、不換行的文字格；寫進種子之後格式照範本。
    private static void AssertSeedSentence(IXLCell cell, string expected)
    {
        Assert.Equal(XLDataType.Text, cell.DataType);
        Assert.Equal(expected, cell.GetString());
        Assert.True(cell.Style.Font.Italic);
        Assert.False(cell.Style.Alignment.WrapText);
        Assert.Equal(XLAlignmentHorizontalValues.Left, cell.Style.Alignment.Horizontal);
        Assert.Equal(XLAlignmentVerticalValues.Top, cell.Style.Alignment.Vertical);
    }
}
