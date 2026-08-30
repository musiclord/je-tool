using Xunit;

namespace JET.Tests.Application;

public sealed class Form1DialogCancellationTests
{
    [Fact]
    public async Task DeferredDialog_CancelledBeforeUiCallback_CancelsWithoutShowingDialog()
    {
        Action? queuedCallback = null;
        using var cancellation = new CancellationTokenSource();
        var showCount = 0;
        var dialogTask = Form1.RunDialogDeferredAsync(
            callback => queuedCallback = callback,
            () =>
            {
                showCount++;
                return "selected.xlsx";
            },
            cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dialogTask);
        queuedCallback!();

        Assert.Equal(0, showCount);
    }

    [Fact]
    public async Task DeferredDialog_CancelledWhileModalOpen_IgnoresLateDialogResult()
    {
        Action? queuedCallback = null;
        using var cancellation = new CancellationTokenSource();
        using var releaseDialog = new ManualResetEventSlim();
        var dialogEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogTask = Form1.RunDialogDeferredAsync(
            callback => queuedCallback = callback,
            () =>
            {
                dialogEntered.SetResult();
                releaseDialog.Wait(TimeSpan.FromSeconds(5));
                return "late-result.xlsx";
            },
            cancellation.Token);
        var callbackTask = Task.Run(queuedCallback!);

        await dialogEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dialogTask);
        releaseDialog.Set();

        var exception = await Record.ExceptionAsync(
            () => callbackTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(exception);
    }
}
