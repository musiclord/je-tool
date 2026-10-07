using System.Text.Json;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch8PreparationDateReadbackTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ActualPreparationDate_ReachesSummaryDetailsCriteriaAndWorkpaperWithoutEnteringTheAst(string provider)
    {
        using var host = new HandlerTestHost();
        var id = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("BEFORE", "2025-06-01", "2025-06-14", "1000", "合成", "before", 10, 1)
            .AddRow("BOUNDARY", "2025-06-01", "2025-06-15", "1000", "合成", "boundary", 10, 1)
            .AddRow("AFTER", "2025-06-01", "2026-01-01", "1000", "合成", "after", 10, 1),
            lastPeriodStart: "2025-06-15", databaseProvider: provider, validateForDownstream: true);
        var dated = Fixtures().Single(item => item.GetProperty("id").GetString() == "preparation-date-mid-year");
        var expected = dated.GetProperty("expected").GetString()!;
        var scenario = WithMetadata(dated.GetProperty("scenario"));
        var preview = (await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario }))).GetProperty("scenario");
        Assert.Equal(2, preview.GetProperty("count").GetInt64());
        Assert.Equal(["AFTER", "BOUNDARY"], Documents(preview.GetProperty("previewRows")));
        var page = await host.DispatchAsync("query.filterVoucherPage", JsonSerializer.Serialize(new { scenario }));
        Assert.Equal(expected, page.GetProperty("conditionText").GetString());
        Assert.Equal(["AFTER", "BOUNDARY"], Documents(page.GetProperty("rows")));
        var details = await host.DispatchAsync("query.filterVoucherRowsPage", JsonSerializer.Serialize(new
        {
            scenario, documentNumber = "BOUNDARY", queryRevision = page.GetProperty("queryRevision").GetString()
        }));
        Assert.Contains(expected, Assert.Single(details.GetProperty("rows").EnumerateArray()).GetProperty("matchDescription").GetString(), StringComparison.Ordinal);

        var saved = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { scenario } }));
        var revision = saved.GetProperty("resultRef").GetProperty("revision").GetString();
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        Assert.True(JsonElement.DeepEquals(scenario.GetProperty("groups"), loaded.GetProperty("filterScenarios")[0].GetProperty("groups")));
        var runId = loaded.GetProperty("latestRuns").GetProperty("validate").GetProperty("resultRef").GetProperty("runId").GetString();
        var criteria = await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new { validationRunId = runId, revision }));
        AssertWorkbookContains(criteria, expected);
        var paper = await host.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(new
        {
            validationRunId = runId, scenarioRevision = revision, scenarioPositions = new[] { 1 }
        }));
        AssertWorkbookContains(paper, expected);
        var paperPath = paper.GetProperty("artifact").GetProperty("fullPath").GetString()!;
        Assert.InRange(new FileInfo(paperPath).Length, 1, 10_000_000);
        var oldPaper = File.ReadAllBytes(paperPath);

        await host.DispatchAsync("project.update", """{"lastPeriodStart":"2025-12-31"}""");
        // 使用者 2026-10-07 裁定上游修改清除下游：改財報準備日會清掉已存情境，審計員重新儲存後才以新日期讀回。
        // 原本直接用舊版本讀回保留的情境，第一次失敗收據 20261007-032952783-7147565fec1c42be845dac22acf7263b。
        var cleared = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        Assert.Empty(cleared.GetProperty("filterScenarios").EnumerateArray());
        Assert.Equal(oldPaper, File.ReadAllBytes(paperPath));
        var resaved = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { scenario } }));
        var next = Fixtures().Single(item => item.GetProperty("id").GetString() == "preparation-date-year-end");
        var newPage = await host.DispatchAsync("query.filterVoucherPage", JsonSerializer.Serialize(new
        {
            scenarioPosition = 1, scenarioRevision = resaved.GetProperty("resultRef").GetProperty("revision").GetString()
        }));
        Assert.Equal(next.GetProperty("expected").GetString(), newPage.GetProperty("conditionText").GetString());
        Assert.Equal(["AFTER"], Documents(newPage.GetProperty("rows")));
        Assert.Equal(oldPaper, File.ReadAllBytes(paperPath));
        var reloaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        Assert.True(JsonElement.DeepEquals(scenario.GetProperty("groups"), reloaded.GetProperty("filterScenarios")[0].GetProperty("groups")));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task PreparationDate_PropagatesThroughNestedGroupAndVoucherReadback(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("B", "2025-06-01", "2025-06-15", "1000", "合成", "boundary", 10, 1),
            lastPeriodStart: "2025-06-15", databaseProvider: provider);
        var scenario = JsonElement.Parse("""
            {"name":"合成巢狀日期","rationale":"固定全文","groups":[{"rules":[
                {"type":"voucher","side":"all","quantifier":"any","rules":[
                    {"type":"group","rules":[{"type":"prescreen","prescreenKey":"postPeriodApproval"}]}
                ]}
            ]}]}
            """);
        var page = await host.DispatchAsync("query.filterVoucherPage", JsonSerializer.Serialize(new { scenario }));
        Assert.Equal("整張傳票至少一筆符合：同一分錄（預篩選：財報準備日起核准（2025-06-15 起，含當日））", page.GetProperty("conditionText").GetString());
        Assert.Equal(["B"], Documents(page.GetProperty("rows")));
    }

    [Fact]
    public void MissingPreparationContext_DoesNotInventADateInTheReadback()
    {
        var fixture = Fixtures().Single(item => item.GetProperty("preparationDate").ValueKind == JsonValueKind.Null);
        Assert.Equal(fixture.GetProperty("expected").GetString(), FilterConditionRenderer.Render(fixture.GetProperty("scenario")));
    }

    private static JsonElement WithMetadata(JsonElement scenario) => JsonSerializer.SerializeToElement(new
    {
        name = "合成核准日期", rationale = "固定日期與全文", groups = scenario.GetProperty("groups")
    });

    private static string?[] Documents(JsonElement rows) => rows.EnumerateArray()
        .Select(row => row.GetProperty("documentNumber").GetString()).Order(StringComparer.Ordinal).ToArray();

    private static void AssertWorkbookContains(JsonElement response, string expected)
    {
        var path = response.GetProperty("artifact").GetProperty("fullPath").GetString()!;
        using var book = new XLWorkbook(path);
        Assert.Contains(book.Worksheets.Where(sheet => sheet.Visibility == XLWorksheetVisibility.Visible)
            .SelectMany(sheet => sheet.CellsUsed()).Select(cell => cell.GetString()),
            text => text.Contains(expected, StringComparison.Ordinal));
    }

    private static IReadOnlyList<JsonElement> Fixtures()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tools", "tests", "fixtures", "batch8-filter-readback.json")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Repository fixture directory was not found.");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "tools", "tests", "fixtures", "batch8-filter-readback.json")));
        return json.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
    }
}
