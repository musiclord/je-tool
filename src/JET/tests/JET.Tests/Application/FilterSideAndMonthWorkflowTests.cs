using System.Text.Json;
using ClosedXML.Excel;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class FilterSideAndMonthWorkflowTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task PreviewSaveRetryReopenAndReportsPreserveSideAndIndependentDates(string provider)
    {
        using var host = new HandlerTestHost();
        var id = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票項次", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("V1", "1", "2025-01-30", "2025-02-01", "A", "合成 A", "Synthetic", 10, 1)
            .AddRow("V1", "2", "2025-01-30", "2025-02-01", "B", "合成 B", "Synthetic", 20, 1)
            .AddRow("V1", "3", "2025-01-30", "2025-02-01", "X", "合成 X", "Synthetic", 30, 0)
            .AddRow("V2", "1", "2025-01-31", "2025-02-02", "A", "合成 A", "Synthetic", 10, 1)
            .AddRow("V2", "2", "2025-01-31", "2025-02-02", "B", "合成 B", "Synthetic", 10, 0)
            .AddRow("V3", "1", "2025-02-27", "2025-02-28", "B", "合成 B", "Synthetic", 10, 1)
            .AddRow("V3", "2", "2025-02-27", "2025-02-28", "A", "合成 A", "Synthetic", 10, 0)
            .AddRow("V4", "1", "2025-02-28", "2025-03-01", "A", "合成 A", "Synthetic", 10, 1)
            .AddRow("V4", "2", "2025-02-28", "2025-03-01", "B", "合成 B", "Synthetic", 8, 0)
            .AddRow("V4", "3", "2025-02-28", "2025-03-01", "X", "合成 X", "Synthetic", 2, 0),
            databaseProvider: provider, validateForDownstream: true);
        var pair = JsonElement.Parse("""
            {"name":"指定借貸科目","rationale":"固定答案","groups":[{"matchScope":"sameVoucher","rules":[
            {"type":"fieldValue","field":"accNum","operator":"in","values":["A"],"drCr":"debit"},
            {"type":"fieldValue","field":"accNum","operator":"in","values":["B"],"drCr":"credit"}]}]}
            """);
        var dates = JsonElement.Parse("""
            {"name":"月底排除核准月初","rationale":"兩個日期各自判斷","groups":[{"rules":[
            {"type":"fieldValue","field":"postDate","operator":"monthEndDays","value":"2"},
            {"type":"fieldValue","field":"docDate","operator":"notMonthStartDays","value":"2"}]}]}
            """);
        var pairPreview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario = pair }));
        Assert.Equal(2, pairPreview.GetProperty("scenario").GetProperty("count").GetInt64());
        var datePreview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario = dates }));
        Assert.Equal(2, datePreview.GetProperty("scenario").GetProperty("count").GetInt64());
        var page = await host.DispatchAsync("query.filterVoucherPage", JsonSerializer.Serialize(new { scenario = pair }));
        Assert.Equal(["V2", "V4"], page.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("documentNumber").GetString()).Order());
        var detail = await host.DispatchAsync("query.filterVoucherRowsPage", JsonSerializer.Serialize(new
        { scenario = pair, documentNumber = "V4", queryRevision = page.GetProperty("queryRevision").GetString() }));
        var rows = detail.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Single(rows, row => row.GetProperty("isHit").GetBoolean());
        Assert.Contains("借方分錄", page.GetProperty("conditionText").GetString());
        Assert.Contains("貸方分錄", page.GetProperty("conditionText").GetString());
        var committed = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { pair, dates } }));
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString();
        // A failed replacement must not publish a partial definition or erase the saved result.
        var invalid = JsonElement.Parse(dates.GetRawText().Replace("\"value\":\"2\"", "\"value\":\"32\"", StringComparison.Ordinal));
        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("filter.commit",
            JsonSerializer.Serialize(new { scenarios = new[] { pair, invalid } })));
        Assert.Equal(JetErrorCodes.InvalidScenario, error.Code);
        await host.DispatchAsync("project.releaseLock");
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        var saved = loaded.GetProperty("filterScenarios");
        Assert.Equal("debit", saved[0].GetProperty("groups")[0].GetProperty("rules")[0].GetProperty("drCr").GetString());
        Assert.Equal("credit", saved[0].GetProperty("groups")[0].GetProperty("rules")[1].GetProperty("drCr").GetString());
        Assert.Equal("notMonthStartDays", saved[1].GetProperty("groups")[0].GetProperty("rules")[1].GetProperty("operator").GetString());
        Assert.Equal("2", saved[1].GetProperty("groups")[0].GetProperty("rules")[0].GetProperty("value").GetString());
        foreach (var item in new[] { (Position: 1, Expected: new[] { "V2|1", "V4|1" }), (Position: 2, Expected: new[] { "V3|1", "V3|2" }) })
        {
            var hits = await host.DispatchAsync("query.filterHitsPage", JsonSerializer.Serialize(new
            { scenarioPosition = item.Position, scenarioRevision = revision, pageSize = 50 }));
            Assert.Equal(item.Expected, hits.GetProperty("rows").EnumerateArray().Select(row =>
                row.GetProperty("documentNumber").GetString() + "|" + row.GetProperty("lineItem").GetString()).Order());
        }
        // Retry the corrected definition through the same action, then export the resulting revision.
        committed = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { pair, dates } }));
        revision = committed.GetProperty("resultRef").GetProperty("revision").GetString();
        var validationRunId = loaded.GetProperty("latestRuns").GetProperty("validate").GetProperty("resultRef").GetProperty("runId").GetString();
        var report = await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new { validationRunId, revision }));
        await host.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(new { validationRunId, scenarioRevision = revision, scenarioPositions = new[] { 1, 2 } }));
        var workpaper = Assert.Single(Directory.GetFiles(Path.Combine(host.ProjectsRoot, id), "*WorkingPaper*.xlsx"));
        foreach (var path in new[] { report.GetProperty("artifact").GetProperty("fullPath").GetString()!, workpaper })
        {
            using var workbook = new XLWorkbook(path);
            var text = string.Join("\n", workbook.Worksheets.Where(sheet => sheet.Visibility == XLWorksheetVisibility.Visible)
                .SelectMany(sheet => sheet.CellsUsed()).Select(cell => cell.GetString()));
            Assert.Contains("借方分錄：", text);
            Assert.Contains("貸方分錄：", text);
            Assert.Contains("每月月底天數「2」 天", text);
            Assert.Contains("排除每月月初天數「2」 天", text);
        }
    }
}
