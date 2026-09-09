namespace JET.Infrastructure;

/// <summary>容量固定且可供多執行緒使用；滿載時覆寫最舊項目，快照依舊到新排序。</summary>
internal sealed class BoundedRingBuffer<T>(int capacity)
{
    private readonly T[] _items = new T[Math.Max(1, capacity)];
    private readonly Lock _gate = new();
    private int _start;
    private int _count;

    public void Add(T entry)
    {
        lock (_gate)
        {
            if (_count < _items.Length)
            {
                _items[(_start + _count) % _items.Length] = entry;
                _count++;
            }
            else
            {
                _items[_start] = entry;
                _start = (_start + 1) % _items.Length;
            }
        }
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (_gate)
        {
            var result = new T[_count];
            for (var index = 0; index < _count; index++)
            {
                result[index] = _items[(_start + index) % _items.Length];
            }

            return result;
        }
    }
}
