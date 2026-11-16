using System.Diagnostics;

namespace JET.Infrastructure;

/// <summary>
/// 一次操作的監看紀錄：經過的步驟、SQL 的筆數與時間、要求取消的時間，以及定時紀錄與匯出進度的節流狀態。
/// 操作本身的執行緒、SQL 語句與監看模組的計時器都會寫入；計數用 Interlocked，清單用鎖保護。
/// </summary>
internal sealed class OperationWatch : IDisposable
{
    /// <summary>步驟路徑保留最前面幾個與最後幾個步驟，中間的只記個數。</summary>
    private const int HeadStages = 4;
    private const int TailStages = 28;

    /// <summary>步驟路徑的長度上限；支援日誌收這個欄位的上限比它大，路徑不會在半途被截斷。</summary>
    internal const int MaxStagePathLength = 1000;

    private const int MaxSqlTexts = 64;
    private const string OtherSqlTexts = "(other statements)";

    private readonly OperationMonitor _monitor;
    private readonly Lock _gate = new();
    private readonly List<(string Stage, long OffsetMilliseconds)> _headStages = [];
    private readonly Queue<(string Stage, long OffsetMilliseconds)> _tailStages = new();
    private readonly Dictionary<string, SqlTextStats> _sqlTexts = new(StringComparer.Ordinal);
    private CancellationToken _cancellationToken;
    private CancellationTokenRegistration _cancelRegistration;
    private string? _lastSql;
    private SqlTextStats? _lastSqlStats;
    private string? _currentStage;
    private int _stagesEntered;
    private long _statements;
    private long _slowStatements;
    private long _failedStatements;
    private long _sqlTicks;
    private long _rowsRead;
    private long _longestStatementTicks;
    private long _cancelRequestedTimestamp;
    private int _ended;

    internal OperationWatch(OperationMonitor monitor, string action, OperationWatch? parent)
    {
        _monitor = monitor;
        Action = action;
        Parent = parent;
        Context = ExecutionContext.Capture();
        StartTimestamp = Stopwatch.GetTimestamp();
        StartCpu = Environment.CpuUsage.TotalTime;
        LastHeartbeatCpu = StartCpu;
    }

    internal string Action { get; }

    internal OperationWatch? Parent { get; }

    /// <summary>操作開始時的日誌 scope（correlation_id、action 等），計時器在別的執行緒寫紀錄時沿用。</summary>
    internal ExecutionContext? Context { get; }

    internal long StartTimestamp { get; }

    internal TimeSpan StartCpu { get; }

    /// <summary>計時器上一次記 action.still_running 的時間與當時的處理器時間；還沒記過時是 0。</summary>
    internal long LastHeartbeatTimestamp;

    internal TimeSpan LastHeartbeatCpu;

    internal int Heartbeats;

    /// <summary>匯出進度的節流狀態，只在發布進度的執行緒上讀寫。</summary>
    internal ExportProgressThrottle ExportProgress { get; } = new();

    internal long Statements => Interlocked.Read(ref _statements);

    internal long SlowStatements => Interlocked.Read(ref _slowStatements);

    internal long FailedStatements => Interlocked.Read(ref _failedStatements);

    internal long SqlTicks => Interlocked.Read(ref _sqlTicks);

    internal long RowsRead => Interlocked.Read(ref _rowsRead);

    internal long LongestStatementTicks => Interlocked.Read(ref _longestStatementTicks);

    /// <summary>要求取消的時間；沒有要求過時是 0。</summary>
    internal long CancelRequestedTimestamp => Interlocked.Read(ref _cancelRequestedTimestamp);

    internal int StagesEntered => Volatile.Read(ref _stagesEntered);

    internal string? CurrentStage => Volatile.Read(ref _currentStage);

    /// <summary>開始前就已經要求取消；這種操作沒有執行過程，不算執行中要求取消。</summary>
    internal bool CancelledBeforeStart { get; private set; }

    internal void WatchCancellation(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        if (cancellationToken.IsCancellationRequested)
        {
            CancelledBeforeStart = true;
            return;
        }

        if (cancellationToken.CanBeCanceled)
        {
            // 取消回呼只記下時間，不做其他事。
            _cancelRegistration = cancellationToken.UnsafeRegister(
                static state => ((OperationWatch)state!).CancelRequested(),
                this);
        }
    }

    /// <summary>
    /// 取消回呼依登記的相反順序執行，這筆在操作開始時登記，排在最後。操作若在取消的執行緒上直接結束，
    /// 結束時回呼可能還沒輪到就被移除；所以計時器每次檢查與操作結束時，再看一次是否已經要求取消。
    /// </summary>
    internal void ObserveCancellation()
    {
        if (!CancelledBeforeStart && _cancellationToken.IsCancellationRequested)
        {
            CancelRequested();
        }
    }

    internal void StageEntered(string stage)
    {
        long offset;
        lock (_gate)
        {
            offset = ElapsedMilliseconds(Stopwatch.GetTimestamp());
            if (_headStages.Count < HeadStages)
            {
                _headStages.Add((stage, offset));
            }
            else
            {
                if (_tailStages.Count == TailStages)
                {
                    _tailStages.Dequeue();
                }

                _tailStages.Enqueue((stage, offset));
            }

            _stagesEntered++;
            _currentStage = stage;
        }

        _monitor.LogStageEntered(this, stage, offset);
    }

    internal void StatementStarted() => Interlocked.Increment(ref _statements);

    internal void AddSqlTicks(long ticks) => Interlocked.Add(ref _sqlTicks, ticks);

    internal void AddRowRead() => Interlocked.Increment(ref _rowsRead);

