using JET.Bridge;

namespace JET;

/// <summary>
/// 管理 <c>AppCompositionRoot</c> 建好的 dispatcher 與資源，從啟動到關閉都由它負責。
/// 啟動工作在背景執行，不擋住視窗出現。關閉時先取消啟動工作與進行中的請求並等它們結束，再釋放 logger；
/// 這樣背景工作不會在安裝程式或測試開始清除資源之後回頭寫入。
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
    /// 關閉視窗前用來取得共用的閘；取不到時立刻回 null，不等待。
    /// 這把閘和 dispatcher、<c>ProjectReleaseLockHandler</c> 用的是同一個。
    /// 呼叫端取得 lease 之後，要持有到 <see cref="Dispose"/> 返回才釋放。
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
            // 請求如果沒有在等待時限內自己結束，runtime 可以先讓視窗關閉，
            // 但不能先釋放本機鎖的 handle 與 logger。要等進行中的請求全部結束，才釋放 runtime 擁有的資源；
            // 否則還在寫入的作業會和另一個 JET 程序同時持有同一個案件。
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
