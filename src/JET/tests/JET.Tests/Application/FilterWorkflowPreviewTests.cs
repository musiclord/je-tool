using System.Text.Json;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class FilterWorkflowPreviewTests
{
    [Theory]
    [InlineData("nameIsAutomatic")]
    [InlineData("rationaleIsAutomatic")]
    public void EditorOrigins_RejectsNonBooleanNamingOwnership(string key)
    {
        var scenario = JsonDocument.Parse("""
          {"name":"Synthetic","rationale":"Synthetic",
           "editorOrigins":{"version":1,"groups":[{"letters":[null]}],"KEY":"true"},
           "groups":[{"rules":[{"type":"drCrOnly","drCr":"debit"}]}]}
          """.Replace("KEY", key)).RootElement;
        var error = Assert.Throws<JetActionException>(() => JET.Application.FilterScenarioPayloadParser.Parse(scenario, 4));
        Assert.Equal(JetErrorCodes.InvalidScenario, error.Code);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task AutomaticAndManualNames_SurviveReopenWithoutChangingHits(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var scenario = JsonDocument.Parse("""
          {"name":"G","rationale":"Synthetic","source":"kct",
           "editorOrigins":{"version":1,"nameIsAutomatic":true,"rationaleIsAutomatic":false,
                            "groups":[{"presetGroup":false,"letters":["G"]}]},
           "groups":[{"rules":[{"type":"prescreen","prescreenKey":"blankDescription"}]}]}
          """).RootElement;
        var saved = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { scenario } }));
        Assert.True(JsonElement.DeepEquals(scenario.GetProperty("editorOrigins"), saved.GetProperty("scenarios")[0].GetProperty("editorOrigins")));
        var withoutOrigins = JsonSerializer.SerializeToElement(new { name = "G", rationale = "Synthetic", source = "kct", groups = scenario.GetProperty("groups") });
        var withPreview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario }));
        var withoutPreview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario = withoutOrigins }));
        // Demo 的期間內摘要空白固定為 18 筆，編輯來源不得參與審計判定。
        Assert.Equal(18, withPreview.GetProperty("scenario").GetProperty("count").GetInt64());
        Assert.Equal(18, withoutPreview.GetProperty("scenario").GetProperty("count").GetInt64());
        await host.DispatchAsync("project.releaseLock");
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = setup.ProjectId }));
        Assert.True(JsonElement.DeepEquals(scenario.GetProperty("editorOrigins"), loaded.GetProperty("filterScenarios")[0].GetProperty("editorOrigins")));
    }

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
