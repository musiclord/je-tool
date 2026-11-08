using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 底稿與條件篩選報告裡的條件說明，欄位名稱用【】標示、值照舊用「」（裁定 2026-11-03-17）。
/// 用合成案件匯出兩份文件後讀檔，確認【】只包住欄位名稱，值與連接詞都不在【】裡。
/// 畫面的讀回不加【】，由 FilterConditionRendererTests 守住。
/// </summary>
public sealed class DocumentFieldNameMarkupJourneyTests
{
    private const string ExpectedConditionText =
        "【傳票摘要】 包含「調整」 且 【金額】（絕對值） ≥ 1000 且 【總帳日期】 每月幾日屬於「5、31」；空白不列入";

    [Theory]
    [InlineData("sqlite")]
    public async Task ExportedWorkingPaperAndCriteriaReport_MarkOnlyFieldNamesInConditionText(string provider)
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = "FIELD-MARK-001",
            clientName = "合成欄位標示案件",
            operatorId = "tester",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            databaseProvider = provider
        }));
        var projectId = created.GetProperty("projectId").GetString()!;

        var builder = new InlineGlWorkbookBuilder()
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "合成現金", "合成調整", 1500, 1)
            .AddRow("JV-001", "2025-03-05", "4101", "合成收入", "合成調整", 1500, 0);
        var workbookPath = builder.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
            {
                filePath = workbookPath,
                fileName = "合成總帳.xlsx"
            }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(workbookPath);
        }

        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["mapping"] = builder.BuildFlagModeMapping(),
            ["amountMode"] = "flag",
            ["manualAutoPolicy"] = new { manualValues = new[] { "1" }, automaticValues = new[] { "0" } }
        }));
        var validation = await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        var validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString();
        var prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString();
        var saved = await host.DispatchAsync("filter.commit", """
            {
              "scenarios": [
                {
                  "name": "調整分錄",
                  "rationale": "合成案件",
                  "groups": [
                    { "join": "AND", "conditions": [
                      { "join": "AND", "type": "text", "field": "description", "mode": "contains", "keywords": "調整" },
                      { "join": "AND", "type": "numRange", "field": "amount", "from": "1000", "to": "" },
                      { "join": "AND", "type": "fieldValue", "field": "glDate", "operator": "dayOfMonthIn", "values": ["5", "31"] }
                    ] }
                  ]
                }
              ]
            }
            """);
        var revision = saved.GetProperty("resultRef").GetProperty("revision").GetString();

        await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new
        {
            validationResultId,
            prescreenResultId,
            revision
        }));
        await host.DispatchAsync("export.workingPaperStream", JsonSerializer.Serialize(new
        {
            validationResultId,
            prescreenResultId,
            scenarioRevision = revision,
            scenarioPositions = new[] { 1 }
        }));
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        var reports = loaded.GetProperty("reports").EnumerateArray().ToArray();
        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);

        using (var workingPaper = new XLWorkbook(ReportPath(reports, projectDirectory, "workingPaper")))
        {
            var row = workingPaper.Worksheet(WorkingPaperSheetCatalog.Step3).RowsUsed()
                .Single(candidate => candidate.Cell(2).GetString() == "C1");
            var description = row.Cell(3).GetString();
            // 儲存格裡的換行寫成 CRLF，比對前統一成 LF。
            Assert.Equal("調整分錄\n篩選條件：" + ExpectedConditionText, description.Replace("\r\n", "\n", StringComparison.Ordinal));
            AssertMarksOnlyFieldNames(description);
        }

        using (var criteriaReport = new XLWorkbook(ReportPath(reports, projectDirectory, "criteriaSelectionReport")))
        {
            var sheet = criteriaReport.Worksheets.First();
            Assert.Equal("Criteria Selection 1", sheet.Cell("A5").GetString());
            var conditionText = sheet.Cell("B5").GetString();
            Assert.Equal(ExpectedConditionText, conditionText);
            AssertMarksOnlyFieldNames(conditionText);
        }
    }

    private static void AssertMarksOnlyFieldNames(string text)
    {
        var marked = Regex.Matches(text, "【([^】]*)】").Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(new[] { "傳票摘要", "金額", "總帳日期" }, marked);
        Assert.Contains("「調整」", text, StringComparison.Ordinal);
        Assert.Contains("「5、31」", text, StringComparison.Ordinal);
        Assert.DoesNotContain("【1000】", text, StringComparison.Ordinal);
    }

    private static string ReportPath(JsonElement[] reports, string projectDirectory, string kind)
    {
        var fileName = reports.Single(report => report.GetProperty("kind").GetString() == kind)
            .GetProperty("fileName").GetString()!;
        return Path.Combine(projectDirectory, fileName);
    }
}
