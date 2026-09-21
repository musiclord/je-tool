using System.Runtime.CompilerServices;
using System.Text;
using ExcelDataReader;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>唯讀 BIFF .xls，不啟動 Excel、不執行巨集、不建立全量 DataSet。</summary>
public sealed class BinaryExcelTableReader : ITabularFileReader
{
    public bool Supports(string filePath) => Path.GetExtension(filePath).Equals(".xls", StringComparison.OrdinalIgnoreCase);

    public Task<TabularFileInspection> InspectAsync(string filePath, CancellationToken cancellationToken)
    {
        using var reader = Open(filePath);
        var sheets = new List<WorksheetInspection>();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var columns = FindHeader(reader, 0, cancellationToken, out _);
            sheets.Add(new WorksheetInspection(reader.Name, columns, null));
        } while (Read(() => reader.NextResult()));
        return Task.FromResult(new TabularFileInspection("xls", sheets, null, null, null));
    }

    public Task<IReadOnlyList<string>> ReadColumnsAsync(TabularSourceRequest request, CancellationToken cancellationToken)
    {
        using var reader = Open(request.FilePath);
        SelectSheet(reader, request.SheetName, cancellationToken);
        var columns = FindHeader(reader, request.LeadingRowsToSkip, cancellationToken, out _);
        if (columns.Count == 0) throw Empty();
        return Task.FromResult(columns);
    }

    public async IAsyncEnumerable<StagingRow> ReadRowsAsync(TabularSourceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = Open(request.FilePath);
        SelectSheet(reader, request.SheetName, cancellationToken);
        var columns = FindHeader(reader, request.LeadingRowsToSkip, cancellationToken, out var rowNumber);
        if (columns.Count == 0) throw Empty();
        while (Read(() => reader.Read()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowNumber++;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var observations = new List<TabularCellObservation>();
            for (var column = 0; column < reader.FieldCount; column++)
            {
                var value = reader.GetValue(column);
                if (value is null or DBNull) continue;
                var format = reader.GetNumberFormatString(column);
                var cell = NativeTabularValue.Read(value, format is not null &&
                    ExcelDateFormatDetector.ClassifyFormatCode(format) == ExcelNumberKind.Time);
                if (cell.Text.Length == 0) continue;
                var name = columns[column];
                values[name] = cell.Text;
                observations.Add(new(name, cell.Kind, cell.Text.Length, cell.DecimalPlaces));
            }
            if (values.Count > 0) yield return new StagingRow(rowNumber, values, observations);
        }
        await Task.CompletedTask;
    }

    private static IReadOnlyList<string> FindHeader(IExcelDataReader reader, int skip, CancellationToken ct, out int row)
    {
        row = 0;
        while (Read(() => reader.Read()))
        {
            ct.ThrowIfCancellationRequested();
            row++;
            if (row <= skip) continue;
            var header = Enumerable.Range(0, reader.FieldCount)
                .Select(i => (ColumnNumber: i + 1, RawName: reader.GetValue(i) is { } value
                    ? NativeTabularValue.Read(value).Text : null)).ToArray();
            if (header.Any(cell => !string.IsNullOrWhiteSpace(cell.RawName))) return TabularHeaderNormalizer.Normalize(header);
        }
        return [];
    }

    private static void SelectSheet(IExcelDataReader reader, string? sheetName, CancellationToken ct)
    {
        if (sheetName is null) return;
        do
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(reader.Name, sheetName, StringComparison.OrdinalIgnoreCase)) return;
        } while (Read(() => reader.NextResult()));
        throw new JetActionException(JetErrorCodes.SheetNotFound, "選取的工作表已不存在，請重新選擇來源檔與工作表。");
    }

    private static IExcelDataReader Open(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Stream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ExcelReaderFactory.CreateBinaryReader(stream);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            stream?.Dispose();
            throw Failure(exception);
        }
    }

    private static bool Read(Func<bool> read)
    {
        try { return read(); }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        { throw Failure(exception); }
    }

    private static JetActionException Empty() => new(JetErrorCodes.EmptyWorkbook, "工作表沒有標頭或資料，請重新選擇工作表。");
    private static JetActionException Failure(Exception exception) => new(JetErrorCodes.FileReadError,
        "無法讀取 .xls。請確認檔案可正常開啟且未加密，或在 Excel 另存為 .xlsx 後重試。", innerException: exception);
}
