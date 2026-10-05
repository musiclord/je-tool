using System.Runtime.CompilerServices;
using System.Text;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// CSV / .txt（內容為 CSV）讀取器。
/// - 編碼：EncodingDetector 確定性鏈（BOM → 嚴格 UTF-8 → Big5），request.EncodingName 可覆寫。
/// - 分隔符：CsvDialectDetector 引號感知取樣統計，request.Delimiter 可覆寫。
/// - 解析：<see cref="CsvRecordReader"/>，一般 CSV 讀法（欄位開頭的引號才是引號，中間的引號是一般字元；
///   引號沒有成對時明確失敗並寫出第幾列）。cell 一律字串、由投影階段解析。
/// - 標頭：略過開頭的空白列，第一個有內容的列是標頭（和 Excel 讀取器相同）；標頭自己讀再交給
///   TabularHeaderNormalizer，重複欄名走 _2/_3 正規化。
/// - 欄數：標頭範圍外有資料的欄合成 COL_n 佔位欄（和 Open XML 讀取器相同，有資料的欄絕不靜默丟棄）；
///   少欄視為空。
/// - SourceRowNumber 以記錄計（空白列也算一列，標頭之前的空白列也算）；引號內含換行時與實體行號可能偏移，屬已知限制。
/// </summary>
public sealed class CsvTableReader : ITabularFileReader
{
    private const char DefaultDelimiter = ',';

