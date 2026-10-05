using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch7ProjectDateOptionWireTests
{
    [Theory]
    [InlineData("sqlite", true, "project.load")]
    [InlineData("sqlite", false, "project.load")]
    [InlineData("duckdb", true, "project.load")]
    [InlineData("duckdb", false, "project.load")]
    [InlineData("sqlite", true, "project.update")]
    [InlineData("sqlite", false, "project.update")]
    [InlineData("duckdb", true, "project.update")]
    [InlineData("duckdb", false, "project.update")]
    public async Task ProjectResponse_PreservesTheExistingRocDateOption(string provider, bool rocEnabled, string action)
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = "B7-DATE", entityName = "合成日期選項", operatorId = "tester",
            periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider
        }));
        var id = created.GetProperty("projectId").GetString()!;
        var store = new JsonFileProjectStore(new JetProjectFolder(host.ProjectsRoot));
        var original = (await store.FindAsync(id, CancellationToken.None))!;
        await store.SaveAsync(original with { RocDateEnabled = rocEnabled }, CancellationToken.None);

        var response = await host.DispatchAsync(action, JsonSerializer.Serialize(new
        {
            projectId = id, entityName = "合成客戶改名"
        }));
        var project = response.GetProperty("project");
        Assert.True(project.TryGetProperty("rocDateEnabled", out var option), "The actual project response must carry rocDateEnabled, including false.");
        Assert.Equal(rocEnabled, option.GetBoolean());
        var saved = (await store.FindAsync(id, CancellationToken.None))!;
        Assert.Equal(rocEnabled, saved.RocDateEnabled);
        if (action == "project.update") Assert.Equal("合成客戶改名", saved.EntityName);
    }
}