    internal void StatementEnded(string sql, long sqlTicks, long rowsRead, bool slow, bool failed)
    {
        if (slow)
        {
            Interlocked.Increment(ref _slowStatements);
        }

        if (failed)
        {
            Interlocked.Increment(ref _failedStatements);
        }

        lock (_gate)
        {
            if (sqlTicks > _longestStatementTicks)
            {
                _longestStatementTicks = sqlTicks;
            }

            // 批量寫入每一列都用同一個命令執行同一段 SQL；和上一條是同一個字串時不再查表，省下整段 SQL 的雜湊。
            SqlTextStats stats;
            if (ReferenceEquals(sql, _lastSql) && _lastSqlStats is not null)
            {
                stats = _lastSqlStats;
            }
            else
            {
                var key = _sqlTexts.ContainsKey(sql) || _sqlTexts.Count < MaxSqlTexts ? sql : OtherSqlTexts;
                ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_sqlTexts, key, out _);
                slot ??= new SqlTextStats();
                stats = slot;
                _lastSql = sql;
                _lastSqlStats = stats;
            }

            stats.Count++;
            stats.Ticks += sqlTicks;
            stats.MaxTicks = Math.Max(stats.MaxTicks, sqlTicks);
            stats.Rows += rowsRead;
        }
    }

    internal long ElapsedMilliseconds(long nowTimestamp) =>
        (long)Stopwatch.GetElapsedTime(StartTimestamp, nowTimestamp).TotalMilliseconds;

    /// <summary>
    /// 步驟路徑：每個步驟後面接開始的時間點，例如 report_plan@0ms &gt; report_write@1203ms。
    /// 步驟太多或路徑超過 <see cref="MaxStagePathLength"/> 時，保留最前面與最後面的步驟，
    /// 中間寫成 (+N stages omitted)；最後一個步驟一定留著，看得出操作最後停在哪裡。
    /// </summary>
    internal string StagePath()
    {
        List<string> head;
        List<string> tail;
        int omitted;
        lock (_gate)
        {
            head = _headStages.Select(Segment).ToList();
            tail = _tailStages.Select(Segment).ToList();
            omitted = _stagesEntered - head.Count - tail.Count;
        }

        var path = JoinStages(head, omitted, tail);
        while (path.Length > MaxStagePathLength && head.Count + tail.Count > 1)
        {
            // 從中間拿掉：先拿後段最前面的步驟，後段只剩最後一個時再拿前段最後面的步驟。
            if (tail.Count == 0)
            {
                // 步驟都在前段時，先把最後一個移到後段，省略的標記才會排在它前面。
                tail.Add(head[^1]);
                head.RemoveAt(head.Count - 1);
            }

            if (tail.Count > 1)
            {
                tail.RemoveAt(0);
            }
            else
            {
                head.RemoveAt(head.Count - 1);
            }

            omitted++;
            path = JoinStages(head, omitted, tail);
        }

        return path;

        static string Segment((string Stage, long OffsetMilliseconds) item) => $"{item.Stage}@{item.OffsetMilliseconds}ms";
    }

    private static string JoinStages(List<string> head, int omitted, List<string> tail) =>
        omitted > 0
            ? string.Join(" > ", head.Append($"(+{omitted} stages omitted)").Concat(tail))
            : string.Join(" > ", head.Concat(tail));

    /// <summary>花在 SQLite 裡最久的幾種語句，依合計時間排序；只寫進診斷日誌。</summary>
    internal string TopSql(int count, Func<long, long> toMilliseconds, Func<string, string> shorten)
    {
        lock (_gate)
        {
            return string.Join(
                "\n",
                _sqlTexts
                    .OrderByDescending(pair => pair.Value.Ticks)
                    .Take(count)
                    .Select(pair => $"count={pair.Value.Count} total_ms={toMilliseconds(pair.Value.Ticks)} "
                        + $"max_ms={toMilliseconds(pair.Value.MaxTicks)} rows={pair.Value.Rows} sql={shorten(pair.Key)}"));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
        {
            return;
        }

        try
        {
            ObserveCancellation();
            _cancelRegistration.Dispose();
        }
        catch (Exception)
        {
            // 監看只做記錄，解除取消登記失敗也照常寫摘要。
        }

        _monitor.OperationEnded(this);
    }

    private void CancelRequested() =>
        Interlocked.CompareExchange(ref _cancelRequestedTimestamp, Stopwatch.GetTimestamp(), 0);

    private sealed class SqlTextStats
    {
        public long Count;
        public long Ticks;
        public long MaxTicks;
        public long Rows;
    }
}

/// <summary>匯出進度的節流：換階段、換工作表或完成一張工作表時記一筆，其餘最多每隔一段時間記一筆。</summary>
internal sealed class ExportProgressThrottle
{
    private readonly Lock _gate = new();
    private string? _phase;
    private string? _sheetName;
    private long _sheetsCompleted = -1;
    private long _lastLoggedTimestamp;

    internal bool ShouldLog(string? phase, string? sheetName, long sheetsCompleted, long nowTimestamp, TimeSpan interval)
    {
        lock (_gate)
        {
            var changed = _lastLoggedTimestamp == 0
                || !string.Equals(phase, _phase, StringComparison.Ordinal)
                || !string.Equals(sheetName, _sheetName, StringComparison.Ordinal)
                || sheetsCompleted != _sheetsCompleted;
            if (!changed && Stopwatch.GetElapsedTime(_lastLoggedTimestamp, nowTimestamp) < interval)
            {
                return false;
            }

            _phase = phase;
            _sheetName = sheetName;
            _sheetsCompleted = sheetsCompleted;
            _lastLoggedTimestamp = nowTimestamp;
            return true;
        }
    }
}
