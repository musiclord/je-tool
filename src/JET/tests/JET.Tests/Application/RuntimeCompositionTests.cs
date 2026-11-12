using System.Text.Json;
using JET.Application;
using JET.Bridge;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 組裝 runtime 時，呼叫端可以包裝每個 action handler，並交給 runtime 一段啟動後在背景執行的工作。
/// </summary>
public sealed class RuntimeCompositionTests
{
    [Fact]
    public async Task CreateRuntime_PassesEveryRegisteredHandlerThroughTheDecorator()
    {
        using var root = new TempProjectRoot();
        var decorated = new List<string>();
        ProjectSession? seenSession = null;
        using var runtime = AppCompositionRoot.CreateRuntime(
            new StubHostShell(),
            root.Path,
            enableDevTools: false,
            userProfileDirectory: Path.Combine(root.Path, "user-profile"),
            decorateHandler: (handler, session) =>
            {
                decorated.Add(handler.Action);
                seenSession ??= session;
                return handler.Action == "system.ping" ? new FixedResultHandler(handler.Action) : handler;
            });

        Assert.Equal(
            runtime.Dispatcher.RegisteredActions.Order(StringComparer.Ordinal),
            decorated.Order(StringComparer.Ordinal));
        Assert.NotNull(seenSession);
        using var payload = JsonDocument.Parse("{}");
        var result = await runtime.Dispatcher.DispatchAsync("system.ping", payload.RootElement, CancellationToken.None);
        Assert.Same(FixedResultHandler.Result, result);
    }

    [Fact]
    public async Task CreateRuntime_RunsStartupWorkWithItsOwnDispatcher_AndCancelsItOnDispose()
    {
        using var root = new TempProjectRoot();
        var started = new TaskCompletionSource<ActionDispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = AppCompositionRoot.CreateRuntime(
            new StubHostShell(),
            root.Path,
            enableDevTools: false,
            userProfileDirectory: Path.Combine(root.Path, "user-profile"),
            startupWork: async (dispatcher, cancellationToken) =>
            {
                started.SetResult(dispatcher);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancelled.SetResult();
                    throw;
                }
            });
        try
        {
            Assert.Same(runtime.Dispatcher, await started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            runtime.Dispose();
        }

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class FixedResultHandler(string action) : IApplicationActionHandler
    {
        internal static readonly object Result = new();

        public string Action => action;

        public Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken) =>
            Task.FromResult<object?>(Result);
    }

    private sealed class StubHostShell : IHostShell
    {
        public Task<string?> PickOpenFileAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> PickOpenFilesAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task RevealInExplorerAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public void RequestExit()
        {
        }
    }
}
