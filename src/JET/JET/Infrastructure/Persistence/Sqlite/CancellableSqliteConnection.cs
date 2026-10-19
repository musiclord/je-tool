using System.Data;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace JET.Infrastructure;

/// <summary>
/// 讓 SQLite 執行中的語句可以取消。Microsoft.Data.Sqlite 的 <see cref="SqliteCommand.Cancel"/> 不做任何事，
/// 非同步執行也只在開始前檢查一次取消，長語句要跑完才停（2026-10-05 最後獨立複審實測 54.7 秒）。
/// 這條連線建立的命令改用 <see cref="CancellableSqliteCommand"/>；設定與基底的 <c>CreateCommand</c> 相同。
/// </summary>
internal sealed class CancellableSqliteConnection(string connectionString) : SqliteConnection(connectionString)
{
    public override SqliteCommand CreateCommand() => new CancellableSqliteCommand
    {
        Connection = this,
        CommandTimeout = DefaultTimeout,
        Transaction = Transaction
    };
}

/// <summary>
/// 執行期間把取消接到 <c>sqlite3_interrupt</c>：SQLite 在最近的檢查點中止目前語句，交易內被中斷的寫入由 SQLite
/// 整筆回復；中斷錯誤轉成 <see cref="OperationCanceledException"/>，和 DuckDB 一樣回報成取消
/// （2026-10-19 跨資料庫一致性第二階段）。取消只在執行呼叫期間登記，呼叫返回前解除，
/// 不會中斷之後在同一條連線上執行的其他語句。
/// </summary>
internal sealed class CancellableSqliteCommand : SqliteCommand
{
    private const int SqliteInterrupt = 9;

    /// <summary>sqlite3_interrupt 可以從其他執行緒呼叫，但連線在呼叫期間必須保持開啟。</summary>
    public override void Cancel()
    {
        if (Connection is { State: ConnectionState.Open } connection)
        {
            raw.sqlite3_interrupt(connection.Handle);
        }
    }

    public override Task<SqliteDataReader> ExecuteReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reader = ExecuteWithInterrupt(() => ExecuteReader(behavior), cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            // 取消在第一步剛完成時才送達，讀取器的語句仍帶著中斷旗標；不交出這個讀取器，直接回報取消。
            DisposeAfterInterrupt(reader);
            throw new OperationCanceledException(cancellationToken);
        }

        return Task.FromResult(reader);
    }

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<int>(cancellationToken);
        }

        try
        {
            return Task.FromResult(ExecuteWithInterrupt(ExecuteNonQuery, cancellationToken));
        }
        catch (Exception exception)
        {
            return Task.FromException<int>(exception);
        }
    }

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<object?>(cancellationToken);
        }

        try
        {
            return Task.FromResult(ExecuteWithInterrupt(ExecuteScalar, cancellationToken));
        }
        catch (Exception exception)
        {
            return Task.FromException<object?>(exception);
        }
    }

    private T ExecuteWithInterrupt<T>(Func<T> execute, CancellationToken cancellationToken)
    {
        // 解除登記時會等執行中的回呼結束，所以這個方法返回後不會再有中斷送到這條連線。
        using var registration = cancellationToken.Register(
            static state => ((CancellableSqliteCommand)state!).Cancel(),
            this);
        try
        {
            return execute();
        }
        catch (SqliteException exception) when (
            exception.SqliteErrorCode == SqliteInterrupt && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("作業已取消。", exception, cancellationToken);
        }
    }

    private static void DisposeAfterInterrupt(SqliteDataReader reader)
    {
        try
        {
            reader.Dispose();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == SqliteInterrupt)
        {
            // 關閉時剩下的語句也被同一次取消中斷；結果本來就不再使用。
        }
    }
}
