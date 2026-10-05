using JET.Domain;
using JET.Tests.Architecture;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch9DeletePreviewActionContractTests
{
    [Fact]
    public void Preview_IsConcurrentAndDoesNotRetainTheActiveProjectDatabase()
    {
        Assert.True(ActionExecutionPolicy.IsClassified("project.deletePreview"));
        Assert.False(ActionExecutionPolicy.IsExclusive("project.deletePreview"));
        Assert.False(ActionExecutionPolicy.RequiresConditionalGate("project.deletePreview"));
        Assert.False(ActionExecutionPolicy.RetainsProjectDatabase("project.deletePreview"));
    }

    [Fact]
    public async Task Preview_RegisteredActionHasExactCountsShapeAndDoesNotChangeTheActiveCase()
    {
        using var host = new HandlerTestHost();
        var first = await host.DispatchAsync("project.create", """
            {"caseName":"delete-preview-a","entityName":"Synthetic A","operatorId":"synthetic",
             "projectCode":"SYNTHETIC-A","periodStart":"2025-01-01","periodEnd":"2025-12-31"}
            """);
        await host.DispatchAsync("project.create", """
            {"caseName":"delete-preview-b","entityName":"Synthetic B","operatorId":"synthetic",
             "projectCode":"SYNTHETIC-B","periodStart":"2025-01-01","periodEnd":"2025-12-31"}
            """);
        var result = await host.DispatchAsync("project.deletePreview", "{\"projectId\":\"delete-preview-a\"}");
        JsonShape.HasExactKeys(result, "projectId", "databaseProvider", "reportCount", "workpaperCount");
        Assert.Equal(first.GetProperty("projectId").GetString(), result.GetProperty("projectId").GetString());
        JsonShape.Str(result, "databaseProvider");
        Assert.Equal("sqlite", result.GetProperty("databaseProvider").GetString());
        Assert.Equal(0, result.GetProperty("reportCount").GetInt32());
        Assert.Equal(0, result.GetProperty("workpaperCount").GetInt32());
        // A session-scoped update after the preview must still affect B, never the previewed A.
        var update = await host.DispatchAsync("project.update", "{\"entityName\":\"Synthetic B updated\"}");
        Assert.Equal("delete-preview-b", update.GetProperty("project").GetProperty("projectId").GetString());
        Assert.True(Directory.Exists(Path.Combine(host.ProjectsRoot, "delete-preview-a")));
        Assert.True(Directory.Exists(Path.Combine(host.ProjectsRoot, "delete-preview-b")));
    }
}
