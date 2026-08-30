using JET.Application;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// host.exitApp 契約測試：handler 只委派 IHostShell.RequestExit 並回 ok（純 host capability）。
/// IHostShell 為 host boundary，依測試規範以手寫 recording stub 驗證。
/// </summary>
public sealed class HostExitAppHandlerTests
{
    [Fact]
    public async Task ExitApp_RequestsHostExitAndReturnsOk()
    {
        var shell = new RecordingHostShell();
        using var host = new HandlerTestHost(shell);

        var data = await host.DispatchAsync("host.exitApp");

        Assert.True(data.GetProperty("ok").GetBoolean());
        Assert.True(shell.ExitRequested);
    }

    [Fact]
    public async Task ExitApp_WorksWithoutActiveProject()
    {
        // 結束應用程式不需要 active project（picker 畫面也能退出）。
        var shell = new RecordingHostShell();
        using var host = new HandlerTestHost(shell);

        var data = await host.DispatchAsync("host.exitApp");

        Assert.True(data.GetProperty("ok").GetBoolean());
    }

    private sealed class RecordingHostShell : IHostShell
    {
        public bool ExitRequested { get; private set; }

        public Task<string?> PickOpenFileAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<IReadOnlyList<string>> PickOpenFilesAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<string?> PickSavePathAsync(string baseFileName, CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(null);
        }

        public Task RevealInExplorerAsync(string path, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public void RequestExit()
        {
            ExitRequested = true;
        }
    }
}
