using System.Text.Json;
using ClosedXML.Excel;
using Xunit;

namespace JET.Tests.Application;

/// <summary>人工驗收第 1、8 項：實際工作簿編輯、重開、GL 重匯與重新產生報告，最後刪除合成案件。</summary>
public sealed class FilterAcceptanceReportLifecycleTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task EditedWorkpaper_ReopensThenReimportRebuildsWithoutLosingVersions(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var directory = Path.Combine(host.ProjectsRoot, setup.ProjectId);
        async Task<string> PrepareExport()
        {
            var validation = await host.DispatchAsync("validate.run");
            var validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString();
            var prescreen = await host.DispatchAsync("prescreen.run");
            var prescreenRunId = prescreen.GetProperty("resultRef").GetProperty("runId").GetString();
            var committed = await host.DispatchAsync("filter.commit", """
                {"scenarios":[{"name":"合成版本驗收","rationale":"固定操作旅程","groups":[{"rules":[
                {"type":"customKeywords","keywords":"調整"}]}]}]}
                """);
            var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString();
            await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new { validationRunId, prescreenRunId, revision }));
            return JsonSerializer.Serialize(new { validationRunId, prescreenRunId, scenarioRevision = revision, scenarioPositions = new[] { 1 } });
        }

        var payload = await PrepareExport();
        await host.DispatchAsync("export.workpaperStream", payload);
        var firstPath = Assert.Single(Directory.GetFiles(directory, "*WorkingPaper*.xlsx"));
        await host.DispatchAsync("export.workpaperStream", payload);
        Assert.Equal(2, Directory.GetFiles(directory, "*WorkingPaper*.xlsx").Length);
        using (var workbook = new XLWorkbook(firstPath))
        {
            workbook.Worksheet(1).Cell(1, 1).Value = "Synthetic auditor edit";
            workbook.Save();
        }
        var editedBytes = await File.ReadAllBytesAsync(firstPath);
        await host.DispatchAsync("project.releaseLock");
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = setup.ProjectId }));
        Assert.Equal(2, loaded.GetProperty("reportArtifacts").EnumerateArray().Count(item => item.GetProperty("kind").GetString() == "workingPaper"));

        var gl = await DemoProjectPipeline.DispatchDemoFixtureAsync("demo.exportGlFile");
        await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
        {
            filePath = gl.GetProperty("filePath").GetString(), fileName = gl.GetProperty("fileName").GetString()
        }));
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(setup.Demo.GetProperty("gl").GetProperty("mapping").GetRawText()),
            amountMode = setup.Demo.GetProperty("gl").GetProperty("amountMode").GetString()
        }));
        var changed = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = setup.ProjectId }));
        var oldVersions = changed.GetProperty("reportArtifacts").EnumerateArray()
            .Where(item => item.GetProperty("kind").GetString() == "workingPaper").ToArray();
        Assert.Equal(2, oldVersions.Length);
        Assert.All(oldVersions, item => Assert.True(item.GetProperty("stale").GetBoolean()));
        var freshPayload = await PrepareExport();
        await host.DispatchAsync("export.workpaperStream", freshPayload);
        Assert.Equal(3, Directory.GetFiles(directory, "*WorkingPaper*.xlsx").Length);
        Assert.Equal(editedBytes, await File.ReadAllBytesAsync(firstPath));

        await host.DispatchAsync("project.releaseLock");
        await host.DispatchAsync("project.delete", JsonSerializer.Serialize(new { projectId = setup.ProjectId }));
        Assert.False(Directory.Exists(directory));
    }
}
