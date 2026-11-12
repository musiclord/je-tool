using System.Text.Json;
using ClosedXML.Excel;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class IndependentWorkingPaperWorkflowTests
{
    [Theory]
    [InlineData("sqlite")]
    public async Task AccountMappingTemplate_UsesMappedAccountsWithoutAValidationResult(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, runValidation: false, databaseProvider: provider);
        var result = await host.DispatchAsync("export.accountMappingTemplate", """{"onlyIfMissing":true}""");
        Assert.Equal("created", result.GetProperty("disposition").GetString());
        Assert.Equal(150, result.GetProperty("rowCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("validationResultId").ValueKind);
        using var book = new XLWorkbook(result.GetProperty("filePath").GetString()!);
        Assert.Equal("Standardized Account Name*", book.Worksheet("AccountMapping").Cell(3, 3).GetString());
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = setup.ProjectId }));
        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("validationResult").ValueKind);
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task WorkingPaper_ExportsAfterCalendarChangeAndResave_WithoutPrescreenOrCriteriaReport(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, runValidation: false, databaseProvider: provider);
        var validation = await host.DispatchAsync("validate.run");
        const string scenarios = """
            {"scenarios":[{"name":"借方分錄","rationale":"合成流程核對","groups":[{"conditions":[{"type":"drCrOnly","drCr":"debit"}]}]}]}
            """;
        await host.DispatchAsync("filter.commit", scenarios);
        await host.DispatchAsync("calendar.setNonWorkingDays", """{"days":[6]}""");
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = setup.ProjectId }));
        // 上游修改會清除下游：行事曆修改清掉已存情境，篩選回到從未執行；
        // 審計員重新儲存後即可直接匯出底稿。
        Assert.False(loaded.GetProperty("staleState").GetProperty("filter").GetBoolean());
        Assert.Empty(loaded.GetProperty("filterScenarios").EnumerateArray());
        Assert.False(loaded.GetProperty("staleState").GetProperty("validation").GetBoolean());
        var saved = await host.DispatchAsync("filter.commit", scenarios);
        var response = await host.DispatchAsync("export.workingPaperStream", JsonSerializer.Serialize(new
        {
            validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString(),
            scenarioRevision = saved.GetProperty("resultRef").GetProperty("revision").GetString(),
            scenarioPositions = new[] { 1 }
        }));
        var report = response.GetProperty("publishedReport");
        Assert.Equal("workingPaper", report.GetProperty("kind").GetString());
        Assert.False(report.GetProperty("stale").GetBoolean());
        var folder = Path.Combine(host.ProjectsRoot, setup.ProjectId);
        Assert.Empty(Directory.GetFiles(folder, "*CriteriaSelectionReport*.xlsx"));
        Assert.Empty(Directory.GetFiles(folder, "*PrescreeningReport*.xlsx"));
        using var book = new XLWorkbook(Path.Combine(folder, report.GetProperty("fileName").GetString()!));
        Assert.True(book.Worksheet(WorkingPaperSheetCatalog.Step41).RowsUsed().Count() > 1);
        var after = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = setup.ProjectId }));
        Assert.False(after.GetProperty("staleState").GetProperty("filter").GetBoolean());
        Assert.Single(after.GetProperty("filterScenarios").EnumerateArray());
    }
}
