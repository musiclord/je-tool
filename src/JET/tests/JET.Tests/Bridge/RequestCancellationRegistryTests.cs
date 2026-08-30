using JET.Bridge;
using JET.Domain;
using Xunit;

namespace JET.Tests.Bridge;

public sealed class RequestCancellationRegistryTests
{
    [Fact]
    public async Task CancelAll_CancelsEveryInFlightRequest_AndIsIdempotent()
    {
        var registry = new RequestCancellationRegistry();
        var first = registry.Begin("request-1");
        var second = registry.Begin("request-2");

        var firstDrain = registry.CancelAll();
        var secondDrain = registry.CancelAll();

        Assert.True(first.Token.IsCancellationRequested);
        Assert.True(second.Token.IsCancellationRequested);
        Assert.False(firstDrain.IsCompleted);
        Assert.False(secondDrain.IsCompleted);

        first.Dispose();
        second.Dispose();
        await Task.WhenAll(firstDrain, secondDrain).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancelAll_WhenRequestCallbackBlocks_ReturnsPendingTaskWithoutBlockingCaller()
    {
        var registry = new RequestCancellationRegistry();
        var scope = registry.Begin("blocking-callback");
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        using var registration = scope.Token.Register(() =>
        {
            callbackStarted.SetResult();
            releaseCallback.Wait();
        });

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var shutdown = registry.CancelAll();
        stopwatch.Stop();
        try
        {
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(scope.Token.IsCancellationRequested);
            Assert.False(shutdown.IsCompleted);
            Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
        finally
        {
            releaseCallback.Set();
            scope.Dispose();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void Begin_AfterCancelAll_CannotStartUncancelledWork()
    {
        var registry = new RequestCancellationRegistry();
        registry.CancelAll();

        Assert.ThrowsAny<OperationCanceledException>(() => registry.Begin("late-request"));
    }

    [Fact]
    public void RegisteredRequest_CanBeCancelled_AndIsRemovedWhenScopeEnds()
    {
        var registry = new RequestCancellationRegistry();
        var scope = registry.Begin("request-1");

        Assert.True(registry.TryCancel("request-1"));
        Assert.True(scope.Token.IsCancellationRequested);

        scope.Dispose();
        Assert.False(registry.TryCancel("request-1"));
    }

    [Fact]
    public void DuplicateInFlightRequestId_IsRejectedWithoutReplacingOriginalToken()
    {
        var registry = new RequestCancellationRegistry();
        using var original = registry.Begin("same-id");

        var error = Assert.Throws<JetActionException>(() => registry.Begin("same-id"));

        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
        Assert.True(registry.TryCancel("same-id"));
        Assert.True(original.Token.IsCancellationRequested);
    }
}
