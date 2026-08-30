using System.Text.Json;
using JET.Application;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// host.selectSavePath 契約測試：handler 將檔名片段委派 host boundary，取消時回 null。
/// </summary>
public sealed class HostSelectSavePathHandlerTests
{
    [Fact]
    public async Task SelectSavePath_MissingName_UsesWorkingPaperBaseName()
    {
        var shell = new RecordingHostShell("C:/audit/WorkingPaper.xlsx");
        using var host = new HandlerTestHost(shell);

        var data = await host.DispatchAsync("host.selectSavePath");

        Assert.Equal("WorkingPaper", shell.BaseFileName);
        Assert.Equal("C:/audit/WorkingPaper.xlsx", data.GetProperty("path").GetString());
    }

    [Fact]
    public async Task SelectSavePath_CustomName_PassesTrimmedBaseNameToHost()
    {
        var shell = new RecordingHostShell("D:/exports/ACME.xlsx");
        using var host = new HandlerTestHost(shell);

        var data = await host.DispatchAsync(
            "host.selectSavePath",
            JsonSerializer.Serialize(new { defaultFileName = "  ACME 2025  " }));

        Assert.Equal("ACME 2025", shell.BaseFileName);
        Assert.Equal("D:/exports/ACME.xlsx", data.GetProperty("path").GetString());
    }

    [Fact]
    public async Task SelectSavePath_BlankName_UsesWorkingPaperBaseName()
    {
        var shell = new RecordingHostShell("C:/audit/fallback.xlsx");
        using var host = new HandlerTestHost(shell);

        await host.DispatchAsync(
            "host.selectSavePath",
            JsonSerializer.Serialize(new { defaultFileName = "   " }));

        Assert.Equal("WorkingPaper", shell.BaseFileName);
    }

    [Fact]
    public async Task SelectSavePath_InvalidFileNameCharacters_AreRemovedBeforeHostDialog()
    {
        var shell = new RecordingHostShell("C:/audit/safe.xlsx");
        using var host = new HandlerTestHost(shell);

        await host.DispatchAsync(
            "host.selectSavePath",
            JsonSerializer.Serialize(new { defaultFileName = "  客戶:2025/測試*  " }));

        Assert.Equal("客戶2025測試", shell.BaseFileName);
    }

    [Fact]
    public async Task SelectSavePath_UserCancels_ReturnsNullPath()
    {
        var shell = new RecordingHostShell(null);
        using var host = new HandlerTestHost(shell);

        var data = await host.DispatchAsync("host.selectSavePath");

        Assert.Equal(JsonValueKind.Null, data.GetProperty("path").ValueKind);
    }

    private sealed class RecordingHostShell(string? selectedPath) : IHostShell
    {
        public string? BaseFileName { get; private set; }

        public Task<string?> PickSavePathAsync(string baseFileName, CancellationToken cancellationToken)
        {
            BaseFileName = baseFileName;
            return Task.FromResult(selectedPath);
        }

        public Task<string?> PickOpenFileAsync(
            string title, IReadOnlyList<string> extensions, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> PickOpenFilesAsync(
            string title, IReadOnlyList<string> extensions, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public void RequestExit() { }

        public Task RevealInExplorerAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
