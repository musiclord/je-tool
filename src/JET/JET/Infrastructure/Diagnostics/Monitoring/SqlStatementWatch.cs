using System.Data.Common;
using System.Diagnostics;

namespace JET.Infrastructure;

/// <summary>SQLite 命令在執行前向監看模組登記；回傳的紀錄跟著這條語句，直到執行完、讀取器關閉或出錯。</summary>
public interface ISqlStatementObserver
{
    SqlStatementWatch? Begin(DbCommand command);
}

/// <summary>正在 SQLite 裡做的事：執行語句，或讀下一列。</summary>
public enum SqlStep
{
    Execute,
    Read,
}

/// <summary>
/// 一條語句的監看紀錄：開始時間、目前是否在 SQLite 裡、累計花在 SQLite 裡的時間與讀到的列數。
/// 執行語句的執行緒寫入，監看模組的計時器只讀取；這裡的方法都不往外丟例外。
/// </summary>
public sealed class SqlStatementWatch
{
    private readonly OperationMonitor _monitor;
    private long _callStartedTimestamp;
    private int _step;
    private long _sqlTicks;
    private long _longestCallTicks;
    private long _rowsRead;
    private int _finished;

    internal SqlStatementWatch(OperationMonitor monitor, OperationWatch? operation, DbCommand command)
    {
        _monitor = monitor;
        Operation = operation;
        Command = command;
        Sql = command.CommandText ?? string.Empty;
        Context = ExecutionContext.Capture();
        StartTimestamp = Stopwatch.GetTimestamp();
    }

    internal OperationWatch? Operation { get; }

    internal DbCommand Command { get; }

    internal string Sql { get; }

    /// <summary>語句開始時的日誌 scope（correlation_id、action 等），計時器在別的執行緒寫紀錄時沿用。</summary>
    internal ExecutionContext? Context { get; }

    internal long StartTimestamp { get; }

    internal SqlStep Step => (SqlStep)Volatile.Read(ref _step);

    internal long SqlTicks => Interlocked.Read(ref _sqlTicks);

    internal long LongestCallTicks => Interlocked.Read(ref _longestCallTicks);

    internal long RowsRead => Interlocked.Read(ref _rowsRead);

    /// <summary>目前這次進入 SQLite 的開始時間；不在 SQLite 裡時是 0。</summary>
    internal long CallStartedTimestamp => Interlocked.Read(ref _callStartedTimestamp);

    /// <summary>計時器上一次記「還在跑」的時間，用來控制重複記錄的間隔。</summary>
    internal long LastSlowLogTimestamp;

    /// <summary>這條語句曾經被記成跑太久。</summary>
    internal int SlowLogged;

    /// <summary>計時器記「還在跑」時一併取得的參數值；語句跑完才變慢的，在完成時取得。</summary>
    internal IReadOnlyList<KeyValuePair<string, object?>>? CapturedParameters;

    internal void CallStarted(SqlStep step)
    {
        Volatile.Write(ref _step, (int)step);
        Interlocked.Exchange(ref _callStartedTimestamp, Stopwatch.GetTimestamp());
    }

    internal void CallEnded()
    {
        var started = Interlocked.Exchange(ref _callStartedTimestamp, 0);
        if (started == 0)
        {
            return;
        }

        var ticks = Stopwatch.GetTimestamp() - started;
        Interlocked.Add(ref _sqlTicks, ticks);
        if (ticks > Interlocked.Read(ref _longestCallTicks))
        {
            Interlocked.Exchange(ref _longestCallTicks, ticks);
        }

        Operation?.AddSqlTicks(ticks);
    }

    internal void RowRead()
    {
        Interlocked.Increment(ref _rowsRead);
        Operation?.AddRowRead();
    }

    /// <summary>語句執行完或讀取器關閉；已經記過完成或失敗的不再記。</summary>
    internal void Completed()
    {
        if (Interlocked.Exchange(ref _finished, 1) == 0)
        {
            _monitor.StatementFinished(this, exception: null, cancellationRequested: false);
        }
    }

    /// <summary>語句出錯或被取消；已經記過完成或失敗的不再記。回傳 false，讓呼叫端放在 exception filter 裡也不攔下例外。</summary>
    internal bool Failed(Exception exception, bool cancellationRequested)
    {
        if (Interlocked.Exchange(ref _finished, 1) == 0)
        {
            _monitor.StatementFinished(this, exception, cancellationRequested);
        }

        return false;
    }
}

/// <summary>命令類別呼叫監看模組的入口：沒有監看模組時什麼都不做，監看模組出錯時吞掉，不影響語句本身。</summary>
internal static class SqlStatementObservation
{
    public static SqlStatementWatch? Begin(ISqlStatementObserver? observer, DbCommand command)
    {
        if (observer is null)
        {
            return null;
        }

        try
        {
            return observer.Begin(command);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>供 <c>catch (...) when (Failed(...))</c> 使用：記下失敗後回傳 false，例外照原樣往外丟，堆疊不變。</summary>
    public static bool Failed(SqlStatementWatch? statement, Exception exception, CancellationToken cancellationToken)
    {
        statement?.Failed(exception, cancellationToken.IsCancellationRequested);
        return false;
    }
}
