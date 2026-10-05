using System.Data;
using System.Data.Common;
using System.Data.OleDb;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>使用既有 Office ACE 讀取本機 Access 一般資料表，不開啟 Access 視窗或執行查詢及巨集。</summary>
public sealed class AccessTableReader : ITabularFileReader
{
    private readonly Func<string, DbConnection> connect;
    public AccessTableReader() : this(CreateConnection) { }
    internal AccessTableReader(Func<string, DbConnection> connect) => this.connect = connect;
    public bool Supports(string filePath) => Path.GetExtension(filePath).ToLowerInvariant() is ".mdb" or ".accdb";

    public async Task<TabularFileInspection> InspectAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var connection = Open(filePath, cancellationToken);
        var tables = Tables(connection, cancellationToken);
        var sheets = new List<WorksheetInspection>();
        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = Command(connection, table, schemaOnly: true);
            using var cancellation = CancelCommandOnRequest(command, cancellationToken);
            using var reader = NativeCall(() => command.ExecuteReader(CommandBehavior.SequentialAccess), cancellationToken);
            sheets.Add(new(table, NativeCall(() => Columns(reader), cancellationToken), null));
        }
        return new TabularFileInspection("access", sheets, null, null, null);
    }

    public async Task<IReadOnlyList<string>> ReadColumnsAsync(TabularSourceRequest request, CancellationToken cancellationToken)
    {
        await using var connection = Open(request.FilePath, cancellationToken);
        using var command = Command(connection, SelectTable(connection, request.SheetName, cancellationToken), schemaOnly: true);
        using var cancellation = CancelCommandOnRequest(command, cancellationToken);
        using var reader = NativeCall(() => command.ExecuteReader(CommandBehavior.SequentialAccess), cancellationToken);
        return NativeCall(() => Columns(reader), cancellationToken);
    }

    public async IAsyncEnumerable<StagingRow> ReadRowsAsync(TabularSourceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = Open(request.FilePath, cancellationToken);
        using var command = Command(connection, SelectTable(connection, request.SheetName, cancellationToken), schemaOnly: false);
        using var cancellation = CancelCommandOnRequest(command, cancellationToken);
        using var reader = NativeCall(() => command.ExecuteReader(CommandBehavior.SequentialAccess), cancellationToken);
        var columns = NativeCall(() => Columns(reader), cancellationToken);
        var rowNumber = 1;
        while (Read(reader, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var observations = new List<TabularCellObservation>();
            for (var column = 0; column < reader.FieldCount; column++)
            {
                var value = ReadValue(reader, column, cancellationToken);
                if (value is null) continue;
                var cell = NativeTabularValue.Read(value);
                if (cell.Text.Length == 0) continue;
                values[columns[column]] = cell.Text;
                observations.Add(new(columns[column], cell.Kind, cell.Text.Length, cell.DecimalPlaces));
            }
            yield return new StagingRow(++rowNumber, values, observations);
        }
    }

    private DbConnection Open(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DbConnection? connection = null;
        try
        {
            connection = connect(path);
            connection.Open();
            ct.ThrowIfCancellationRequested();
            return connection;
        }
        catch (OperationCanceledException) { connection?.Dispose(); throw; }
        catch (Exception exception) when (exception is DbException or InvalidOperationException or ArgumentException)
        {
            connection?.Dispose();
            ct.ThrowIfCancellationRequested();
            throw Failure(exception);
        }
    }

    private static DbConnection CreateConnection(string path)
    {
        var builder = new OleDbConnectionStringBuilder
        {
            Provider = "Microsoft.ACE.OLEDB.16.0", DataSource = Path.GetFullPath(path), PersistSecurityInfo = false
        };
        builder["Mode"] = "Read";
        builder["OLE DB Services"] = -2;
        return new OleDbConnection(builder.ConnectionString);
    }

    private static string[] Tables(DbConnection connection, CancellationToken ct)
    {
        return NativeCall(() =>
        {
            using var schema = connection.GetSchema("Tables");
            return schema.Rows.Cast<DataRow>().Where(row =>
                string.Equals(Convert.ToString(row["TABLE_TYPE"]), "TABLE", StringComparison.OrdinalIgnoreCase))
                .Select(row => Convert.ToString(row["TABLE_NAME"])!)
                .Where(name => !string.IsNullOrWhiteSpace(name) && !name.StartsWith("MSys", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal).ToArray();
        }, ct);
    }

    private static string SelectTable(DbConnection connection, string? name, CancellationToken ct)
    {
        var tables = Tables(connection, ct);
        if (name is null && tables.Length > 0) return tables[0];
        return tables.FirstOrDefault(table => string.Equals(table, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new JetActionException(JetErrorCodes.SheetNotFound, "Access 中沒有選取的一般資料表，請重新選擇資料表。");
    }

    private static DbCommand Command(DbConnection connection, string table, bool schemaOnly)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM [{table.Replace("]", "]]", StringComparison.Ordinal)}]"
            + (schemaOnly ? " WHERE 1 = 0" : "");
        command.CommandTimeout = 30;
        return command;
    }

    private static T NativeCall<T>(Func<T> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var result = operation();
            // ExecuteReader 可能在取消後才回傳；此時尚未交給呼叫端的 reader 也必須釋放。
            if (ct.IsCancellationRequested && result is IDisposable disposable) disposable.Dispose();
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (DbException exception)
        {
            ct.ThrowIfCancellationRequested();
            throw Failure(exception);
        }
    }

    private static bool Read(DbDataReader reader, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var result = reader.Read();
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (DbException exception) { ct.ThrowIfCancellationRequested(); throw Failure(exception); }
    }

    private static object? ReadValue(DbDataReader reader, int ordinal, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var value = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
            ct.ThrowIfCancellationRequested();
            return value;
        }
        catch (DbException exception) { ct.ThrowIfCancellationRequested(); throw Failure(exception); }
    }

    // ACE 的取消是盡力而為；呼叫前後仍檢查 token，不能把使用者取消誤報為檔案損壞。
    private static CancellationTokenRegistration CancelCommandOnRequest(DbCommand command, CancellationToken ct) =>
        ct.Register(static state =>
        {
            try { ((DbCommand)state!).Cancel(); }
            catch (DbException) { }
            catch (InvalidOperationException) { }
        }, command);

    private static IReadOnlyList<string> Columns(DbDataReader reader) => TabularHeaderNormalizer.Normalize(
        Enumerable.Range(0, reader.FieldCount).Select(i => (i + 1, (string?)reader.GetName(i))).ToArray());

    private static JetActionException Failure(Exception exception) => new(JetErrorCodes.FileReadError,
        "無法讀取 Access 資料表。請確認檔案可開啟、未加密且未被獨占；關檔後重試。" +
        $"若 Access 可開啟但 JET 仍失敗，請匯出支援日誌，請 IT 確認既有 ACE 16 能供 {RuntimeInformation.ProcessArchitecture} 程式使用。" +
        "也可將資料表另存為 CSV 或 .xlsx 後匯入。",
        innerException: exception);
}