    public bool Supports(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase);
    }

    public Task<IReadOnlyList<string>> ReadColumnsAsync(TabularSourceRequest request, CancellationToken cancellationToken)
    {
        var dialect = ResolveDialect(request);
        using var textReader = OpenTextReader(request.FilePath, dialect.Encoding);
        var records = new CsvRecordReader(textReader, dialect.Delimiter);
        var header = ReadHeader(records, request.FilePath, dialect.Encoding);
        return Task.FromResult(header.Columns);
    }

    public Task<TabularFileInspection> InspectAsync(string filePath, CancellationToken cancellationToken)
    {
        // 檢視回報的是「偵測鏈的判定結果」（不吃覆寫參數）：
        // 精靈把這些值直接帶回 import.*.fromFile 的 encoding/delimiter 覆寫欄位
        var encoding = EncodingDetector.Detect(filePath);
        var sampleText = ReadSampleText(filePath, encoding);

        if (string.IsNullOrWhiteSpace(sampleText))
        {
            throw EmptyFile(filePath);
        }

        var detected = CsvDialectDetector.DetectDelimiter(sampleText);

        using var textReader = OpenTextReader(filePath, encoding);
        var records = new CsvRecordReader(textReader, detected ?? DefaultDelimiter);
        var header = ReadHeader(records, filePath, encoding);

        return Task.FromResult(new TabularFileInspection(
            FileType: "csv",
            Worksheets: null,
            Columns: header.Columns,
            Encoding: EncodingDetector.WireNameOf(encoding),
            Delimiter: detected?.ToString(),
            Notices: header.Columns.Count == 1 ? [TabularSourceNotices.SingleColumn] : null));
    }

    public async IAsyncEnumerable<StagingRow> ReadRowsAsync(
        TabularSourceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var dialect = ResolveDialect(request);
        using var textReader = OpenTextReader(request.FilePath, dialect.Encoding);
        var records = new CsvRecordReader(textReader, dialect.Delimiter);
        var header = ReadHeader(records, request.FilePath, dialect.Encoding);
        var columns = new List<string>(header.Columns);
        var usedNames = new HashSet<string>(columns, StringComparer.Ordinal);

        while (true)
        {
            List<string>? fields;
            try
            {
                fields = records.ReadRecord();
            }
            catch (DecoderFallbackException ex)
            {
                ImportFailureDiagnostics.Attach(ex, new ImportFailureContext(ImportFailureStage.Rows,
                    LastCompletedRow: records.RecordNumber, Encoding: EncodingDetector.WireNameOf(dialect.Encoding),
                    Delimiter: dialect.Delimiter, ReaderVersion: ReaderVersion));
                throw SourceFileErrors.CannotDecode(request.FilePath, dialect.Encoding, ex);
            }
            catch (CsvStructureException ex)
            {
                ImportFailureDiagnostics.Attach(ex, new ImportFailureContext(ImportFailureStage.Rows,
                    LastCompletedRow: records.RecordNumber - 1, Row: ex.RecordNumber,
                    Encoding: EncodingDetector.WireNameOf(dialect.Encoding), Delimiter: dialect.Delimiter,
                    ReaderVersion: ReaderVersion));
                throw SourceFileErrors.UnbalancedQuote(request.FilePath, ex);
            }

            if (fields is null)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var values = ReadRowValues(fields, columns, usedNames);
            if (values.Count == 0)
            {
                continue; // 全空列
            }

            var observations = values
                .Select(pair => new TabularCellObservation(
                    pair.Key,
                    LegacyFieldKind.Text,
                    pair.Value.Length,
                    DecimalPlaces: null))
                .ToList();

            yield return new StagingRow(records.RecordNumber, values, observations)
            {
                HasQuotedLineBreak = records.RecordSpansLines
            };
        }

        await Task.CompletedTask;
    }

    private static string? ReaderVersion => typeof(CsvTableReader).Assembly.GetName().Version?.ToString();

    /// <summary>標頭之外有資料的欄 lazy 合成佔位欄（規則與 Open XML 讀取器相同）。</summary>
    private static Dictionary<string, string> ReadRowValues(
        List<string> fields, List<string> columns, HashSet<string> usedNames)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < fields.Count; i++)
        {
            var cell = fields[i].Trim();
            if (cell.Length == 0)
            {
                continue;
            }

            while (i >= columns.Count)
            {
                columns.Add(SynthesizePlaceholder(columns.Count + 1, usedNames));
            }

            values[columns[i]] = cell;
        }

        return values;
    }

    private static string SynthesizePlaceholder(int columnNumber, HashSet<string> usedNames)
    {
        var name = $"COL_{columnNumber}";
        var suffix = 2;
        while (!usedNames.Add(name))
        {
            name = $"COL_{columnNumber}_{suffix}";
            suffix++;
        }

        return name;
    }

    /// <summary>
    /// 略過開頭的空白列，讀第一個有內容的記錄作為標頭並正規化；整個檔都是空白列時以 empty_workbook 回報。
    /// </summary>
    private static (IReadOnlyList<string> Columns, int RowNumber) ReadHeader(
        CsvRecordReader records, string filePath, Encoding encoding)
    {
        try
        {
            while (true)
            {
                var fields = records.ReadRecord();
                if (fields is null)
                {
                    throw EmptyFile(filePath);
                }

                if (CsvRecordReader.IsBlank(fields))
                {
                    continue;
                }

                var headers = new List<(int ColumnNumber, string? RawName)>(fields.Count);
                for (var i = 0; i < fields.Count; i++)
                {
                    headers.Add((i + 1, fields[i]));
                }

                return (TabularHeaderNormalizer.Normalize(headers), records.RecordNumber);
            }
        }
        catch (DecoderFallbackException ex)
        {
            ImportFailureDiagnostics.Attach(ex, new ImportFailureContext(ImportFailureStage.Header,
                Encoding: EncodingDetector.WireNameOf(encoding), ReaderVersion: ReaderVersion));
            throw SourceFileErrors.CannotDecode(filePath, encoding, ex);
        }
        catch (CsvStructureException ex)
        {
            ImportFailureDiagnostics.Attach(ex, new ImportFailureContext(ImportFailureStage.Header,
                Row: ex.RecordNumber, Encoding: EncodingDetector.WireNameOf(encoding), ReaderVersion: ReaderVersion));
            throw SourceFileErrors.UnbalancedQuote(filePath, ex);
        }
    }

    private static JetActionException EmptyFile(string filePath) => new(
        JetErrorCodes.EmptyWorkbook,
        $"檔案 '{Path.GetFileName(filePath)}' 找不到標頭列。請確認第一個有內容的列是欄名。");

    private (Encoding Encoding, char Delimiter) ResolveDialect(TabularSourceRequest request)
    {
        var encoding = EncodingDetector.Resolve(request.EncodingName, request.FilePath);
        var sampleText = ReadSampleText(request.FilePath, encoding);

        // 空檔／全空白：在交給解析器之前就以 empty_workbook 回報，行為確定。
        if (string.IsNullOrWhiteSpace(sampleText))
        {
            throw EmptyFile(request.FilePath);
        }

        if (request.Delimiter is char overridden)
        {
            return (encoding, overridden);
        }

        var detected = CsvDialectDetector.DetectDelimiter(sampleText);
        return (encoding, detected ?? DefaultDelimiter); // null = 單欄檔，分隔符無作用
    }

    private static string ReadSampleText(string filePath, Encoding encoding)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);

            var buffer = new char[32 * 1024];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }
        catch (DecoderFallbackException ex)
        {
            // V8：自動偵測時，檢視、預覽與匯入的呼叫端不知道選了哪個編碼；這裡記下實際用來解碼的編碼，支援日誌才不是 unknown。
            ImportFailureDiagnostics.Attach(ex, new ImportFailureContext(ImportFailureStage.Header,
                Encoding: EncodingDetector.WireNameOf(encoding), ReaderVersion: ReaderVersion));
            throw SourceFileErrors.CannotDecode(filePath, encoding, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw SourceFileErrors.CannotOpen(filePath, ex);
        }
    }

    /// <summary>明確持有 TextReader 的 using 所有權（避免檔案 handle 殘留）。</summary>
    private static StreamReader OpenTextReader(string filePath, Encoding encoding)
    {
        try
        {
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw SourceFileErrors.CannotOpen(filePath, ex);
        }
    }
}
