using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 前端說明「案件選擇」及 action 契約「日誌與主機整合」：兩種診斷匯出在案件載入失敗時仍可使用，
/// 目的地只能是合法 projectId 的既有非連結目錄，不以 project.json 能否解析為前提。
/// </summary>
public sealed class SupportLogUnavailableProjectTests
{
    public static IEnumerable<object[]> UnavailableCases() =>
        from provider in new[] { "sqlite", "duckdb" }
        from damage in new[] { "invalid-json", "invalid-metadata", "missing-metadata" }
        from export in new[] { (Action: "support.log.export", Sink: false), (Action: "dev.log.exportFile", Sink: false), (Action: "dev.log.exportFile", Sink: true) }
        select new object[] { provider, damage, export.Action, export.Sink };

    [Theory]
    [MemberData(nameof(UnavailableCases))]
    public async Task UnavailableProjectMetadata_DoesNotPreventDiagnosticExportOrChangeTheCase(
        string provider, string damage, string action, bool withSink)
    {
        using var host = new HandlerTestHost(enableDevTools: action == "dev.log.exportFile", withDiagnosticLogFile: withSink);
        var id = await CreateAsync(host, provider, "Synthetic damaged case");
        await host.DispatchAsync("project.releaseLock");
        var folder = Path.Combine(host.ProjectsRoot, id);
        var metadata = Path.Combine(folder, "project.json");
        if (damage == "invalid-json") await File.WriteAllTextAsync(metadata, "{synthetic-invalid-json");
        else if (damage == "invalid-metadata")
        {
            var document = JsonNode.Parse(await File.ReadAllTextAsync(metadata))!;
            document["periodStart"] = "synthetic-invalid-period";
            await File.WriteAllTextAsync(metadata, document.ToJsonString());
        }
        else File.Delete(metadata);
        var metadataBefore = File.Exists(metadata) ? await File.ReadAllBytesAsync(metadata) : null;

        const string selectedCorrelation = "selected-load-error";
        await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("project.load",
            JsonSerializer.Serialize(new { projectId = id }), correlationId: selectedCorrelation));
        await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("project.load",
            """{"projectId":"Different missing case"}""", correlationId: "unrelated-load-error"));
        var noActive = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("export.calendarTemplates"));
        Assert.Equal(JetErrorCodes.NoActiveProject, noActive.Code);

        var exported = await host.DispatchAsync(action, JsonSerializer.Serialize(new { projectId = id, correlationId = selectedCorrelation }));
        var file = exported.GetProperty("filePath").GetString()!;
        Assert.Equal(Path.GetFullPath(folder), Path.GetDirectoryName(Path.GetFullPath(file)));
        Assert.StartsWith(action == "support.log.export" ? "JET-support-" : "JET-dev-log-", Path.GetFileName(file), StringComparison.Ordinal);
        Assert.EndsWith(".txt", file, StringComparison.Ordinal);
        var lines = (await File.ReadAllLinesAsync(file)).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        Assert.Equal(exported.GetProperty("lineCount").GetInt32(), lines.Length);
        var entries = lines.Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        Assert.Contains(entries, entry => entry.GetProperty("eventName").GetString() == "action.error"
            && entry.GetRawText().Contains(selectedCorrelation, StringComparison.Ordinal)
            && entry.GetRawText().Contains("project.load", StringComparison.Ordinal));
        Assert.All(lines, line => Assert.DoesNotContain("unrelated-load-error", line, StringComparison.Ordinal));
        if (action == "support.log.export")
        {
            Assert.Equal("correlation", exported.GetProperty("scope").GetString());
            Assert.All(lines, line =>
            {
                Assert.DoesNotContain(host.ProjectsRoot, line, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(id, line, StringComparison.Ordinal);
                Assert.DoesNotContain("synthetic-invalid", line, StringComparison.Ordinal);
                Assert.DoesNotContain("parameters", line, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("projectId", line, StringComparison.OrdinalIgnoreCase);
            });
        }
        else Assert.Equal(withSink ? "fileSink" : "ringBuffer", exported.GetProperty("source").GetString());

        if (metadataBefore is null) Assert.False(File.Exists(metadata));
        else Assert.Equal(metadataBefore, await File.ReadAllBytesAsync(metadata));
        Assert.Empty(Directory.GetFiles(folder, ".JET-*.tmp"));
        var stillNoActive = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("export.calendarTemplates"));
        Assert.Equal(JetErrorCodes.NoActiveProject, stillNoActive.Code);
    }

    public static IEnumerable<object[]> InvalidDestinationCases() =>
        from action in new[] { "support.log.export", "dev.log.exportFile" }
        from id in new[] { "Missing synthetic case", "../escape", "..\\escape", ".", "NUL", "C:\\outside" }
        select new object[] { action, id };

    [Theory]
    [MemberData(nameof(InvalidDestinationCases))]
    public async Task MissingOrInvalidDestination_IsRejectedWithoutCreatingFiles(string action, string id)
    {
        using var host = new HandlerTestHost(enableDevTools: true);
        Directory.CreateDirectory(host.ProjectsRoot);
        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(action,
            JsonSerializer.Serialize(new { projectId = id })));
        Assert.Equal(JetErrorCodes.ProjectNotFound, error.Code);
        Assert.Empty(Directory.GetFiles(host.ProjectsRoot, "JET-*.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(host.ProjectsRoot, ".JET-*.tmp", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(host.ProjectsRoot, "Missing synthetic case")));
    }

    [Theory]
    [InlineData("support.log.export")]
    [InlineData("dev.log.exportFile")]
    public async Task LinkedProjectDirectory_IsRejectedWithoutWritingIntoItsTarget(string action)
    {
        using var host = new HandlerTestHost(enableDevTools: true);
        var targetId = await CreateAsync(host, "sqlite", "Synthetic link target");
        await host.DispatchAsync("project.releaseLock");
        var target = Path.Combine(host.ProjectsRoot, targetId);
        var link = Path.Combine(host.ProjectsRoot, "Synthetic linked case");
        await CreateDirectoryLinkAsync(link, target);
        try
        {
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(action,
                """{"projectId":"Synthetic linked case"}"""));
            Assert.Equal(JetErrorCodes.SupportLogExportFailed, error.Code);
            Assert.Empty(Directory.GetFiles(target, "JET-*.txt"));
            Assert.Empty(Directory.GetFiles(target, ".JET-*.tmp"));
            Assert.True(File.Exists(Path.Combine(target, "project.json")));
        }
        finally
        {
            // Delete only the test-owned link itself, never recurse into its target.
            Assert.StartsWith(Path.GetFullPath(host.ProjectsRoot) + Path.DirectorySeparatorChar, Path.GetFullPath(link), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(link, recursive: false);
        }
    }

    private static async Task CreateDirectoryLinkAsync(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        // A directory junction needs no administrator or Developer Mode privilege.
        // All arguments are generated, test-owned paths; no user input enters this command.
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"Could not create synthetic junction: {await output} {await error}");
    }

    private static async Task<string> CreateAsync(HandlerTestHost host, string provider, string name)
    {
        var result = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        { caseName = name, periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider }));
        return result.GetProperty("projectId").GetString()!;
    }
}
