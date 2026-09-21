using System.Data;
using System.Data.Common;
using System.Data.OleDb;
using System.Runtime.CompilerServices;
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
        var tables = Tables(connection);
        var sheets = new List<WorksheetInspection>();
        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = Command(connection, table, schemaOnly: true);
            using var reader = Execute(command);
            sheets.Add(new(table, Columns(reader), null));
        }
        return new TabularFileInspection("access", sheets, null, null, null);
    }

    public async Task<IReadOnlyList<string>> ReadColumnsAsync(TabularSourceRequest request, CancellationToken cancellationToken)
    {
        await using var connection = Open(request.FilePath, cancellationToken);
        using var command = Command(connection, SelectTable(connection, request.SheetName), schemaOnly: true);
        using var reader = Execute(command);
        return Columns(reader);
    }

    public async IAsyncEnumerable<StagingRow> ReadRowsAsync(TabularSourceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = Open(request.FilePath, cancellationToken);
        using var command = Command(connection, SelectTable(connection, request.SheetName), schemaOnly: false);
        using var reader = Execute(command);
        var columns = Columns(reader);
        var rowNumber = 1;
        while (Read(reader))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var observations = new List<TabularCellObservation>();
            for (var column = 0; column < reader.FieldCount; column++)
            {
                if (reader.IsDBNull(column)) continue;
                var cell = NativeTabularValue.Read(reader.GetValue(column));
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
        try { connection = connect(path); connection.Open(); return connection; }
        catch (Exception exception) when (exception is DbException or InvalidOperationException or ArgumentException)
        { connection?.Dispose(); throw Failure(exception); }
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

    private static string[] Tables(DbConnection connection)
    {
        try
        {
            using var schema = connection.GetSchema("Tables");
            return schema.Rows.Cast<DataRow>().Where(row =>
                string.Equals(Convert.ToString(row["TABLE_TYPE"]), "TABLE", StringComparison.OrdinalIgnoreCase))
                .Select(row => Convert.ToString(row["TABLE_NAME"])!)
                .Where(name => !string.IsNullOrWhiteSpace(name) && !name.StartsWith("MSys", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal).ToArray();
        }
        catch (DbException exception) { throw Failure(exception); }
    }

    private static string SelectTable(DbConnection connection, string? name)
    {
        var tables = Tables(connection);
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

    private static DbDataReader Execute(DbCommand command)
    {
        try { return command.ExecuteReader(CommandBehavior.SequentialAccess); }
        catch (DbException exception) { throw Failure(exception); }
    }

    private static bool Read(DbDataReader reader)
    {
        try { return reader.Read(); }
        catch (DbException exception) { throw Failure(exception); }
    }

    private static IReadOnlyList<string> Columns(DbDataReader reader) => TabularHeaderNormalizer.Normalize(
        Enumerable.Range(0, reader.FieldCount).Select(i => (i + 1, (string?)reader.GetName(i))).ToArray());

    private static JetActionException Failure(Exception exception) => new(JetErrorCodes.FileReadError,
        "無法讀取 Access 資料表。請確認檔案可開啟、未加密且未被獨占；關檔後重試。若仍失敗，請匯出支援日誌確認 Office 資料介面。",
        innerException: exception);
}
