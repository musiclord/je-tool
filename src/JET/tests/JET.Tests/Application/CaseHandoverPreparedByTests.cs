using System.Text.Json;
using ClosedXML.Excel;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class CaseHandoverPreparedByTests
{
    // jet-guide section 7: an existing Prepared by field uses the saved creator,
    // not the current Windows account. This does not add a field to the workpaper template.
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task AnotherAccountExports_ReportsKeepCreatorAndCaseIdentityIsUnchanged(string provider)
    {
        using var root = new TempProjectRoot();
        string id;
        using (var creator = new HandlerTestHost(principalName: "creator.a", projectsRootPath: root.Path))
        {
            var prepared = await Batch9ArtifactStateTests.PrepareAsync(creator, provider);
            id = prepared.Id;
            await creator.DispatchAsync("project.releaseLock");
        }
        using var auditor = new HandlerTestHost(principalName: "auditor.b", projectsRootPath: root.Path);
        Assert.Equal("auditor.b", auditor.Principal);
        var loaded = await auditor.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        Assert.Equal("creator.a", loaded.GetProperty("project").GetProperty("operatorId").GetString());
        var validation = await auditor.DispatchAsync("validate.run");
        var validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString();
        var prescreen = await auditor.DispatchAsync("prescreen.run");
        var filtered = await auditor.DispatchAsync("filter.commit", """{"scenarios":[{"name":"合成借方","rationale":"固定答案","groups":[{"rules":[{"type":"drCrOnly","drCr":"debit"}]}]}]}""");
        var revision = filtered.GetProperty("resultRef").GetProperty("revision").GetString();
        var validationExport = await auditor.DispatchAsync("export.validationArtifacts", JsonSerializer.Serialize(new { runId = validationRunId }));
        var validationArtifact = validationExport.GetProperty("reportArtifacts").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "validationReport");
        AssertPreparedBy(root.Path, id, validationArtifact, "ValidationReport", "E4");
        var prescreenExport = await auditor.DispatchAsync("export.prescreenReport", JsonSerializer.Serialize(new
            { runId = prescreen.GetProperty("resultRef").GetProperty("runId").GetString() }));
        AssertPreparedBy(root.Path, id, prescreenExport.GetProperty("artifact"), "Pre-screening_Report", "E4");
        var criteriaExport = await auditor.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new { validationRunId, revision }));
        AssertPreparedBy(root.Path, id, criteriaExport.GetProperty("artifact"), "Summary Inforamtion", "D1");
        var workpaper = await auditor.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(new
            { validationRunId, scenarioRevision = revision, scenarioPositions = new[] { 1 } }));
        Assert.False(workpaper.GetProperty("artifact").GetProperty("stale").GetBoolean());
        Assert.True(File.Exists(workpaper.GetProperty("artifact").GetProperty("fullPath").GetString()));
        var reopened = await auditor.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        Assert.Equal("creator.a", reopened.GetProperty("project").GetProperty("operatorId").GetString());
    }

    private static void AssertPreparedBy(string root, string id, JsonElement artifact, string sheet, string cell)
    {
        using var book = new XLWorkbook(Path.Combine(root, id, artifact.GetProperty("fileName").GetString()!));
        Assert.Equal("Prepared by: creator.a", book.Worksheet(sheet).Cell(cell).GetString());
        Assert.DoesNotContain(book.Worksheets.SelectMany(worksheet => worksheet.CellsUsed()).Select(value => value.GetString()),
            value => value.Contains("auditor.b", StringComparison.Ordinal));
    }
}
