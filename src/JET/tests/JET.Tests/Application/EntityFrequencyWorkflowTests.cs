using System.Text.Json;
using ClosedXML.Excel;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class EntityFrequencyWorkflowTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Frequency_UsesSelectedPopulationAndDistinctVouchersThenSurvivesSaveReopenAndExport(string provider)
    {
        using var host = new HandlerTestHost();
        var id = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標", "建立人員", "核准人員")
            .AddRow("JV-1", "2025-03-01", "1000", "合成 A", "row1", "10", 1, "U1", "V1")
            .AddRow("JV-1", "2025-03-01", "1000", "合成 A", "row2", "10", 0, "U1", "V2")
            .AddRow("JV-2", "2025-03-01", "1000", "合成 A", "row3", "10", 1, "U2", "V1")
            .AddRow("JV-2", "2025-03-01", "2000", "合成 B", "row4", "10", 0, "U2", "V1")
            .AddRow(null, "2025-03-01", "1000", "合成 A", "row5", "10", 1, "U1", "V1")
            .AddRow("JV-3", "2025-03-01", "2000", "合成 B", "row6", "10", 0, "U3", "V2")
            .AddRow(null, "2025-03-01", "3000", "合成 C", "row7", "10", 1, null, null)
            .AddRow("OUT", "2026-03-01", "1000", "合成 A", "outside", "10", 1, "U1", "V1"),
            databaseProvider: provider, validateForDownstream: true);
        object Scenario(string field, string unit, string op, int from, int? to = null) => new
        {
            name = "合成統計", rationale = "固定答案", groups = new[] { new { conditions = new[] { new
            { type = "entityFrequency", field, countUnit = unit, countOperator = op, countFrom = from, countTo = to } } } }
        };
        async Task Check(string field, string unit, string op, int from, long expected, int? to = null)
        {
            var preview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario = Scenario(field, unit, op, from, to) }));
            Assert.Equal(expected, preview.GetProperty("scenario").GetProperty("count").GetInt64());
        }
        await Check("accountNumber", "entries", "equals", 4, 4);
        await Check("accountNumber", "entries", "lessThan", 4, 3);
        await Check("accountNumber", "entries", "greaterThan", 2, 4);
        await Check("accountNumber", "entries", "between", 2, 6, 4);
        await Check("accountNumber", "vouchers", "equals", 2, 6);
        await Check("accountNumber", "vouchers", "equals", 0, 1);
        await Check("createdBy", "entries", "equals", 3, 3);
        await Check("createdBy", "vouchers", "equals", 1, 6);
        await Check("approvedBy", "entries", "greaterThan", 3, 4);
        await Check("approvedBy", "vouchers", "equals", 2, 6);
        var scenario = Scenario("accountNumber", "vouchers", "between", 1, 2);
        var commit = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { scenario } }));
        var revision = commit.GetProperty("resultRef").GetProperty("revision").GetString();
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        Assert.Equal("entityFrequency", loaded.GetProperty("filterScenarios")[0].GetProperty("groups")[0].GetProperty("conditions")[0].GetProperty("type").GetString());
        var hits = await host.DispatchAsync("query.filterHitsPage", JsonSerializer.Serialize(new { scenarioPosition = 1, scenarioRevision = revision, pageSize = 50 }));
        Assert.Equal(6, hits.GetProperty("rows").GetArrayLength());
        var report = await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new
        { validationResultId = loaded.GetProperty("validationResult").GetProperty("resultRef").GetProperty("resultId").GetString(), revision }));
        Assert.True(File.Exists(report.GetProperty("publishedReport").GetProperty("fullPath").GetString()));
        using (var workbook = new XLWorkbook(report.GetProperty("publishedReport").GetProperty("fullPath").GetString()!))
        {
            Assert.Contains(workbook.Worksheets.Where(sheet => sheet.Visibility == XLWorksheetVisibility.Visible)
                .SelectMany(sheet => sheet.CellsUsed()).Select(cell => cell.GetString()),
                // 用語統一後，條件讀回跟著畫面改（第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a）。
                // 裁定 2026-10-08-04 統一全系統的資料名稱：「分錄測試範圍內」改為「GL 中」。
                text => text.Contains("GL 中「會計科目編號」傳票張數（同號只算一張） 介於 1～2（含端點）", StringComparison.Ordinal));
        }
        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new
        { scenario = Scenario("description", "vouchers", "equals", 1) })));
        Assert.Equal(JetErrorCodes.InvalidScenario, error.Code);
        await Check("accountNumber", "vouchers", "equals", 2, 6);
    }
}
