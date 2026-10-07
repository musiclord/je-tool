using System.Text.Json;
using ClosedXML.Excel;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class IndependentWorkpaperWorkflowTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task AccountMappingTemplate_UsesMappedAccountsWithoutAValidationRun(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, runValidation: false, databaseProvider: provider);
        var result = await host.DispatchAsync("export.accountMappingTemplate", """{"onlyIfMissing":true}""");
        Assert.Equal("created", result.GetProperty("disposition").GetString());
        Assert.Equal(150, result.GetProperty("rowCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("validationRunId").ValueKind);
        using var book = new XLWorkbook(result.GetProperty("filePath").GetString()!);
        Assert.Equal("Standardized Account Name*", book.Worksheet("AccountMapping").Cell(3, 3).GetString());
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = setup.ProjectId }));
        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("latestRuns").GetProperty("validate").ValueKind);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Workpaper_ExportsAfterCalendarChangeAndResave_WithoutPrescreenOrCriteriaReport(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, runValidation: false, databaseProvider: provider);
        var validation = await host.DispatchAsync("validate.run");
        const string scenarios = """
            {"scenarios":[{"name":"借方分錄","rationale":"合成流程核對","groups":[{"rules":[{"type":"drCrOnly","drCr":"debit"}]}]}]}
            """;
        await host.DispatchAsync("filter.commit", scenarios);
        await host.DispatchAsync("calendar.setNonWorkingDays", """{"days":[6]}""");
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = setup.ProjectId }));
        // 使用者 2026-10-07 裁定上游修改清除下游：行事曆修改清掉已存情境，篩選回到從未執行；
        // 審計員重新儲存後即可直接匯出底稿。原本斷言情境保留且標成待重跑，第一次失敗收據
        // 20261007-032952783-7147565fec1c42be845dac22acf7263b。
        Assert.False(loaded.GetProperty("staleState").GetProperty("filter").GetBoolean());
        Assert.Empty(loaded.GetProperty("filterScenarios").EnumerateArray());
        Assert.False(loaded.GetProperty("staleState").GetProperty("validation").GetBoolean());
        var saved = await host.DispatchAsync("filter.commit", scenarios);
        var response = await host.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(new
        {
            validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString(),
            scenarioRevision = saved.GetProperty("resultRef").GetProperty("revision").GetString(),
            scenarioPositions = new[] { 1 }
        }));
        var artifact = response.GetProperty("artifact");
        Assert.Equal("workingPaper", artifact.GetProperty("kind").GetString());
        Assert.False(artifact.GetProperty("stale").GetBoolean());
        var folder = Path.Combine(host.ProjectsRoot, setup.ProjectId);
        Assert.Empty(Directory.GetFiles(folder, "*CriteriaSelectionReport*.xlsx"));
        Assert.Empty(Directory.GetFiles(folder, "*PrescreeningReport*.xlsx"));
        using var book = new XLWorkbook(Path.Combine(folder, artifact.GetProperty("fileName").GetString()!));
        Assert.True(book.Worksheet(WorkpaperSheetCatalog.Step41).RowsUsed().Count() > 1);
        var after = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = setup.ProjectId }));
        Assert.False(after.GetProperty("staleState").GetProperty("filter").GetBoolean());
        Assert.Single(after.GetProperty("filterScenarios").EnumerateArray());
    }
}
