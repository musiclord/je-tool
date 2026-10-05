using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>不依賴 Office 的讀取邊界測試；實際 ACE 相容性另由 NativeAccessImportAcceptance 驗證。</summary>
public sealed class AccessTableReaderTests
{
    [Theory]
    [InlineData("source.mdb", true)]
    [InlineData("source.ACCDB", true)]
    [InlineData("source.xlsx", false)]
    public void Supports_OnlyAccessContainers(string path, bool expected) =>
        Assert.Equal(expected, new AccessTableReader().Supports(path));

    [Fact]
    public async Task Inspection_ListsOrdinaryTablesOnly_AndNeverReadsRows()
    {
        using var data = Data();
        var connection = new SourceConnection(data);
        connection.TableEntries.AddRange([("Saved query", "VIEW"), ("External", "LINK"), ("MSysObjects", "TABLE")]);
        var result = await Reader(connection).InspectAsync("source.accdb", CancellationToken.None);

        var table = Assert.Single(result.Worksheets!);
        Assert.Equal("GL data", table.Name);
        Assert.Equal(["Account", "Amount", "Date", "Manual", "Description"], table.Columns);
        Assert.Null(table.RowCountEstimate);
        Assert.Equal("SELECT * FROM [GL data] WHERE 1 = 0", Assert.Single(connection.Commands).CommandText);
        Assert.Equal(0, connection.ReadCalls);
        Assert.True(connection.WasDisposed);
    }

