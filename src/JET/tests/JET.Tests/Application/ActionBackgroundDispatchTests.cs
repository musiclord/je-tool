using System.Text.Json;
using System.Text.RegularExpressions;
using JET.Application;
using JET.Bridge;
using JET.Domain;
using JET.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JET.Tests.Application;

public sealed class ActionBackgroundDispatchTests
{
    [Fact]
    public async Task SynchronousLongOperation_ReturnsToCallerSoCancelCanReleaseGateAfterCheckpoint()
    {
        // 驗 SQLite 的記錄檔併回：取消後照樣併回一次，之後閘也放開。
        var registry = new RequestCancellationRegistry();
        using var request = registry.Begin("sync-request");
        var handler = new SynchronousBlockingHandler();
        var checkpoint = new CheckpointProbe();
        var session = new ProjectSession();
        session.Enter("thread-project", TestProjectRepositories.Unconfigured("sqlite"));
        var dispatcher = new ActionDispatcher([handler, new OperationCancelHandler(registry)],
            NullLogger<ActionDispatcher>.Instance, session, cancellationRegistry: registry, databaseCheckpoint: checkpoint);
        await OnDedicatedThreadAsync(async () =>
        {
            var work = dispatcher.DispatchAsync("validate.run", JsonSerializer.SerializeToElement(new { }), request.Token);
            Assert.False(work.IsCompleted);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var cancelled = await dispatcher.DispatchAsync("operation.cancel", JsonSerializer.SerializeToElement(new { requestId = "sync-request" }), CancellationToken.None);
            Assert.True(JsonSerializer.SerializeToElement(cancelled).GetProperty("requested").GetBoolean());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
            Assert.Equal(1, checkpoint.Calls);
            using var lease = dispatcher.TryAcquireExecutionLease();
            Assert.NotNull(lease);
            return null;
        });
    }

    private sealed class SynchronousBlockingHandler : IApplicationActionHandler
    {
        public string Action => "validate.run";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
        {
            Entered.SetResult();
            if (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5))) throw new TimeoutException("Cancel could not reach synchronous operation.");
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<object?>(null);
        }
    }

    public static TheoryData<string> DatabaseActions()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "JET.slnx"))) root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("JET solution not found.");
        var source = File.ReadAllText(Path.Combine(root.FullName, "JET", "wwwroot", "js", "app.js"));
        var match = Regex.Match(source, @"var CANCELLABLE_ACTIONS = \{(?<body>[\s\S]*?)\};");
        Assert.True(match.Success);
        var actions = Regex.Matches(match.Groups["body"].Value, @"'([^']+)'\s*:\s*true")
            .Select(item => item.Groups[1].Value)
            .Concat(ActionExecutionPolicy.ProjectDatabaseActionNames)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var data = new TheoryData<string>();
        foreach (var action in actions) data.Add(action);
        Assert.Contains("query.filterVoucherPage", actions);
        Assert.Contains("validate.run", actions);
        return data;
    }

    [Theory]
    [MemberData(nameof(DatabaseActions))]
    public async Task CancellableAndDatabaseActions_MoveSynchronousHandlerAndCheckpointOffCallerThread(string action)
    {
        // 驗 SQLite 的記錄檔併回也不在呼叫端執行緒。
        var probe = new ProbeHandler(action);
        var checkpoint = new CheckpointProbe();
        var session = new ProjectSession();
        session.Enter("thread-project", TestProjectRepositories.Unconfigured("sqlite"));
        var dispatcher = new ActionDispatcher([probe], NullLogger<ActionDispatcher>.Instance, session,
            databaseCheckpoint: checkpoint);
        var caller = await OnDedicatedThreadAsync(() => dispatcher.DispatchAsync(action, JsonSerializer.SerializeToElement(new { }), CancellationToken.None));
        Assert.NotEqual(caller, probe.ThreadId);
        Assert.True(probe.ThreadPool);
        Assert.Null(probe.Context);
        if (ActionExecutionPolicy.CheckpointsProjectDatabase(action))
        {
            Assert.NotEqual(caller, checkpoint.ThreadId);
            Assert.True(checkpoint.ThreadPool);
            Assert.Equal(1, checkpoint.Calls);
        }
        else
        {
            Assert.Equal(0, checkpoint.Calls);
        }
        Assert.Equal(1, probe.Calls);
    }

    [Theory]
    [InlineData("host.selectFile")]
    [InlineData("host.selectFiles")]
    [InlineData("host.openFolder")]
    [InlineData("host.exitApp")]
    [InlineData("operation.cancel")]
    public async Task NativeAndCancellationActions_KeepTheirCallerThread(string action)
    {
        var probe = new ProbeHandler(action);
        var dispatcher = new ActionDispatcher([probe], NullLogger<ActionDispatcher>.Instance, new ProjectSession());
        var caller = await OnDedicatedThreadAsync(() => dispatcher.DispatchAsync(action, JsonSerializer.SerializeToElement(new { }), CancellationToken.None));
        Assert.Equal(caller, probe.ThreadId);
        Assert.False(probe.ThreadPool);
        Assert.NotNull(probe.Context);
    }

    private static async Task<int> OnDedicatedThreadAsync(Func<Task<object?>> action)
    {
        var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            try { action().GetAwaiter().GetResult(); completed.SetResult(Environment.CurrentManagedThreadId); }
            catch (Exception error) { completed.SetException(error); }
        }) { IsBackground = true };
        thread.Start();
        return await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class ProbeHandler(string action) : IApplicationActionHandler
    {
        public string Action => action;
        public int ThreadId { get; private set; }
        public bool ThreadPool { get; private set; }
        public SynchronizationContext? Context { get; private set; }
        public int Calls { get; private set; }
        public Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
        {
            ThreadId = Environment.CurrentManagedThreadId;
            ThreadPool = Thread.CurrentThread.IsThreadPoolThread;
            Context = SynchronizationContext.Current;
            Calls++;
            return Task.FromResult<object?>(new { ok = true });
        }
    }

    private sealed class CheckpointProbe : IProjectDatabaseCheckpoint
    {
        public int ThreadId { get; private set; }
        public bool ThreadPool { get; private set; }
        public int Calls { get; private set; }
        public Task CheckpointAsync(string projectId, CancellationToken cancellationToken)
        {
            ThreadId = Environment.CurrentManagedThreadId;
            ThreadPool = Thread.CurrentThread.IsThreadPoolThread;
            Calls++;
            return Task.CompletedTask;
        }
        public Task CloseAsync(string projectId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
