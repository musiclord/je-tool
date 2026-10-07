using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class UsageScenarioProjectBoundaryTests
{
    // S1-01: jet-guide section 2 and frontend description require both audit dates.
    [Theory]
    [InlineData("sqlite", "periodStart")]
    [InlineData("sqlite", "periodEnd")]
    [InlineData("duckdb", "periodStart")]
    [InlineData("duckdb", "periodEnd")]
    public async Task MissingAuditDate_IsRejectedWithoutCreatingProject(string provider, string missing)
    {
        using var host = new HandlerTestHost();
        Directory.CreateDirectory(host.ProjectsRoot);
        var payload = new JsonObject
        {
            ["caseName"] = "Synthetic missing period", ["databaseProvider"] = provider,
            ["periodStart"] = "2025-01-01", ["periodEnd"] = "2025-12-31"
        };
        payload.Remove(missing);
        var error = await Assert.ThrowsAsync<JetActionException>(() =>
            host.DispatchAsync("project.create", payload.ToJsonString()));
        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
        Assert.Contains(missing, error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(host.ProjectsRoot, "Synthetic missing period")));
        Assert.Empty(Directory.GetFiles(host.ProjectsRoot, "project.json", SearchOption.AllDirectories));
    }

    // S1-15: frontend description picker errors and action contract support.log.export.
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task FailedProjectLoad_ExportsSafeSupportLogWithoutOpeningProject(string provider)
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        { caseName = "Synthetic support case", periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider }));
        var id = created.GetProperty("projectId").GetString()!;
        await host.DispatchAsync("project.releaseLock");
        var projectPath = Path.Combine(host.ProjectsRoot, id, "project.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(projectPath))!;
        document["periodStart"] = "synthetic-invalid-date";
        await File.WriteAllTextAsync(projectPath, document.ToJsonString());
        const string correlationId = "synthetic-picker-load-failure";
        await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("project.load",
            JsonSerializer.Serialize(new { projectId = id }), correlationId: correlationId));
        var noProject = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("export.calendarTemplates"));
        Assert.Equal(JetErrorCodes.NoActiveProject, noProject.Code);

        var exported = await host.DispatchAsync("support.log.export", JsonSerializer.Serialize(new { projectId = id, correlationId }));
        var file = exported.GetProperty("filePath").GetString()!;
        Assert.StartsWith(Path.Combine(host.ProjectsRoot, id) + Path.DirectorySeparatorChar, file, StringComparison.OrdinalIgnoreCase);
        var lines = (await File.ReadAllLinesAsync(file)).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        Assert.Equal(exported.GetProperty("lineCount").GetInt32(), lines.Length);
        var events = lines.Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        Assert.Contains(events, row => row.GetProperty("eventName").GetString() == "action.error"
            && row.GetRawText().Contains(correlationId, StringComparison.Ordinal)
            && row.GetRawText().Contains("project.load", StringComparison.Ordinal));
        Assert.All(lines, line =>
        {
            Assert.DoesNotContain(host.ProjectsRoot, line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("synthetic-invalid-date", line, StringComparison.Ordinal);
            Assert.DoesNotContain(id, line, StringComparison.Ordinal);
            Assert.DoesNotContain("parameters", line, StringComparison.OrdinalIgnoreCase);
        });
    }
}
