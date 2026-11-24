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
        if (columns.Count == 0) throw NoHeader(request);
        return Task.FromResult(columns);
    }

    public async IAsyncEnumerable<StagingRow> ReadRowsAsync(TabularSourceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = Open(request.FilePath);
        SelectSheet(reader, request.SheetName, cancellationToken);
        var columns = FindHeader(reader, request.LeadingRowsToSkip, cancellationToken, out var rowNumber);
        if (columns.Count == 0) throw NoHeader(request);
        while (Read(() => reader.Read()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowNumber++;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var observations = new List<TabularCellObservation>();
            for (var column = 0; column < reader.FieldCount; column++)
            {
                var cell = ReadCell(reader, column);
                if (cell is null || cell.Value.Text.Length == 0) continue;
                var name = columns[column];
                values[name] = cell.Value.Text;
                observations.Add(new(name, cell.Value.Kind, cell.Value.Text.Length, cell.Value.DecimalPlaces));
            }
            if (values.Count > 0) yield return new StagingRow(rowNumber, values, observations);
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// 錯誤儲存格（#N/A、#REF! 等）保留 Excel 顯示的原文，和 Open XML 讀取器相同；金額欄因此會以金額無效明確失敗，
    /// 不會被讀成空白後變成 0。
    /// </summary>
    private static (string Text, LegacyFieldKind Kind, int? DecimalPlaces)? ReadCell(IExcelDataReader reader, int column)
    {
        var value = reader.GetValue(column);
        if (value is null or DBNull)
        {
            return reader.GetCellError(column) is { } cellError
                ? (ErrorText(cellError), LegacyFieldKind.Text, null)
                : null;
        }
        var format = reader.GetNumberFormatString(column);
        var timeOnly = format is not null && ExcelDateFormatDetector.ClassifyFormatCode(format) == ExcelNumberKind.Time;
        if (value is DateTime date && !timeOnly && !DateNormalizer.IsReadableExcelDate(date))
        {
            // ExcelDataReader 換算 1900 日期系統時，把序列值 0 到 59 先加 1 再換成日期，所以 1899-12-31 來自序列值 0，
            // Excel 顯示成 1900 年 1 月 0 日；照 Excel 顯示的年月日存下來，確認配對時列為日曆上沒有這一天。
            // 這個套件換算 1904 日期系統時先加 1,462，序列值 -1461 以上、-1460 未滿也會換出這一天；
            // 套件不公開日期系統，這段負數同樣存成 1900-01-00，和 .xlsx 存下的序列值文字不同。
            // 序列值 60 在 Excel 顯示成日曆上沒有的 1900-02-29，這個套件換出的卻是和 59 同一天的 1900-02-28，讀取器分不出來。
            if (date.Date == new DateTime(1899, 12, 31))
            {
                return (ExcelSerialDate.DayZero, LegacyFieldKind.Date, null);
            }

            // Excel 的日期不會早於 1900 年；換出更早的日期時不寫成 yyyy-MM-dd，改交出序列值的文字，
            // 確認配對時這個值列為無法辨識的日期。
            // ExcelDataReader 已經處理過 1904 日期系統的位移，所以直接用 ToOADate 的值。
            // 這個限制寫在這裡，不寫進 NativeTabularValue，Access 的日期才不會受同一個限制。
            return (DateNormalizer.ExcelSerialText(date.ToOADate()), LegacyFieldKind.Date, null);
        }

        // 日期格式的儲存格換不成日期時，例如晚於 9999-12-31，ExcelDataReader 交回原本的數字；
        // 和 .xlsx 一樣交出序列值的文字，確認配對時列為無法辨識的日期。
        if (value is double serial && format is not null
            && ExcelDateFormatDetector.ClassifyFormatCode(format) == ExcelNumberKind.Date)
        {
            return (DateNormalizer.ExcelSerialText(serial), LegacyFieldKind.Date, null);
        }

        return NativeTabularValue.Read(value, timeOnly);
    }

    private static string ErrorText(CellError error) => error switch
    {
        CellError.NULL => "#NULL!",
        CellError.DIV0 => "#DIV/0!",
        CellError.VALUE => "#VALUE!",
        CellError.REF => "#REF!",
        CellError.NAME => "#NAME?",
        CellError.NUM => "#NUM!",
        CellError.NA => "#N/A",
        CellError.GETTING_DATA => "#GETTING_DATA",
        _ => "#ERROR"
    };

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
            stream = SourceFileAccess.OpenRead(path);
            return ExcelReaderFactory.CreateBinaryReader(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            throw SourceFileErrors.CannotOpen(path, exception);
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

    private static JetActionException NoHeader(TabularSourceRequest request) =>
        request.LeadingRowsToSkip > 0 && !request.FixedLeadingRows
            ? SourceFileErrors.HeaderRowBeyondContent(request.FilePath, request.LeadingRowsToSkip) : Empty();

    private static JetActionException Empty() => new(JetErrorCodes.EmptyWorkbook, "工作表沒有標頭或資料，請重新選擇工作表。");
    private static JetActionException Failure(Exception exception) => new(JetErrorCodes.FileReadError,
        "無法讀取 .xls。請確認檔案可正常開啟且未加密，或在 Excel 另存為 .xlsx 後重試。", innerException: exception);
}
