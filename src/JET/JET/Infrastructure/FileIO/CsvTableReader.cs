using System.Runtime.CompilerServices;
using System.Text;
using JET.Domain;
using nietras.SeparatedValues;

namespace JET.Infrastructure;

/// <summary>
/// CSV / .txt（內容為 CSV）讀取器。
/// - 編碼：EncodingDetector 確定性鏈（BOM → 嚴格 UTF-8 → Big5），request.EncodingName 可覆寫。
/// - 分隔符：CsvDialectDetector 引號感知取樣統計，request.Delimiter 可覆寫。
/// - 解析：Sep（RFC 4180——引號內的分隔符與換行不切欄、"" 跳脫），cell 一律字串、由投影階段解析。
/// - SourceRowNumber 以邏輯列計（標頭 = 1）；引號內含換行時與實體行號可能偏移，屬已知限制。
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
        using var reader = OpenSepReader(textReader, request.FilePath, dialect.Delimiter);

        IReadOnlyList<string> columns = ReadNormalizedHeader(reader, request.FilePath);
        return Task.FromResult(columns);
    }

    public Task<TabularFileInspection> InspectAsync(string filePath, CancellationToken cancellationToken)
    {
        // 檢視回報的是「偵測鏈的判定結果」（不吃覆寫參數）：
        // 精靈把這些值直接帶回 import.*.fromFile 的 encoding/delimiter 覆寫欄位
        var encoding = EncodingDetector.Detect(filePath);
        var sampleText = ReadSampleText(filePath, encoding);

        if (string.IsNullOrWhiteSpace(sampleText))
        {
            throw new JetActionException(
                JetErrorCodes.EmptyWorkbook,
                $"檔案 '{Path.GetFileName(filePath)}' 找不到標頭列。");
        }

        var detected = CsvDialectDetector.DetectDelimiter(sampleText);

        using var textReader = OpenTextReader(filePath, encoding);
        using var reader = OpenSepReader(textReader, filePath, detected ?? DefaultDelimiter);
        var columns = ReadNormalizedHeader(reader, filePath);

        return Task.FromResult(new TabularFileInspection(
            FileType: "csv",
            Worksheets: null,
            Columns: columns,
            Encoding: EncodingDetector.WireNameOf(encoding),
            Delimiter: detected?.ToString()));
    }

    public async IAsyncEnumerable<StagingRow> ReadRowsAsync(
        TabularSourceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var dialect = ResolveDialect(request);
        using var textReader = OpenTextReader(request.FilePath, dialect.Encoding);
        using var reader = OpenSepReader(textReader, request.FilePath, dialect.Delimiter);
        var columns = ReadNormalizedHeader(reader, request.FilePath);

        var rowNumber = 1; // 標頭列 = 1，資料列由 2 起算

        while (true)
        {
            bool moved;
            try
            {
                moved = reader.MoveNext();
            }
            catch (DecoderFallbackException ex)
            {
                throw new JetActionException(
                    JetErrorCodes.FileReadError,
                    $"檔案 '{Path.GetFileName(request.FilePath)}' 含無法以偵測編碼解讀的內容（{ex.Message}）；" +
                    "請改以匯入參數指定編碼，或將來源另存為 UTF-8。");
            }

            if (!moved)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            rowNumber++;

            // SepReader.Row 是 ref struct，必須在單一陳述式內消費完，不可跨 yield 邊界。
            var values = ReadRowValues(reader.Current, columns);
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

            yield return new StagingRow(rowNumber, values, observations);
        }

        await Task.CompletedTask;
    }

    private static Dictionary<string, string> ReadRowValues(SepReader.Row row, IReadOnlyList<string> columns)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var colCount = Math.Min(row.ColCount, columns.Count);

        for (var i = 0; i < colCount; i++)
        {
            var cell = row[i].ToString().Trim();
            if (cell.Length > 0)
            {
                values[columns[i]] = cell;
            }
        }

        return values;
    }

    /// <summary>
    /// 讀取第一邏輯列作為標頭並正規化。Sep 內建 header 模式要求欄名唯一（重複欄名直接拋例外），
    /// 因此以 HasHeader=false 自行消費標頭列，與 Open XML 活頁簿共用 TabularHeaderNormalizer（guide §3.1.1）。
    /// </summary>
    private static IReadOnlyList<string> ReadNormalizedHeader(SepReader reader, string filePath)
    {
        bool moved;
        try
        {
            moved = reader.MoveNext();
        }
        catch (DecoderFallbackException ex)
        {
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                $"檔案 '{Path.GetFileName(filePath)}' 含無法以偵測編碼解讀的內容（{ex.Message}）；" +
                "請改以匯入參數指定編碼，或將來源另存為 UTF-8。");
        }

        var headers = moved ? ReadHeaderCells(reader.Current) : [];

        // 空檔或只有空白標頭列：0 欄或單一空欄
        if (headers.Count == 0 || (headers.Count == 1 && string.IsNullOrWhiteSpace(headers[0].RawName)))
        {
            throw new JetActionException(
                JetErrorCodes.EmptyWorkbook,
                $"檔案 '{Path.GetFileName(filePath)}' 找不到標頭列。");
        }

        return TabularHeaderNormalizer.Normalize(headers);
    }

    private static List<(int ColumnNumber, string? RawName)> ReadHeaderCells(SepReader.Row row)
    {
        var headers = new List<(int ColumnNumber, string? RawName)>(row.ColCount);
        for (var i = 0; i < row.ColCount; i++)
        {
            headers.Add((i + 1, row[i].ToString()));
        }

        return headers;
    }

    private (Encoding Encoding, char Delimiter) ResolveDialect(TabularSourceRequest request)
    {
        var encoding = EncodingDetector.Resolve(request.EncodingName, request.FilePath);
        var sampleText = ReadSampleText(request.FilePath, encoding);

        // 空檔／全空白：在交給解析器之前就以 empty_workbook 回報，行為確定。
        if (string.IsNullOrWhiteSpace(sampleText))
        {
            throw new JetActionException(
                JetErrorCodes.EmptyWorkbook,
                $"檔案 '{Path.GetFileName(request.FilePath)}' 找不到標頭列。");
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
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                $"檔案 '{Path.GetFileName(filePath)}' 無法以指定或偵測的編碼解讀（{ex.Message}）；" +
                "請改以匯入參數指定編碼，或將來源另存為 UTF-8。");
        }
        catch (IOException ex)
        {
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                $"無法讀取檔案 '{Path.GetFileName(filePath)}'：{ex.Message}");
        }
    }

    /// <summary>明確持有 TextReader 的 using 所有權，不依賴 Sep 是否代為釋放（避免檔案 handle 殘留）。</summary>
    private static StreamReader OpenTextReader(string filePath, Encoding encoding)
    {
        try
        {
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        }
        catch (IOException ex)
        {
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                $"無法讀取檔案 '{Path.GetFileName(filePath)}'：{ex.Message}");
        }
    }

    private static SepReader OpenSepReader(TextReader textReader, string filePath, char delimiter)
    {
        try
        {
            // HasHeader=false：標頭列由 ReadNormalizedHeader 自行消費（Sep 內建 header 解析要求欄名唯一，
            // 重複欄名會拋例外，無法走 TabularHeaderNormalizer 的 _2/_3 正規化）；
            // Unescape：去除 RFC 4180 引號包覆與 "" 跳脫；
            // DisableColCountCheck：容忍各列欄數不一致（缺欄視為空，超欄忽略）。
            return Sep.Reader(o => o with
            {
                Sep = new Sep(delimiter),
                HasHeader = false,
                Unescape = true,
                DisableColCountCheck = true
            }).From(textReader);
        }
        catch (DecoderFallbackException ex)
        {
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                $"檔案 '{Path.GetFileName(filePath)}' 無法以指定或偵測的編碼解讀（{ex.Message}）；" +
                "請改以匯入參數指定編碼，或將來源另存為 UTF-8。");
        }
        catch (IOException ex)
        {
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                $"無法讀取檔案 '{Path.GetFileName(filePath)}'：{ex.Message}");
        }
    }
}
