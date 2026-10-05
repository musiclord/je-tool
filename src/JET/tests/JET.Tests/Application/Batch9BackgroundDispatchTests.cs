using System.Text.Json;
using System.Text.RegularExpressions;
using JET.Application;
using JET.Bridge;
using JET.Domain;
using JET.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch9BackgroundDispatchTests
{
    [Fact]
    public async Task SynchronousLongOperation_ReturnsToCallerSoCancelCanReleaseGateAndRetention()
    {
        var registry = new RequestCancellationRegistry();
        using var request = registry.Begin("batch9-sync");
        var handler = new SynchronousBlockingHandler();
        var retention = new RetentionProbe();
        var session = new ProjectSession();
        session.Enter("batch9-thread", TestProjectRepositories.Unconfigured("sqlite"));
        var dispatcher = new ActionDispatcher([handler, new OperationCancelHandler(registry)],
            NullLogger<ActionDispatcher>.Instance, session, cancellationRegistry: registry, databaseRetention: retention);
        await OnDedicatedThreadAsync(async () =>
        {
            var work = dispatcher.DispatchAsync("validate.run", JsonSerializer.SerializeToElement(new { }), request.Token);
            Assert.False(work.IsCompleted);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var cancelled = await dispatcher.DispatchAsync("operation.cancel", JsonSerializer.SerializeToElement(new { requestId = "batch9-sync" }), CancellationToken.None);
            Assert.True(JsonSerializer.SerializeToElement(cancelled).GetProperty("requested").GetBoolean());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
            Assert.True(retention.Disposed);
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
            .Concat(ActionExecutionPolicy.ProjectDatabaseRetainingActionNames)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var data = new TheoryData<string>();
        foreach (var action in actions) data.Add(action);
        Assert.Contains("query.filterVoucherPage", actions);
        Assert.Contains("validate.run", actions);
        return data;
    }

    [Theory]
    [MemberData(nameof(DatabaseActions))]
    public async Task CancellableAndDatabaseActions_MoveSynchronousHandlerAndRetentionOffCallerThread(string action)
    {
        var probe = new ProbeHandler(action);
        var retention = new RetentionProbe();
        var session = new ProjectSession();
        session.Enter("batch9-thread", TestProjectRepositories.Unconfigured("sqlite"));
        var dispatcher = new ActionDispatcher([probe], NullLogger<ActionDispatcher>.Instance, session,
            databaseRetention: retention);
        var caller = await OnDedicatedThreadAsync(() => dispatcher.DispatchAsync(action, JsonSerializer.SerializeToElement(new { }), CancellationToken.None));
        Assert.NotEqual(caller, probe.ThreadId);
        Assert.True(probe.ThreadPool);
        Assert.Null(probe.Context);
        if (ActionExecutionPolicy.RetainsProjectDatabase(action))
        {
            Assert.NotEqual(caller, retention.ThreadId);
            Assert.True(retention.ThreadPool);
            Assert.True(retention.Disposed);
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

    private sealed class RetentionProbe : IProjectDatabaseRetention, IDisposable
    {
        public int ThreadId { get; private set; }
        public bool ThreadPool { get; private set; }
        public bool Disposed { get; private set; }
        public IDisposable TryRetain(string projectId)
        {
            ThreadId = Environment.CurrentManagedThreadId;
            ThreadPool = Thread.CurrentThread.IsThreadPoolThread;
            return this;
        }
        public void Dispose() => Disposed = true;
    }
}
