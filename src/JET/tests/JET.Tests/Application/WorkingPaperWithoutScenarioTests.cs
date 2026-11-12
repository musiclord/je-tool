using System.Text.Json;
using ClosedXML.Excel;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

// 沒有已儲存的情境也能產生工作底稿，第三、四步留空，由審計員在底稿說明；
// 已有情境、但一個都沒勾選時也照樣產生。

/// <summary>
/// 第六步沒有情境，或有情境但一個都沒勾選時，照樣產生工作底稿。第三、四步的工作表照樣存在，
/// 只是沒有情境列與傳票列；其他步驟照常。全部用程式生成的合成資料。
/// </summary>
public sealed class WorkingPaperWithoutScenarioTests
{
    [Theory]
    [InlineData("sqlite")]
    public async Task NoSavedScenario_ProducesWorkingPaperWithEmptyStep3AndStep4(string databaseProvider)
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host, ConfigureGl, databaseProvider: databaseProvider, validateForDownstream: true);
        var validationResultId = await CurrentValidationResultIdAsync(host, projectId);

        var response = await host.DispatchAsync("export.workingPaperStream", JsonSerializer.Serialize(new
        {
            validationResultId,
            scenarioPositions = Array.Empty<int>()
        }));

        var report = response.GetProperty("publishedReport");
        Assert.Equal("workingPaper", report.GetProperty("kind").GetString());
        Assert.False(report.GetProperty("stale").GetBoolean());
        var sourceRef = report.GetProperty("sourceRef");
        Assert.Equal(JsonValueKind.Null, sourceRef.GetProperty("scenarioRevision").ValueKind);
        Assert.Empty(sourceRef.GetProperty("scenarioPositions").EnumerateArray());
        AssertEmptyScenarioSheets(host, projectId, report);

        // 之後儲存了情境，這份沒有情境的底稿就不是目前版本。
        await host.DispatchAsync("filter.commit", ScenarioPayload());
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        var stored = loaded.GetProperty("reports").EnumerateArray()
            .Single(item => item.GetProperty("reportId").GetString() == report.GetProperty("reportId").GetString());
        Assert.True(stored.GetProperty("stale").GetBoolean());
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task SavedScenariosNoneSelected_ProducesWorkingPaperWithEmptyStep3AndStep4(string databaseProvider)
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host, ConfigureGl, databaseProvider: databaseProvider, validateForDownstream: true);
        var validationResultId = await CurrentValidationResultIdAsync(host, projectId);
        var committed = await host.DispatchAsync("filter.commit", ScenarioPayload());
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString();

        var response = await host.DispatchAsync("export.workingPaperStream", JsonSerializer.Serialize(new
        {
            validationResultId,
            scenarioRevision = revision,
            scenarioPositions = Array.Empty<int>()
        }));

        var report = response.GetProperty("publishedReport");
        Assert.False(report.GetProperty("stale").GetBoolean());
        Assert.Equal(revision, report.GetProperty("sourceRef").GetProperty("scenarioRevision").GetString());
        Assert.Empty(report.GetProperty("sourceRef").GetProperty("scenarioPositions").EnumerateArray());
        AssertEmptyScenarioSheets(host, projectId, report);
    }

    private static void AssertEmptyScenarioSheets(HandlerTestHost host, string projectId, JsonElement report)
    {
        using var book = new XLWorkbook(Path.Combine(host.ProjectsRoot, projectId, report.GetProperty("fileName").GetString()!));
        // 第三步：表頭照舊，第 19 列起沒有情境。
        var step3 = book.Worksheet(WorkingPaperSheetCatalog.Step3);
        Assert.Equal("高風險範圍條件", step3.Cell(18, 3).GetString());
        Assert.True(step3.Row(19).IsEmpty());
        // 第四步：表頭照舊，第 13 列起沒有傳票。
        var step4 = book.Worksheet(WorkingPaperSheetCatalog.Step4);
        Assert.False(step4.Row(11).IsEmpty());
        Assert.True(step4.Row(13).IsEmpty());
        Assert.True(book.Worksheets.Contains(WorkingPaperSheetCatalog.Step41));
        // 其他步驟照常。
        Assert.True(book.Worksheets.Contains(WorkingPaperSheetCatalog.Step1));
        Assert.True(book.Worksheets.Contains(WorkingPaperSheetCatalog.Step2));
        Assert.True(book.Worksheets.Contains(WorkingPaperSheetCatalog.Step5));
    }

    private static async Task<string> CurrentValidationResultIdAsync(HandlerTestHost host, string projectId)
    {
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        return loaded.GetProperty("validationResult").GetProperty("resultRef").GetProperty("resultId").GetString()!;
    }

    private static void ConfigureGl(InlineGlWorkbookBuilder builder) =>
        builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金", "合成沒有情境借方", "100.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "收入", "合成沒有情境貸方", "100.00", 0);

    private static string ScenarioPayload() =>
        """
        {"scenarios":[{"name":"合成借方分錄","rationale":"合成沒有勾選情境","groups":[{"conditions":[{"type":"drCrOnly","drCr":"debit"}]}]}]}
        """;
}
