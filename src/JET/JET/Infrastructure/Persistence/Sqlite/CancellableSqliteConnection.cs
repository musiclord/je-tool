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
/// 整筆回復；中斷錯誤轉成 <see cref="OperationCanceledException"/>，和 DuckDB 一樣回報成取消。
/// 取消只在執行呼叫期間登記，呼叫返回前解除，
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

    /// <summary>
    /// 經由 <see cref="System.Data.Common.DbCommand"/> 取得的讀取器：逐列讀取時也接上中斷，見 <see cref="CancellableSqliteDataReader"/>。
    /// 本地 repository 都經這個入口讀資料。
    /// </summary>
    protected override async Task<System.Data.Common.DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken) =>
        new CancellableSqliteDataReader(await ExecuteReaderAsync(behavior, cancellationToken), this);

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

    internal T ExecuteWithInterrupt<T>(Func<T> execute, CancellationToken cancellationToken)
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

    internal static void DisposeAfterInterrupt(SqliteDataReader reader)
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

/// <summary>
/// 逐列讀取時也能取消：讀下一列的期間把取消接到 <c>sqlite3_interrupt</c>，和執行語句時相同，讀完就解除。
/// 原本只有第一步可以中斷，之後每讀一列只在開始前檢查一次取消；下一列要掃過很多資料才出來時，
/// 按取消後要等到它出來才停（第 22 步盤點的第 7 項）。其餘成員原樣交給 SQLite 的讀取器。
/// </summary>
internal sealed class CancellableSqliteDataReader(SqliteDataReader inner, CancellableSqliteCommand command)
    : System.Data.Common.DbDataReader
{
    private bool interrupted;

    public override Task<bool> ReadAsync(CancellationToken cancellationToken) =>
        StepAsync(inner.Read, cancellationToken);

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) =>
        StepAsync(inner.NextResult, cancellationToken);

    private Task<bool> StepAsync(Func<bool> step, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<bool>(cancellationToken);
        }

        try
        {
            var advanced = command.ExecuteWithInterrupt(step, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                // 取消在這一列剛讀完時才送達，語句仍帶著中斷旗標；不交出這一列，直接回報取消。
                interrupted = true;
                return Task.FromCanceled<bool>(cancellationToken);
            }

            return Task.FromResult(advanced);
        }
        catch (OperationCanceledException exception)
        {
            interrupted = true;
            return Task.FromException<bool>(exception);
        }
        catch (Exception exception)
        {
            return Task.FromException<bool>(exception);
        }
    }

    public override bool Read() => inner.Read();

    public override bool NextResult() => inner.NextResult();

    public override int Depth => inner.Depth;

    public override int FieldCount => inner.FieldCount;

    public override int VisibleFieldCount => inner.VisibleFieldCount;

    public override bool HasRows => inner.HasRows;

    public override bool IsClosed => inner.IsClosed;

    public override int RecordsAffected => inner.RecordsAffected;

    public override object this[int ordinal] => inner[ordinal];

    public override object this[string name] => inner[name];

    public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);

    public override byte GetByte(int ordinal) => inner.GetByte(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);

    public override char GetChar(int ordinal) => inner.GetChar(ordinal);

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);

    public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);

    public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);

    public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);

    public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);

    public override System.Collections.IEnumerator GetEnumerator() => inner.GetEnumerator();

    public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);

    public override T GetFieldValue<T>(int ordinal) => inner.GetFieldValue<T>(ordinal);

    public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);

    public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);

    public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);

    public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);

    public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);

    public override string GetName(int ordinal) => inner.GetName(ordinal);

    public override int GetOrdinal(string name) => inner.GetOrdinal(name);

    public override DataTable? GetSchemaTable() => inner.GetSchemaTable();

    public override Stream GetStream(int ordinal) => inner.GetStream(ordinal);

    public override TextReader GetTextReader(int ordinal) => inner.GetTextReader(ordinal);

    public override string GetString(int ordinal) => inner.GetString(ordinal);

    public override object GetValue(int ordinal) => inner.GetValue(ordinal);

    public override int GetValues(object[] values) => inner.GetValues(values);

    public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);

    public override void Close() => inner.Close();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (interrupted)
            {
                CancellableSqliteCommand.DisposeAfterInterrupt(inner);
            }
            else
            {
                inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
