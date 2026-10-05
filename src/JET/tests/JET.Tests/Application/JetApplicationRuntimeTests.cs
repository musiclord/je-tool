using JET.Application;
using JET.Bridge;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using Xunit;

namespace JET.Tests.Application;

[Collection(TimingSensitiveCollection.Name)]
public sealed class JetApplicationRuntimeTests
{
    [Fact]
    public async Task Dispose_DuringArtifactExport_CancelsAndJoinsBeforeResourceDisposal()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        const string projectId = "關窗匯出測試案件";
        var projectDirectory = folder.GetProjectDirectory(projectId);
        Directory.CreateDirectory(projectDirectory);
        var store = new ProjectReportArtifactStore(folder);
        var originalBytes = new byte[] { 9, 8, 7, 6 };
        var original = await store.WriteAsync(
            projectId,
            new ReportArtifactWriteRequest(
                ReportArtifactKind.WorkingPaper,
                ScenarioRefs("revision-original"),
                (output, cancellationToken) => output.WriteAsync(originalBytes, cancellationToken).AsTask()),
            CancellationToken.None);
        var finalPath = Path.Combine(projectDirectory, original.RelativeFileName);

        var registry = new RequestCancellationRegistry();
        var cleanupCompleted = false;
        var resource = new RecordingDisposable(() => cleanupCompleted =
            File.ReadAllBytes(finalPath).SequenceEqual(originalBytes)
            && Directory.GetFiles(projectDirectory, "*.tmp").Length == 0);
        var runtime = CreateRuntime(resource, registry);
        var writerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exportToken = CancellationToken.None;
        var exportTask = registry.RunAsync(
            "export-request",
            cancellationToken =>
            {
                exportToken = cancellationToken;
                return store.WriteAsync(
                    projectId,
                    new ReportArtifactWriteRequest(
                        ReportArtifactKind.WorkingPaper,
                        ScenarioRefs("revision-replacement"),
                        async (output, writerCancellationToken) =>
                        {
                            await output.WriteAsync(new byte[] { 1, 2, 3 }, writerCancellationToken);
                            writerEntered.SetResult();
                            await Task.Delay(Timeout.InfiniteTimeSpan, writerCancellationToken);
                        }),
                    cancellationToken);
            });

        await writerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.Dispose();

        var cancelledByShutdown = exportToken.IsCancellationRequested;
        if (!cancelledByShutdown)
        {
            registry.TryCancel("export-request");
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => exportTask.WaitAsync(TimeSpan.FromSeconds(5)));

        // 規格 oracle：報告 store 的正式檔只能在完整寫入後發布；關窗取消不得留下半成品。
        Assert.True(cancelledByShutdown);
        Assert.True(cleanupCompleted);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(finalPath, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(projectDirectory, "*.tmp"));
        Assert.Equal(original.ArtifactId, Assert.Single(
            await store.ListAsync(projectId, CancellationToken.None)).ArtifactId);
    }

    [Fact]
    public async Task Dispose_CancelsAndJoinsStartupWork_BeforeOwnedResource()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new RecordingDisposable(() => Assert.True(exited.Task.IsCompleted));
        var runtime = CreateRuntime(resource);

        runtime.Start(async cancellationToken =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                exited.SetResult();
            }
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.Dispose();

        Assert.True(exited.Task.IsCompleted);
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public async Task Dispose_WhenStartupIgnoresCancellation_ReturnsAtShutdownDeadline()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationCallbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCancellationCallback = new ManualResetEventSlim();
        var resource = new RecordingDisposable();
        var runtime = CreateRuntime(
            resource,
            shutdownJoinTimeout: TimeSpan.FromMilliseconds(100));
        runtime.Start(async cancellationToken =>
        {
            using var registration = cancellationToken.Register(() =>
            {
                cancellationCallbackStarted.SetResult();
                releaseCancellationCallback.Wait();
            });
            started.SetResult();
            await releaseStartup.Task;
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopwatch = Stopwatch.StartNew();
        Task disposeTask = Task.Run(runtime.Dispose);
        Task completedWithinLimit;
        try
        {
            await cancellationCallbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            completedWithinLimit = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            stopwatch.Stop();
            releaseCancellationCallback.Set();
            releaseStartup.TrySetResult();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        // 邊界值：連 cancellation callback 都卡住時，也不能繞過測試注入的 100ms deadline。
        Assert.Same(disposeTask, completedWithinLimit);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(80), TimeSpan.FromSeconds(2));
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public async Task Dispose_WhenRequestMissesDeadline_DefersOwnedResourceUntilRequestDrains()
    {
        var registry = new RequestCancellationRegistry();
        var requestEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new RecordingDisposable();
        var runtime = CreateRuntime(
            resource,
            registry,
            shutdownJoinTimeout: TimeSpan.FromMilliseconds(100));
        var request = registry.RunAsync(
            "non-cooperative-request",
            async _ =>
            {
                requestEntered.SetResult();
                await releaseRequest.Task;
                return true;
            });

        await requestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.Dispose();

        Assert.Equal(0, resource.DisposeCount);
        releaseRequest.SetResult();
        Assert.True(await request.WaitAsync(TimeSpan.FromSeconds(5)));
        await WaitUntilAsync(() => resource.DisposeCount == 1);
    }

    [Fact]
    public void ShutdownExecutionLease_UsesDispatcherGateAndRemainsFailFast()
    {
        var resource = new RecordingDisposable();
        var gate = new ActionExecutionGate();
        var dispatcher = new ActionDispatcher(
            [],
            NullLogger<ActionDispatcher>.Instance,
            new ProjectSession(),
            executionGate: gate);
        var runtime = new JetApplicationRuntime(dispatcher, resource);
        runtime.Start(startupWork: null);

        using (Assert.IsAssignableFrom<IDisposable>(gate.TryAcquire()))
        {
            Assert.Null(runtime.TryAcquireShutdownExecutionLease());
        }

        using var shutdownLease = Assert.IsAssignableFrom<IDisposable>(
            runtime.TryAcquireShutdownExecutionLease());
        runtime.Dispose();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var resource = new RecordingDisposable();
        var runtime = CreateRuntime(resource);

        runtime.Start(startupWork: null);
        runtime.Dispose();
        runtime.Dispose();

        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public void Dispose_WhenStartupFaults_ObservesFaultAndStillDisposesResource()
    {
        var resource = new RecordingDisposable();
        var runtime = CreateRuntime(resource);

        runtime.Start(_ => Task.FromException(new InvalidOperationException("startup fault")));

        var exception = Record.Exception(runtime.Dispose);
        Assert.Null(exception);
        Assert.Equal(1, resource.DisposeCount);
    }

    private static JetApplicationRuntime CreateRuntime(
        IDisposable resource,
        RequestCancellationRegistry? cancellationRegistry = null,
        TimeSpan? shutdownJoinTimeout = null) =>
        new(
            new ActionDispatcher(
                [],
                NullLogger<ActionDispatcher>.Instance,
                new ProjectSession(),
                cancellationRegistry: cancellationRegistry),
            resource,
            shutdownJoinTimeout);

    private static ReportArtifactSourceRefs ScenarioRefs(string revision) =>
        new(
            ValidationRunId: "11111111111111111111111111111111",
            PrescreenRunId: "22222222222222222222222222222222",
            ScenarioRevision: revision,
            ScenarioPositions: [1]);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate())
        {
            Assert.True(DateTime.UtcNow < deadline, "等待 runtime deferred disposal 逾時。");
            await Task.Delay(10);
        }
    }

    private sealed class RecordingDisposable(Action? onDispose = null) : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            onDispose?.Invoke();
        }
    }
}