    [Fact]
    public async Task Rows_PreserveTextIdentifiersAndNativeValues()
    {
        using var data = Data();
        data.Rows.Add("001", 12.3400m, new DateTime(2025, 3, 1), true, DBNull.Value);
        data.Rows.Add("002", -12.3400m, new DateTime(2025, 3, 2), false, "合成摘要");
        var connection = new SourceConnection(data);
        var rows = await CollectAsync(Reader(connection));

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0].SourceRowNumber);
        Assert.Equal("001", rows[0].Values["Account"]);
        Assert.Equal("12.3400", rows[0].Values["Amount"]);
        Assert.Equal("2025-03-01", rows[0].Values["Date"]);
        // 2026-10-04 第二遍回饋審閱 L09：是或否欄位以前讀成「1」「0」，.xlsx 讀成「true」「false」，同一份資料換格式匯入值會不同。
        // 依計畫對齊 .xlsx 的表示方式，所以把原本斷言的「1」「0」改成「true」「false」。
        Assert.Equal("true", rows[0].Values["Manual"]);
        Assert.False(rows[0].Values.ContainsKey("Description"));
        Assert.Equal("false", rows[1].Values["Manual"]);
        Assert.Equal("-12.3400", rows[1].Values["Amount"]);
        Assert.Equal("合成摘要", rows[1].Values["Description"]);
        Assert.Equal(CommandBehavior.SequentialAccess, Assert.Single(connection.Commands).Behavior);
        Assert.True(connection.WasDisposed);
        Assert.True(connection.ReaderDisposed);
    }

    // 2026-10-04 第二遍回饋審閱第 2 批（C3，L17）：Access 讀出的文字以前不去空白，.csv 與 .xlsx 會去，同一份資料換格式答案不同。
    [Fact]
    public async Task Rows_TrimTextLikeTheOtherReaders_AndTreatWhitespaceOnlyAsBlank()
    {
        using var data = Data();
        data.Rows.Add(" 001　", 1m, new DateTime(2025, 3, 1), true, " 合成摘要	");
        data.Rows.Add("	002 ", 2m, new DateTime(2025, 3, 2), false, "   ");
        var rows = await CollectAsync(Reader(new SourceConnection(data)));

        Assert.Equal("001", rows[0].Values["Account"]);
        Assert.Equal("合成摘要", rows[0].Values["Description"]);
        Assert.Equal("002", rows[1].Values["Account"]);
        Assert.False(rows[1].Values.ContainsKey("Description"));
    }

    [Fact]
    public async Task SelectedTable_MustBeAnOrdinaryTableFromSchema()
    {
        using var data = Data();
        var connection = new SourceConnection(data);
        connection.TableEntries.Add(("Saved query", "VIEW"));
        var error = await Assert.ThrowsAsync<JetActionException>(() => Reader(connection).ReadColumnsAsync(
            new("source.mdb", "Saved query"), CancellationToken.None));

        Assert.Equal(JetErrorCodes.SheetNotFound, error.Code);
        Assert.Empty(connection.Commands);
        Assert.True(connection.WasDisposed);
    }

    [Fact]
    public async Task CancelledBeforeOpen_DoesNotCreateConnection()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        var reader = new AccessTableReader(_ => { calls++; throw new InvalidOperationException(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.InspectAsync("source.mdb", cancellation.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CancelledBetweenRows_DoesNotReadAnotherNativeRow()
    {
        using var data = Data();
        data.Rows.Add("001", 1m, new DateTime(2025, 3, 1), true, "first");
        using var cancellation = new CancellationTokenSource();
        var connection = new SourceConnection(data);
        await using var rows = Reader(connection).ReadRowsAsync(new("source.accdb"), cancellation.Token).GetAsyncEnumerator();
        Assert.True(await rows.MoveNextAsync());
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await rows.MoveNextAsync(); });
        Assert.Equal(1, connection.ReadCalls);
        Assert.True(Assert.Single(connection.Commands).CancelCalls > 0);
    }

    [Theory]
    [InlineData("execute")]
    [InlineData("read")]
    public async Task CancellationDuringNativeCall_IsNotReportedAsUnreadableFile(string stage)
    {
        using var data = Data();
        using var cancellation = new CancellationTokenSource();
        var connection = new SourceConnection(data);
        Action cancel = () => { cancellation.Cancel(); throw new SourceException("cancelled native operation"); };
        if (stage == "execute") connection.Executing = cancel;
        else connection.Reading = cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(Reader(connection), cancellation.Token));
        Assert.True(Assert.Single(connection.Commands).CancelCalls > 0);
        Assert.True(connection.WasDisposed);
    }

    [Fact]
    public async Task CancellationAsNativeReaderIsReturned_ReleasesUnclaimedReader()
    {
        using var data = Data();
        using var cancellation = new CancellationTokenSource();
        var connection = new SourceConnection(data) { Executing = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(Reader(connection), cancellation.Token));
        Assert.True(connection.ReaderDisposed);
        Assert.True(connection.WasDisposed);
        Assert.True(Assert.Single(connection.Commands).WasDisposed);
    }

    [Fact]
    public async Task CancellationAsConnectionOpens_ReleasesConnectionWithoutQuerying()
    {
        using var data = Data();
        using var cancellation = new CancellationTokenSource();
        var connection = new SourceConnection(data) { Opening = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader(connection).InspectAsync("source.accdb", cancellation.Token));
        Assert.True(connection.WasDisposed);
        Assert.Empty(connection.Commands);
    }

    [Fact]
    public async Task CellReadFailure_PreservesNativeCauseAndAccessContext()
    {
        using var data = Data();
        data.Rows.Add("001", 1m, new DateTime(2025, 3, 1), true, "first");
        var cause = new SourceException("synthetic value failure");
        var connection = new SourceConnection(data) { ReadingValue = () => throw cause };

        var error = await Assert.ThrowsAsync<JetActionException>(() => CollectAsync(Reader(connection)));
        Assert.Equal(JetErrorCodes.FileReadError, error.Code);
        Assert.Contains("Access", error.Message);
        Assert.Same(cause, error.InnerException);
        Assert.True(connection.WasDisposed);
        Assert.True(connection.ReaderDisposed);
    }

    [Fact]
    public async Task CancellationAsLastCellReturns_DoesNotYieldThePartlyReadRow()
    {
        using var data = Data();
        data.Rows.Add("001", 1m, new DateTime(2025, 3, 1), true, "last cell");
        using var cancellation = new CancellationTokenSource();
        var connection = new SourceConnection(data)
        {
            AfterReadingValue = ordinal => { if (ordinal == 4) cancellation.Cancel(); }
        };
        await using var rows = Reader(connection).ReadRowsAsync(new("source.accdb"), cancellation.Token).GetAsyncEnumerator();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await rows.MoveNextAsync(); });
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, connection.ReadCalls);
        Assert.True(connection.ReaderDisposed);
        Assert.True(connection.WasDisposed);
    }

    [Fact]
    public async Task PreviewEarlyExit_ReleasesReaderCommandAndConnection()
    {
        using var data = Data();
        data.Rows.Add("001", 1m, new DateTime(2025, 3, 1), true, "first");
        data.Rows.Add("002", 2m, new DateTime(2025, 3, 2), false, "second");
        var connection = new SourceConnection(data);
        await foreach (var unused in Reader(connection).ReadRowsAsync(new("source.accdb"), CancellationToken.None)) break;

        Assert.Equal(1, connection.ReadCalls);
        Assert.True(connection.ReaderDisposed);
        Assert.True(connection.WasDisposed);
        Assert.True(Assert.Single(connection.Commands).WasDisposed);
    }

    [Fact]
    public async Task OpenFailure_PreservesCauseAndReleasesConnection()
    {
        using var data = Data();
        var cause = new InvalidOperationException("synthetic connection unavailable");
        var connection = new SourceConnection(data) { Opening = () => throw cause };
        var error = await Assert.ThrowsAsync<JetActionException>(() => Reader(connection).InspectAsync("source.accdb", CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, error.Code);
        Assert.Same(cause, error.InnerException);
        Assert.True(connection.WasDisposed);
    }

    private static AccessTableReader Reader(SourceConnection connection) => new(_ => connection);
    private static DataTable Data()
    {
        var data = new DataTable();
        data.Columns.Add("Account", typeof(string));
        data.Columns.Add("Amount", typeof(decimal));
        data.Columns.Add("Date", typeof(DateTime));
        data.Columns.Add("Manual", typeof(bool));
        data.Columns.Add("Description", typeof(string));
        return data;
    }

    private static async Task<List<StagingRow>> CollectAsync(AccessTableReader reader, CancellationToken token = default)
    {
        var rows = new List<StagingRow>();
        await foreach (var row in reader.ReadRowsAsync(new("source.accdb"), token)) rows.Add(row);
        return rows;
    }

    private sealed class SourceException(string message) : DbException(message);

    private sealed class SourceConnection(DataTable data) : DbConnection
    {
        public readonly List<(string Name, string Type)> TableEntries = [("GL data", "TABLE")];
        public readonly List<SourceCommand> Commands = [];
        public Action? Opening, Executing, Reading, ReadingValue;
        public Action<int>? AfterReadingValue;
        public bool WasDisposed, ReaderDisposed;
        public int ReadCalls;
        [AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "Synthetic";
        public override string DataSource => "Synthetic";
        public override string ServerVersion => "1";
        public override ConnectionState State => WasDisposed ? ConnectionState.Closed : ConnectionState.Open;
        public override void Open() => Opening?.Invoke();
        public override void Close() { }
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand()
        {
            var command = new SourceCommand(this, data);
            Commands.Add(command);
            return command;
        }
        public override DataTable GetSchema(string collectionName)
        {
            Assert.Equal("Tables", collectionName);
            var result = new DataTable();
            result.Columns.Add("TABLE_NAME"); result.Columns.Add("TABLE_TYPE");
            foreach (var (name, type) in TableEntries) result.Rows.Add(name, type);
            return result;
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    private sealed class SourceCommand(SourceConnection connection, DataTable data) : DbCommand
    {
        public CommandBehavior Behavior;
        public int CancelCalls;
        public bool WasDisposed;
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        public override void Cancel() => CancelCalls++;
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            Behavior = behavior;
            connection.Executing?.Invoke();
            return new SourceReader(data.CreateDataReader(), connection);
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    private sealed class SourceReader(DbDataReader inner, SourceConnection connection) : DbDataReader
    {
        public override bool Read() { connection.ReadCalls++; connection.Reading?.Invoke(); return inner.Read(); }
        public override object GetValue(int ordinal)
        {
            connection.ReadingValue?.Invoke();
            var value = inner.GetValue(ordinal);
            connection.AfterReadingValue?.Invoke(ordinal);
            return value;
        }
        protected override void Dispose(bool disposing) { connection.ReaderDisposed = true; inner.Dispose(); base.Dispose(disposing); }
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));
        public override bool NextResult() => inner.NextResult();
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long offset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, offset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long offset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, offset, buffer, bufferOffset, length);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override IEnumerator GetEnumerator() => ((IEnumerable)inner).GetEnumerator();
    }
}
