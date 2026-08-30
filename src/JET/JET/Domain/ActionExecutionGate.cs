namespace JET.Domain;

/// <summary>
/// 變更型作業共用的非阻塞序列化閘。dispatcher 與 concurrent query 的條件式寫入分支
/// 共用同一實例；取不到時由呼叫端回 operation_in_progress，不等待也不排隊。
/// </summary>
public sealed class ActionExecutionGate
{
    private int _held;

    public IDisposable? TryAcquire()
    {
        if (Interlocked.CompareExchange(ref _held, 1, 0) != 0)
        {
            return null;
        }

        return new Lease(this);
    }

    private void Release() => Interlocked.Exchange(ref _held, 0);

    private sealed class Lease(ActionExecutionGate owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}
