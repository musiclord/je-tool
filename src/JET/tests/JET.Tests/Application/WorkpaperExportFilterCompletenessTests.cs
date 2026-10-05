using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;
using static JET.Tests.Application.WorkpaperExportTestSupport;

namespace JET.Tests.Application;

/// <summary>WorkingPaper 匯出：篩選條件完整寫入底稿，且長條件不超過 Excel 儲存格上限。</summary>
public sealed class WorkpaperExportFilterCompletenessTests
{
    [Fact]
    public async Task FilterCompleteness_WorkpaperIncludesNewConditions()
    {
        using var host = new HandlerTestHost();
        var project = await DemoProjectPipeline.SetupAsync(host);
        var validation = await host.DispatchAsync("validate.run");
        var validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString();
        var saved = await host.DispatchAsync("filter.commit", """
          {"scenarios":[{"name":"新條件底稿","rationale":"核對條件與輸出","groups":[{"rules":[
            {"type":"fieldValue","field":"postDate","operator":"in","values":["2025-01-15","2025-02-01"],"includeBlank":false},
            {"type":"fieldValue","field":"description","operator":"notContains","value":"TEST","includeBlank":false}
          ]}]}]}
          """);
        var revision = saved.GetProperty("resultRef").GetProperty("revision").GetString();
        await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new { validationRunId, revision }));
        var exported = await host.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(new
        { validationRunId, scenarioRevision = revision, scenarioPositions = new[] { 1 } }));
        using var workbook = new XLWorkbook(Path.Combine(host.ProjectsRoot, project.ProjectId,
            exported.GetProperty("artifact").GetProperty("fileName").GetString()!));
        var text = workbook.Worksheet(WorkpaperSheetCatalog.Step3).Cell("C19").GetString();
        Assert.StartsWith("新條件底稿" + Environment.NewLine + "篩選條件：", text, StringComparison.Ordinal);
        Assert.Contains("2025-01-15", text, StringComparison.Ordinal);
        Assert.Contains("2025-02-01", text, StringComparison.Ordinal);
        Assert.Contains("不包含任何文字「TEST」", text, StringComparison.Ordinal);
        Assert.DoesNotContain("排除區域", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilterCompleteness_LongConditionsRemainCompleteWithinExcelCellLimits()
    {
        using var host = new HandlerTestHost();
        var project = await DemoProjectPipeline.SetupAsync(host);
        var validation = await host.DispatchAsync("validate.run");
        var validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString();
        var value = new string('X', 33_000);
        var expected = "傳票摘要 開頭符合「" + value + "」；空白不列入";
        var scenario = new { name = "Long conditions", rationale = "Synthetic", groups = new[] { new { rules = new[] {
            new { type = "fieldValue", field = "description", @operator = "startsWith", value, includeBlank = false }
        } } } };
        var saved = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { scenario } }));
        var revision = saved.GetProperty("resultRef").GetProperty("revision").GetString();
        var criteria = await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new { validationRunId, revision }));
        var exported = await host.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(new
        { validationRunId, scenarioRevision = revision, scenarioPositions = new[] { 1 } }));
        XLWorkbook Open(JsonElement response) => new(Path.Combine(host.ProjectsRoot, project.ProjectId,
            response.GetProperty("artifact").GetProperty("fileName").GetString()!));
        using var paper = Open(exported);
        var chunks = paper.Worksheet(WorkpaperSheetCatalog.Step3).Column(3).CellsUsed()
            .Where(cell => cell.Address.RowNumber >= 19).Select(cell => cell.GetString()).ToArray();
        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 32_767));
        Assert.Equal("Long conditions\n篩選條件：" + expected, string.Concat(chunks).Replace("\r\n", "\n", StringComparison.Ordinal));
        using var report = Open(criteria);
        var sheet = report.Worksheet("Summary Inforamtion");
        Assert.Contains("完整條件", sheet.Cell("B5").GetString(), StringComparison.Ordinal);
        var reportChunks = sheet.Column(2).CellsUsed().Where(cell => cell.Address.RowNumber >= 16).Select(cell => cell.GetString()).ToArray();
        Assert.All(reportChunks, chunk => Assert.InRange(chunk.Length, 1, 32_767));
        Assert.Equal(expected, string.Concat(reportChunks));
    }
}
