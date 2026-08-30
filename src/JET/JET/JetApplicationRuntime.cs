using JET.Bridge;

namespace JET;

/// <summary>
/// Host composition 的明確生命週期擁有者。啟動探測維持非阻斷；Host 關閉時先取消並 join，
/// 再釋放 logger，避免背景 producer 在安裝／測試資源開始拆除後回頭寫入。
/// </summary>
public sealed class JetApplicationRuntime : IDisposable
{
    private static readonly TimeSpan DefaultShutdownJoinTimeout = TimeSpan.FromSeconds(2);

    private readonly IDisposable _ownedResource;
    private readonly TimeSpan _shutdownJoinTimeout;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Lock _gate = new();
    private Task _startupTask = Task.CompletedTask;
    private bool _started;
    private bool _disposed;

    internal JetApplicationRuntime(
        ActionDispatcher dispatcher,
        IDisposable ownedResource,
        TimeSpan? shutdownJoinTimeout = null)
    {
        Dispatcher = dispatcher;
        _ownedResource = ownedResource;
        _shutdownJoinTimeout = shutdownJoinTimeout ?? DefaultShutdownJoinTimeout;
        if (_shutdownJoinTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(shutdownJoinTimeout),
                "Shutdown join timeout must be positive.");
        }
    }

    public ActionDispatcher Dispatcher { get; }

    /// <summary>
    /// 原生視窗關閉的 fail-fast 閘：與 dispatcher 及 releaseLock handler 使用同一個 instance。
    /// 呼叫端若取得 lease，須持有到 <see cref="Dispose"/> 返回後再釋放。
    /// </summary>
    internal IDisposable? TryAcquireShutdownExecutionLease() =>
        Dispatcher.TryAcquireExecutionLease();

    internal void Start(Func<CancellationToken, Task>? startupWork)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("JET startup work has already been registered.");
            }

            _started = true;
            if (startupWork is not null)
            {
                _startupTask = Task.Run(() => startupWork(_cancellation.Token));
            }
        }
    }

    public void Dispose()
    {
        Task startupTask;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            startupTask = _startupTask;
        }

        var startupCancellation = _cancellation.CancelAsync();
        var requestsDrained = Dispatcher.CancellationRegistry.CancelAll();
        var shutdownWork = Task.WhenAll(startupCancellation, startupTask, requestsDrained);
        try
        {
            shutdownWork.Wait(_shutdownJoinTimeout);
        }
        catch (AggregateException)
        {
            // 啟動探測與 request 取消都不得讓關閉失敗；Wait 已觀察 fault。
        }
        finally
        {
            // request 若未在 deadline 內 cooperative 結束，runtime 可以先讓視窗離場，
            // 但不得先 dispose 本地鎖 handle／logger。等 registry 真正 drain 後才拆 owned resources，
            // 防止仍在寫入的作業與第二程序同時持有案件。
            if (requestsDrained.IsCompleted)
            {
                _ownedResource.Dispose();
            }
            else
            {
                _ = requestsDrained.ContinueWith(
                    static (_, state) => ((IDisposable)state!).Dispose(),
                    _ownedResource,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            if (shutdownWork.IsCompleted)
            {
                _cancellation.Dispose();
            }
            else
            {
                _ = shutdownWork.ContinueWith(
                    static (completed, state) =>
                    {
                        _ = completed.Exception;
                        ((CancellationTokenSource)state!).Dispose();
                    },
                    _cancellation,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

        }
    }
}
