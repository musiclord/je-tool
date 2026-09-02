using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// dev.log.export 的 Application 層驗收(TDD #6:NDJSON 每行可被 System.Text.Json 解析)。
/// 原始診斷日誌為 dev-only；檔案匯出只接受一個既有案件並直接寫入該案件目錄。
/// </summary>
public sealed class DevLogHandlersTests
{
    [Fact]
    public async Task DevLogExport_ReturnsNdjson_EachLineParsableByStj()
    {
        using var host = new HandlerTestHost(enableDevTools: true);

        await host.DispatchAsync("system.ping"); // 產生 action.start/end 診斷日誌

        var export = await host.DispatchAsync("dev.log.export");
        var ndjson = export.GetProperty("ndjson").GetString()!;
        var lines = ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line); // 每行皆為完整 JSON 物件（NDJSON）
            Assert.True(document.RootElement.TryGetProperty("eventName", out _));
        }

        Assert.Contains(lines, l => l.Contains("action.start")); // 含預期事件
    }

    [Fact]
    public async Task DevLogExportFile_WritesSinkContentToTxt_WhenSinkPresent()
    {
        using var host = new HandlerTestHost(enableDevTools: true, withDiagnosticLogFile: true);
        var projectId = await CreateProjectAsync(host);

        await host.DispatchAsync("system.ping"); // 產生 action.start/end 診斷日誌

        var export = await host.DispatchAsync(
            "dev.log.exportFile",
            JsonSerializer.Serialize(new { projectId }));

        Assert.Equal("fileSink", export.GetProperty("source").GetString());
        var filePath = export.GetProperty("filePath").GetString()!;
        Assert.StartsWith(
            Path.Combine(host.ProjectsRoot, projectId) + Path.DirectorySeparatorChar,
            filePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".txt", filePath, StringComparison.Ordinal);
        Assert.True(File.Exists(filePath));

        var lines = (await File.ReadAllTextAsync(filePath))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(export.GetProperty("lineCount").GetInt32(), lines.Length);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line); // 內容維持 NDJSON:每行一筆完整 JSON 物件
            Assert.True(document.RootElement.TryGetProperty("eventName", out _));
        }

        Assert.Contains(lines, l => l.Contains("action.start"));
    }

    [Fact]
    public async Task DevLogExportFile_FallsBackToRingBuffer_WhenNoSink()
    {
        // 預設 host 未掛檔案 sink：內容退回 ring buffer，但目的地仍只准目前案件目錄。
        using var host = new HandlerTestHost(enableDevTools: true);
        var projectId = await CreateProjectAsync(host);

        await host.DispatchAsync("system.ping");

        var export = await host.DispatchAsync(
            "dev.log.exportFile",
            JsonSerializer.Serialize(new { projectId }));

        Assert.Equal("ringBuffer", export.GetProperty("source").GetString());
        var filePath = export.GetProperty("filePath").GetString()!;
        Assert.StartsWith(
            Path.Combine(host.ProjectsRoot, projectId) + Path.DirectorySeparatorChar,
            filePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(filePath));
        Assert.Contains("action.start", await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task DevLogExportFile_CorruptSinkFallsBackAndRemovesPartialTemporaryFile()
    {
        using var host = new HandlerTestHost(enableDevTools: true, withDiagnosticLogFile: true);
        var projectId = await CreateProjectAsync(host);
        await host.DispatchAsync("system.ping");
        var sinkPath = Assert.Single(Directory.GetFiles(host.DiagnosticLogDirectory!, "*.ndjson"));
        await File.AppendAllTextAsync(sinkPath, "not-json\n");

        var export = await host.DispatchAsync(
            "dev.log.exportFile",
            JsonSerializer.Serialize(new { projectId }));

        Assert.Equal("ringBuffer", export.GetProperty("source").GetString());
        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);
        Assert.Empty(Directory.GetFiles(projectDirectory, ".JET-dev-log-*.tmp"));
        var filePath = export.GetProperty("filePath").GetString()!;
        Assert.True(File.Exists(filePath));
        Assert.Contains("action.start", await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task DevLogExportFile_LockedSinkFallsBackToRingBufferAndRemovesPartialTemporaryFile()
    {
        // sink 檔存在但被其他 handle 以 FileShare.None 佔住：開啟失敗是來源端 I/O 例外，
        // 應退回 ring buffer，而不是被 ProjectLogFileWriter 包成寫入失敗回給使用者。
        using var host = new HandlerTestHost(enableDevTools: true, withDiagnosticLogFile: true);
        var projectId = await CreateProjectAsync(host);
        await host.DispatchAsync("system.ping");
        var sinkPath = Assert.Single(Directory.GetFiles(host.DiagnosticLogDirectory!, "*.ndjson"));

        JsonElement export;
        await using (new FileStream(sinkPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            export = await host.DispatchAsync(
                "dev.log.exportFile",
                JsonSerializer.Serialize(new { projectId }));
        }

        Assert.Equal("ringBuffer", export.GetProperty("source").GetString());
        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);
        Assert.Empty(Directory.GetFiles(projectDirectory, ".JET-dev-log-*.tmp"));
        var filePath = export.GetProperty("filePath").GetString()!;
        Assert.True(File.Exists(filePath));
        Assert.Contains("action.start", await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task SupportLogExport_IsAvailableInReleaseAndWritesOnlySafeNdjsonToProject()
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        var projectId = await CreateProjectAsync(host);
        await host.DispatchAsync("system.ping");

        var export = await host.DispatchAsync(
            "support.log.export",
            JsonSerializer.Serialize(new { projectId }));

        var filePath = export.GetProperty("filePath").GetString()!;
        Assert.StartsWith(
            Path.Combine(host.ProjectsRoot, projectId) + Path.DirectorySeparatorChar,
            filePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".txt", filePath, StringComparison.Ordinal);

        var lines = (await File.ReadAllLinesAsync(filePath))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        Assert.Equal(export.GetProperty("lineCount").GetInt32(), lines.Length);
        Assert.True(lines.Length >= 3, "metadata 加上 system.ping 的 start/end 至少應有三行。");
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.True(document.RootElement.TryGetProperty("eventName", out _));
            Assert.DoesNotContain("projectId", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("parameters", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(host.ProjectsRoot, line, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(lines, line => line.Contains("support.snapshot", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("action.start", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("action.end", StringComparison.Ordinal));
    }

    private static async Task<string> CreateProjectAsync(HandlerTestHost host)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = $"LOG-{suffix}",
            entityName = "Diagnostic Test",
            operatorId = "tester",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            databaseProvider = "sqlite",
        }));
        return created.GetProperty("projectId").GetString()!;
    }
}
