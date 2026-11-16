using System.Runtime.CompilerServices;
using System.Text;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 文字檔讀取器，讀 .csv 與內容是 CSV 的 .txt。
/// - 編碼：由 EncodingDetector 依固定順序判定，先看 BOM，再試嚴格的 UTF-8，都不是就當成 Big5。
///   request.EncodingName 有值時改用指定的編碼。
/// - 分隔符：由 CsvDialectDetector 取檔案開頭的文字，統計各候選分隔符在引號外出現的次數來判定。
///   request.Delimiter 有值時改用指定的分隔符。
/// - 解析：用 <see cref="CsvRecordReader"/> 的一般 CSV 讀法。欄位開頭的引號才是引號，中間的引號是一般字元；
///   引號沒有成對時失敗，並寫出第幾列。每個值都讀成字串，確認配對時才解析成金額或日期。
/// - 標頭：先略過 request.LeadingRowsToSkip 筆記錄，也就是審計員指定的「欄名在第幾列」，空白列也算；再略過空白列。
///   第一個有內容的列是標頭，和 Excel 讀取器相同。標頭由這裡讀出後交給 TabularHeaderNormalizer 正規化，
///   重複的欄名加上 _2、_3。
/// - 欄數：資料列比標頭多出來的欄只要有資料，就補一個 COL_n 佔位欄，不會丟掉有資料的欄，和 Open XML 讀取器相同。
///   資料列比標頭少的欄當成空白。
/// - SourceRowNumber 以記錄計算，空白列與標頭之前的空白列都算一列。引號內含換行時，它和檔案的實際行號可能對不上，
///   這是已知限制。
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
        var header = ReadHeader(records, request.FilePath, dialect.Encoding, request.LeadingRowsToSkip, request.FixedLeadingRows);
        return Task.FromResult(header.Columns);
    }

    public Task<TabularFileInspection> InspectAsync(string filePath, CancellationToken cancellationToken)
    {
        // 檢視檔案時回報的是自動偵測出的編碼與分隔符，不套用指定值。
        // 匯入精靈會把這兩個值帶進 import.*.fromFile 的 encoding 與 delimiter 欄位。
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
        var header = ReadHeader(records, request.FilePath, dialect.Encoding, request.LeadingRowsToSkip, request.FixedLeadingRows);
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

    /// <summary>把一筆記錄的值對到欄名。超出標頭範圍的欄有資料時才補上佔位欄，規則和 Open XML 讀取器相同。</summary>
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
    /// 先略過 leadingRowsToSkip 筆記錄（空白列也算一筆，和 Excel 讀取器以實際列號計算相同），再略過空白列，
    /// 讀第一個有內容的記錄作為標頭並正規化；整個檔都是空白列時以 empty_workbook 回報，
    /// 審計員指定了欄名列卻略過到檔尾時同一個代碼，訊息改說那一列起沒有內容；略過列數是版面固定的就照舊說檔案是空的。
    /// </summary>
    private static (IReadOnlyList<string> Columns, int RowNumber) ReadHeader(
        CsvRecordReader records, string filePath, Encoding encoding, int leadingRowsToSkip = 0, bool fixedLeadingRows = false)
    {
        try
        {
            while (true)
            {
                var fields = records.ReadRecord();
                if (fields is null)
                {
                    throw leadingRowsToSkip > 0 && !fixedLeadingRows
                        ? SourceFileErrors.HeaderRowBeyondContent(filePath, leadingRowsToSkip)
                        : EmptyFile(filePath);
                }

                if (records.RecordNumber <= leadingRowsToSkip || CsvRecordReader.IsBlank(fields))
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
        $"檔案「{Path.GetFileName(filePath)}」找不到標頭列。請確認第一個有內容的列是欄名。");

    private (Encoding Encoding, char Delimiter) ResolveDialect(TabularSourceRequest request)
    {
        var encoding = EncodingDetector.Resolve(request.EncodingName, request.FilePath);
        var sampleText = ReadSampleText(request.FilePath, encoding);

        // 檔案是空的或只有空白時，還沒交給解析器就回報 empty_workbook。
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
            using var stream = SourceFileAccess.OpenRead(filePath);
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);

            var buffer = new char[32 * 1024];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }
        catch (DecoderFallbackException ex)
        {
            // 自動偵測時，檢視、預覽與匯入的呼叫端不知道選了哪個編碼；這裡記下實際用來解碼的編碼，支援日誌才不是 unknown。
            ImportFailureDiagnostics.Attach(ex, new ImportFailureContext(ImportFailureStage.Header,
                Encoding: EncodingDetector.WireNameOf(encoding), ReaderVersion: ReaderVersion));
            throw SourceFileErrors.CannotDecode(filePath, encoding, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw SourceFileErrors.CannotOpen(filePath, ex);
        }
    }

    /// <summary>開檔並回傳 StreamReader。呼叫端要用 using 釋放它，檔案才不會一直被開著。</summary>
    private static StreamReader OpenTextReader(string filePath, Encoding encoding)
    {
        try
        {
            var stream = SourceFileAccess.OpenRead(filePath);
            return new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw SourceFileErrors.CannotOpen(filePath, ex);
        }
    }
}
