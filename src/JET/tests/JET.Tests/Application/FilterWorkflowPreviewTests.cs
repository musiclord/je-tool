using System.Text.Json;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class FilterWorkflowPreviewTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task EditorOrigins_SurviveSaveAndReopen(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var scenario = JsonDocument.Parse("""
          {"name":"Origins","rationale":"Synthetic","source":"kct",
           "editorOrigins":{"version":1,"groups":[{"presetGroup":false,"letters":["G",null]}]},
           "groups":[{"rules":[{"type":"prescreen","prescreenKey":"blankDescription"},{"join":"OR","type":"drCrOnly","drCr":"debit"}]}]}
          """).RootElement;
        var saved = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { scenario } }));
        Assert.Equal(scenario.GetProperty("editorOrigins").GetRawText(), saved.GetProperty("scenarios")[0].GetProperty("editorOrigins").GetRawText());
        var invalid = JsonDocument.Parse("""{"name":"","groups":[{"rules":[{"type":"drCrOnly","drCr":"debit"}]}]}""").RootElement;
        var failedSave = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("filter.commit",
            JsonSerializer.Serialize(new { scenarios = new[] { invalid } })));
        Assert.Equal(JetErrorCodes.InvalidScenario, failedSave.Code);
        await host.DispatchAsync("project.releaseLock");
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = setup.ProjectId }));
        Assert.Equal(scenario.GetProperty("editorOrigins").GetRawText(), loaded.GetProperty("filterScenarios")[0].GetProperty("editorOrigins").GetRawText());
        Assert.Equal("Origins", loaded.GetProperty("filterScenarios")[0].GetProperty("name").GetString());
        var page = await host.DispatchAsync("query.filterVoucherPage", JsonSerializer.Serialize(new { scenario, pageSize = 1 }));
        Assert.NotNull(page.GetProperty("nextCursor").GetString());
        Assert.False(page.TryGetProperty("resultKind", out _));
    }

    [Fact]
    public async Task UnnamedDraft_CanPreviewAndReadButCannotReplaceSavedScenarios()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var scenario = JsonSerializer.SerializeToElement(new { groups = new[] { new { rules = new[] { new { type = "drCrOnly", drCr = "debit" } } } } });
        var preview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario }));
        Assert.True(preview.GetProperty("scenario").GetProperty("count").GetInt64() > 0);
        Assert.False(preview.GetProperty("scenario").TryGetProperty("pendingVoucherCount", out _));
        var page = await host.DispatchAsync("query.filterVoucherPage", JsonSerializer.Serialize(new { scenario }));
        Assert.NotEmpty(page.GetProperty("rows").EnumerateArray());
        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { scenario } })));
        Assert.Equal(JetErrorCodes.InvalidScenario, error.Code);
    }
}
