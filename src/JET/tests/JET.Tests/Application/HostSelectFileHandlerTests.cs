using System.Text.Json;
using JET.Application;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// host.selectFile 契約測試：handler 只委派 host boundary，並回傳路徑與檔名。
/// IHostShell 依專案測試規範使用手寫 recording stub。
/// </summary>
public sealed class HostSelectFileHandlerTests
{
    [Fact]
    public async Task SelectFile_DefaultPayload_UsesDefaultTitleAndXlsxExtension()
    {
        var shell = new RecordingHostShell("C:/audit/report.xlsx");
        using var host = new HandlerTestHost(shell);

        var data = await host.DispatchAsync("host.selectFile");

        Assert.Equal("C:/audit/report.xlsx", data.GetProperty("filePath").GetString());
        Assert.Equal("report.xlsx", data.GetProperty("fileName").GetString());
        Assert.Equal("選擇檔案", shell.Title);
        Assert.Equal([".xlsx"], shell.Extensions);
    }

    [Fact]
    public async Task SelectFile_CustomPayload_PassesTitleAndExtensionsToHost()
    {
        var shell = new RecordingHostShell("C:/audit/source.csv");
        using var host = new HandlerTestHost(shell);

        var data = await host.DispatchAsync("host.selectFile", JsonSerializer.Serialize(new
        {
            title = "選擇來源檔",
            extensions = new[] { ".csv", ".txt" }
        }));

        Assert.Equal("C:/audit/source.csv", data.GetProperty("filePath").GetString());
        Assert.Equal("source.csv", data.GetProperty("fileName").GetString());
        Assert.Equal("選擇來源檔", shell.Title);
        Assert.Equal([".csv", ".txt"], shell.Extensions);
    }

    [Fact]
    public async Task SelectFile_UserCancels_ReturnsNullPathAndName()
    {
        var shell = new RecordingHostShell(null);
        using var host = new HandlerTestHost(shell);

        var data = await host.DispatchAsync("host.selectFile");

        Assert.Equal(JsonValueKind.Null, data.GetProperty("filePath").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("fileName").ValueKind);
    }

    private sealed class RecordingHostShell(string? selectedPath) : IHostShell
    {
        public string? Title { get; private set; }
        public IReadOnlyList<string>? Extensions { get; private set; }

        public Task<string?> PickOpenFileAsync(
            string title, IReadOnlyList<string> extensions, CancellationToken cancellationToken)
        {
            Title = title;
            Extensions = extensions;
            return Task.FromResult(selectedPath);
        }

        public Task<IReadOnlyList<string>> PickOpenFilesAsync(
            string title, IReadOnlyList<string> extensions, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickSavePathAsync(string baseFileName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public void RequestExit() { }

        public Task RevealInExplorerAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
