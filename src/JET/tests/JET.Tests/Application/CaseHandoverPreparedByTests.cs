using System.Text.Json;
using ClosedXML.Excel;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class CaseHandoverPreparedByTests
{
    // jet-guide section 7: an existing Prepared by field uses the saved creator,
    // not the current Windows account. This does not add a field to the working paper template.
    [Theory]
    [InlineData("sqlite")]
    public async Task AnotherAccountExports_ReportsKeepCreatorAndCaseIdentityIsUnchanged(string provider)
    {
        using var root = new TempProjectRoot();
        string id;
        using (var creator = new HandlerTestHost(principalName: "creator.a", projectsRootPath: root.Path))
        {
            // 案件的建立者要是 creator.a，所以在這個 host 上從頭建案，不複製共用案件。
            // 改用複製時建立者是共用案件的 host，第一次失敗的收據是 20261015-095055763-6299ea60373b40c2b96792da2286182d。
            var prepared = await ReportRevisionStateTests.PrepareAsync(creator, provider, fromScratch: true);
            id = prepared.Id;
            await creator.DispatchAsync("project.releaseLock");
        }
        using var auditor = new HandlerTestHost(principalName: "auditor.b", projectsRootPath: root.Path);
        Assert.Equal("auditor.b", auditor.Principal);
        var loaded = await auditor.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        Assert.Equal("creator.a", loaded.GetProperty("project").GetProperty("operatorId").GetString());
        var validation = await auditor.DispatchAsync("validate.run");
        var validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString();
        var prescreen = await auditor.DispatchAsync("prescreen.run");
        var filtered = await auditor.DispatchAsync("filter.commit", """{"scenarios":[{"name":"合成借方","rationale":"固定答案","groups":[{"conditions":[{"type":"drCrOnly","drCr":"debit"}]}]}]}""");
        var revision = filtered.GetProperty("resultRef").GetProperty("revision").GetString();
        var validationExport = await auditor.DispatchAsync("export.validationReports", JsonSerializer.Serialize(new { resultId = validationResultId }));
        var validationReport = validationExport.GetProperty("reports").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "validationReport");
        AssertPreparedBy(root.Path, id, validationReport, "ValidationReport", "E4");
        var prescreenExport = await auditor.DispatchAsync("export.prescreenReport", JsonSerializer.Serialize(new
            { resultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString() }));
        AssertPreparedBy(root.Path, id, prescreenExport.GetProperty("publishedReport"), "Pre-screening_Report", "E4");
        var criteriaExport = await auditor.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new { validationResultId, revision }));
        AssertPreparedBy(root.Path, id, criteriaExport.GetProperty("publishedReport"), "Summary Inforamtion", "D1");
        var workingPaper = await auditor.DispatchAsync("export.workingPaperStream", JsonSerializer.Serialize(new
            { validationResultId, scenarioRevision = revision, scenarioPositions = new[] { 1 } }));
        Assert.False(workingPaper.GetProperty("publishedReport").GetProperty("stale").GetBoolean());
        Assert.True(File.Exists(workingPaper.GetProperty("publishedReport").GetProperty("fullPath").GetString()));
        var reopened = await auditor.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        Assert.Equal("creator.a", reopened.GetProperty("project").GetProperty("operatorId").GetString());
    }

    private static void AssertPreparedBy(string root, string id, JsonElement report, string sheet, string cell)
    {
        using var book = new XLWorkbook(Path.Combine(root, id, report.GetProperty("fileName").GetString()!));
        Assert.Equal("Prepared by: creator.a", book.Worksheet(sheet).Cell(cell).GetString());
        Assert.DoesNotContain(book.Worksheets.SelectMany(worksheet => worksheet.CellsUsed()).Select(value => value.GetString()),
            value => value.Contains("auditor.b", StringComparison.Ordinal));
    }
}
