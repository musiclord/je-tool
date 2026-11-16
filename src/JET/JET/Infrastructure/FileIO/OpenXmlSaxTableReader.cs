using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 讀 .xlsx 與 .xlsm 的讀取器，用 Open XML SDK 的 SAX 方式一邊讀一邊處理。不論檔案大小都用同一個做法：
/// 工作表不載入成 DOM，逐列往前讀，一百萬列的活頁簿和小檔案走同一段程式。
/// - 標頭列是第一個有非空白值的列。ReadColumns 與 Inspect 讀到標頭就停，不讀後面的資料列。
/// - 金額可能存成文字儲存格，所以讀取時不判斷值是不是數字，只把各種儲存格型別統一轉成字串，確認配對時才解析。
/// - 數值一律先從 double 轉成 decimal，再用 InvariantCulture 轉成字串，去掉浮點數的尾數誤差；
///   超出 decimal 範圍時改用 "R" 格式。
/// - 標頭用共用的 TabularHeaderNormalizer 正規化，結果和文字檔讀取器完全相同。
///   資料列超出標頭範圍的儲存格有資料時，補一個 COL_{欄號} 佔位欄，不會丟掉。
/// - request.SheetName 指定要讀的工作表，不分大小寫；沒有指定時讀第一個資料工作表，chartsheet 不算。
/// </summary>
public sealed class OpenXmlSaxTableReader : ITabularFileReader
{
    public bool Supports(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".xlsm", StringComparison.OrdinalIgnoreCase);
    }

    public Task<IReadOnlyList<string>> ReadColumnsAsync(TabularSourceRequest request, CancellationToken cancellationToken)
    {
        using var handle = OpenDocument(request.FilePath);
        var (sheetName, part) = ResolveWorksheet(handle.Document, request);
        var context = CellContext.Create(handle.Document);

        var header = FindHeaderRow(part, context, onDimension: null, request.LeadingRowsToSkip)
            ?? throw NoHeader(request, sheetName);

        IReadOnlyList<string> columns = BuildHeaderMap(header.Cells).Select(h => h.Name).ToList();
        return Task.FromResult(columns);
    }

    public Task<TabularFileInspection> InspectAsync(string filePath, CancellationToken cancellationToken)
    {
        using var handle = OpenDocument(filePath);
        var sheets = ListDataSheets(handle.Document);

        if (sheets.Count == 0)
        {
            throw new JetActionException(
                JetErrorCodes.EmptyWorkbook,
                $"檔案「{Path.GetFileName(filePath)}」沒有任何工作表。");
        }

        var context = CellContext.Create(handle.Document);
        var worksheets = new List<WorksheetInspection>(sheets.Count);

        foreach (var (name, part) in sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? dimensionRef = null;
            var header = FindHeaderRow(part, context, reference => dimensionRef = reference);

            if (header is null)
            {
                // 空的工作表回傳空的欄名清單。檢視只是預覽，不是匯入；匯入精靈會顯示這張工作表沒有資料，不讓整個檔案失敗。
                worksheets.Add(new WorksheetInspection(name, [], null));
                continue;
            }

            var columns = BuildHeaderMap(header.Value.Cells).Select(h => h.Name).ToList();
            int? estimate = TryParseLastRow(dimensionRef, out var lastRow)
                ? Math.Max(0, lastRow - header.Value.RowNumber)
                : null;

            worksheets.Add(new WorksheetInspection(name, columns, estimate));
        }

        return Task.FromResult(new TabularFileInspection(
            FileType: "xlsx",
            Worksheets: worksheets,
            Columns: null,
            Encoding: null,
            Delimiter: null));
    }

    public async IAsyncEnumerable<StagingRow> ReadRowsAsync(
        TabularSourceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var handle = OpenDocument(request.FilePath);
        var (sheetName, part) = ResolveWorksheet(handle.Document, request);
        var context = CellContext.Create(handle.Document);

        Dictionary<int, string>? headerByColumn = null;
        HashSet<string>? usedNames = null;

        foreach (var row in EnumerateContentRows(part, context, onDimension: null))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (row.RowNumber <= request.LeadingRowsToSkip)
            {
                continue;
            }

            if (headerByColumn is null)
            {
                var headers = BuildHeaderMap(row.Cells);
                headerByColumn = headers.ToDictionary(h => h.ColumnNumber, h => h.Name);
                usedNames = new HashSet<string>(headers.Select(h => h.Name), StringComparer.Ordinal);
                continue;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var observations = new List<TabularCellObservation>(row.Cells.Count);
            foreach (var cell in row.Cells)
            {
                var columnNumber = cell.ColumnNumber;
                if (!headerByColumn.TryGetValue(columnNumber, out var header))
                {
                    // 這一欄在標頭範圍之外卻有資料：第一次遇到時補一個佔位欄，有資料的欄不會被丟掉。
                    header = SynthesizePlaceholder(columnNumber, usedNames!);
                    headerByColumn[columnNumber] = header;
                }

                values[header] = cell.Value;
                observations.Add(new TabularCellObservation(
                    header,
                    cell.Kind,
                    cell.Value.Length,
                    cell.DecimalPlaces));
            }

            yield return new StagingRow(row.RowNumber, values, observations);
        }

        if (headerByColumn is null)
        {
            throw NoHeader(request, sheetName);
        }

        await Task.CompletedTask;
    }

    // ---- 文件與工作表解析 ----

    /// <summary>
    /// 文件與底層 FileStream 的生命週期綁定：SDK 以路徑開檔在失敗時可能不釋放控制代碼
    /// （鎖住使用者的來源檔），因此自管 stream，開檔失敗即釋放。
    /// </summary>
    private sealed class WorkbookHandle(SpreadsheetDocument document, FileStream stream) : IDisposable
    {
        public SpreadsheetDocument Document { get; } = document;

        public void Dispose()
        {
            Document.Dispose();
            stream.Dispose();
        }
    }

    private static WorkbookHandle OpenDocument(string filePath)
    {
        FileStream? stream = null;
        try
        {
            // Excel 開著也讀得到最後一次存檔的內容。
            stream = SourceFileAccess.OpenRead(filePath);
            return new WorkbookHandle(SpreadsheetDocument.Open(stream, isEditable: false), stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 被其他程式獨占、沒有權限或磁碟讀取失敗：只寫檔名與下一步，不放 .NET 英文訊息與完整路徑。
            stream?.Dispose();
            throw SourceFileErrors.CannotOpen(filePath, ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException
            or ArgumentException or OpenXmlPackageException or InvalidDataException)
        {
            // FileFormatException（壞 zip / 非 OPC 套件）繼承 FormatException，已涵蓋
            stream?.Dispose();
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                $"檔案「{Path.GetFileName(filePath)}」不是可讀取的 Excel 活頁簿，或內容已損壞。請用 Excel 開啟並另存為 .xlsx 後重試。",
                innerException: ex);
        }
    }

    private static List<(string Name, WorksheetPart Part)> ListDataSheets(SpreadsheetDocument document)
    {
        var workbookPart = document.WorkbookPart;
        var sheets = new List<(string, WorksheetPart)>();

        if (workbookPart is null)
        {
            return sheets;
        }

        foreach (var sheet in workbookPart.Workbook?.Sheets?.Elements<Sheet>() ?? [])
        {
            if (sheet.Id?.Value is not { } relationshipId)
            {
                continue;
            }

            // chartsheet / dialogsheet 不是資料工作表
            if (workbookPart.TryGetPartById(relationshipId, out var part) && part is WorksheetPart worksheetPart)
            {
                sheets.Add((sheet.Name?.Value ?? string.Empty, worksheetPart));
            }
        }

        return sheets;
    }

    private static (string SheetName, WorksheetPart Part) ResolveWorksheet(
        SpreadsheetDocument document, TabularSourceRequest request)
    {
        var fileName = Path.GetFileName(request.FilePath);
        var sheets = ListDataSheets(document);

        if (sheets.Count == 0)
        {
            throw new JetActionException(
                JetErrorCodes.EmptyWorkbook,
                $"檔案「{fileName}」沒有任何工作表。");
        }

        if (request.SheetName is null)
        {
            return sheets[0];
        }

        foreach (var sheet in sheets)
        {
            if (string.Equals(sheet.Name, request.SheetName, StringComparison.OrdinalIgnoreCase))
            {
                return sheet;
            }
        }

        throw new JetActionException(
            JetErrorCodes.SheetNotFound,
            $"檔案「{fileName}」沒有名為「{request.SheetName}」的工作表。");
    }

    private static JetActionException NoHeader(TabularSourceRequest request, string sheetName) =>
        request.LeadingRowsToSkip > 0 && !request.FixedLeadingRows
            ? SourceFileErrors.HeaderRowBeyondContent(request.FilePath, request.LeadingRowsToSkip)
            : EmptySheet(request.FilePath, sheetName);

    private static JetActionException EmptySheet(string filePath, string sheetName)
    {
        return new JetActionException(
            JetErrorCodes.EmptyWorkbook,
            $"檔案「{Path.GetFileName(filePath)}」的工作表「{sheetName}」是空的。");
    }

    // ---- 串流列舉 ----

    private readonly record struct ContentCell(
        int ColumnNumber,
        string Value,
        LegacyFieldKind Kind,
        int? DecimalPlaces);

    private readonly record struct ContentRow(int RowNumber, List<ContentCell> Cells);

    /// <summary>略過前 N 個實際列號，再取第一個有內容的標頭；空白列也計入略過範圍。</summary>
    private static ContentRow? FindHeaderRow(
        WorksheetPart part, CellContext context, Action<string>? onDimension, int leadingRowsToSkip = 0)
    {
        foreach (var row in EnumerateContentRows(part, context, onDimension))
        {
            if (row.RowNumber <= leadingRowsToSkip)
            {
                continue;
            }

            return row;
        }

        return null;
    }

    /// <summary>
    /// 逐列往前讀工作表，不載入成 DOM，只回傳至少有一個非空白值的列。
    /// row 或 cell 沒有寫 r 屬性時，列號或欄號接著前一個往下數；OOXML 允許省略這個屬性。
    /// </summary>
    private static IEnumerable<ContentRow> EnumerateContentRows(
        WorksheetPart part, CellContext context, Action<string>? onDimension)
    {
        using var reader = OpenXmlReader.Create(part);
        var lastRowNumber = 0;

        while (reader.Read())
        {
            if (!reader.IsStartElement)
            {
                continue;
            }

            if (reader.ElementType == typeof(SheetDimension))
            {
                if (reader.LoadCurrentElement() is SheetDimension { Reference.Value: { } reference })
                {
                    onDimension?.Invoke(reference);
                }

                continue;
            }

            if (reader.ElementType != typeof(Row))
            {
                continue;
            }

            if (reader.LoadCurrentElement() is not Row row)
            {
                continue;
            }

            var rowNumber = row.RowIndex?.Value is { } explicitNumber ? (int)explicitNumber : lastRowNumber + 1;
            lastRowNumber = rowNumber;

            var cells = new List<ContentCell>();
            var lastColumnNumber = 0;

            foreach (var cell in row.Elements<Cell>())
            {
                var columnNumber = ParseColumnNumber(cell.CellReference?.Value) ?? lastColumnNumber + 1;
                lastColumnNumber = columnNumber;

                ExtractedCellValue extracted;
                try { extracted = ExtractCellValue(cell, context); }
                catch (Exception error) when (error is FormatException or OverflowException or ArgumentException)
                {
                    ImportFailureDiagnostics.Attach(error, new ImportFailureContext(ImportFailureStage.CellConversion,
                        Row: rowNumber, Column: columnNumber, CellType: cell.DataType?.InnerText ?? "n"));
                    throw;
                }
                if (extracted.Value.Length > 0)
                {
                    cells.Add(new ContentCell(
                        columnNumber,
                        extracted.Value,
                        extracted.Kind,
                        extracted.DecimalPlaces));
                }
            }

            if (cells.Count > 0)
            {
                yield return new ContentRow(rowNumber, cells);
            }
        }
    }

    // ---- 標頭 ----

    /// <summary>
    /// 從標頭列的儲存格建立欄號與欄名的對照。先取最小欄號到最大欄號之間的每一欄，中間沒有儲存格的欄當成空白標頭，
    /// 再交給 TabularHeaderNormalizer 正規化。
    /// </summary>
    private static List<(int ColumnNumber, string Name)> BuildHeaderMap(
        List<ContentCell> headerCells)
    {
        var byColumn = headerCells.ToDictionary(c => c.ColumnNumber, c => c.Value);
        var firstColumn = headerCells.Min(c => c.ColumnNumber);
        var lastColumn = headerCells.Max(c => c.ColumnNumber);

        var rawHeaders = new List<(int ColumnNumber, string? RawName)>(lastColumn - firstColumn + 1);
        for (var columnNumber = firstColumn; columnNumber <= lastColumn; columnNumber++)
        {
            rawHeaders.Add((columnNumber, byColumn.GetValueOrDefault(columnNumber)));
        }

        var normalized = TabularHeaderNormalizer.Normalize(rawHeaders);
        return rawHeaders
            .Select((h, index) => (h.ColumnNumber, normalized[index]))
            .ToList();
    }

    private static string SynthesizePlaceholder(int columnNumber, HashSet<string> usedNames)
    {
        var name = $"COL_{columnNumber}";

        // 佔位欄名和檔案裡真正的標頭同名時，照標頭正規化的做法加上 _2、_3。這種情形很少見，編號的順序不保證。
        var suffix = 2;
        while (!usedNames.Add(name))
        {
            name = $"COL_{columnNumber}_{suffix}";
            suffix++;
        }

        return name;
    }

    // ---- cell 值萃取 ----

    /// <summary>每個活頁簿只解析一次的三項資料：共用字串表、每個樣式是日期還是時間，以及是否使用 1904 日期系統。</summary>
    private sealed record CellContext(
        IReadOnlyList<string> SharedStrings,
        ExcelNumberKind[] StyleKinds,
        bool Date1904)
    {
        public static CellContext Create(SpreadsheetDocument document)
        {
            var workbookPart = document.WorkbookPart;
            if (workbookPart is null)
            {
                return new CellContext([], [], false);
            }

            var date1904 = workbookPart.Workbook?.WorkbookProperties?.Date1904?.Value ?? false;
            return new CellContext(ReadSharedStrings(workbookPart), ReadStyleKinds(workbookPart), date1904);
        }
    }

    /// <summary>把 sharedStrings.xml 從頭到尾讀一次，載入全部共用字串。筆數以實際讀到的為準，不看 count 屬性；注音標示不算內容。</summary>
    private static List<string> ReadSharedStrings(WorkbookPart workbookPart)
    {
        var strings = new List<string>();
        var part = workbookPart.SharedStringTablePart;

        if (part is null)
        {
            return strings;
        }

        using var reader = OpenXmlReader.Create(part);
        while (reader.Read())
        {
            if (reader.ElementType == typeof(SharedStringItem) && reader.IsStartElement
                && reader.LoadCurrentElement() is SharedStringItem item)
            {
                strings.Add(ExcelText.Decode(ExtractRichText(item)));
            }
        }

        return strings;
    }

    /// <summary>
    /// 只取 &lt;t&gt; 與 &lt;r&gt;&lt;t&gt;，排除 rPh / phoneticPr（注音標示不屬儲存格內容）。
    /// 呼叫端再以 <see cref="ExcelText.Decode"/> 還原 Excel 的 _xHHHH_ 寫法：Excel 存檔時把控制字元寫成這種樣子，
    /// 審計員在 Excel 看到什麼，JET 就匯入什麼。
    /// </summary>
    private static string ExtractRichText(OpenXmlElement container)
    {
        var builder = new StringBuilder();

        foreach (var child in container.ChildElements)
        {
            switch (child)
            {
                case Text text:
                    builder.Append(text.Text);
                    break;
                case Run run when run.Text is not null:
                    builder.Append(run.Text.Text);
                    break;
                    // PhoneticRun（rPh）/ PhoneticProperties（phoneticPr）一律跳過
            }
        }

        return builder.ToString();
    }

    private static ExcelNumberKind[] ReadStyleKinds(WorkbookPart workbookPart)
    {
        // styles.xml 恆小，DOM 載入；cellXfs 的 numFmtId 為準（不依賴 applyNumberFormat 旗標，
        // 與 Excel 實際呈現一致）
        if (workbookPart.WorkbookStylesPart?.Stylesheet is not { } stylesheet)
        {
            return [];
        }

        var customFormats = new Dictionary<int, string>();
        foreach (var format in stylesheet.NumberingFormats?.Elements<NumberingFormat>() ?? [])
        {
            if (format.NumberFormatId?.Value is { } id && format.FormatCode?.Value is { } code)
            {
                customFormats[(int)id] = code;
            }
        }

        return (stylesheet.CellFormats?.Elements<CellFormat>() ?? [])
            .Select(xf => ExcelDateFormatDetector.Classify((int)(xf.NumberFormatId?.Value ?? 0), customFormats))
            .ToArray();
    }

    private readonly record struct ExtractedCellValue(
        string Value,
        LegacyFieldKind Kind,
        int? DecimalPlaces);

    private static ExtractedCellValue ExtractCellValue(Cell cell, CellContext context)
    {
        var dataType = cell.DataType?.Value;

        if (dataType == CellValues.SharedString)
        {
            var raw = cell.CellValue?.InnerText;
            var value = raw is not null
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                && index >= 0 && index < context.SharedStrings.Count
                ? context.SharedStrings[index].Trim()
                : string.Empty;
            return TextValue(value);
        }

        if (dataType == CellValues.InlineString)
        {
            var value = cell.InlineString is null ? string.Empty : ExcelText.Decode(ExtractRichText(cell.InlineString)).Trim();
            return TextValue(value);
        }

        if (dataType == CellValues.String)
        {
            // 公式字串取快取值，同樣是 Excel 的跳脫寫法
            return TextValue(ExcelText.Decode(cell.CellValue?.InnerText ?? string.Empty).Trim());
        }

        if (dataType == CellValues.Error)
        {
            // 錯誤 cell 取原文（#DIV/0! 等）
            return TextValue((cell.CellValue?.InnerText ?? string.Empty).Trim());
        }

        if (dataType == CellValues.Boolean)
        {
            var raw = cell.CellValue?.InnerText?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                return new ExtractedCellValue(string.Empty, LegacyFieldKind.Number, DecimalPlaces: 0);
            }

            var value = raw == "1" ? "true" : "false";
            return new ExtractedCellValue(value, LegacyFieldKind.Number, DecimalPlaces: 0);
        }

        if (dataType == CellValues.Date)
        {
            // SpreadsheetML 的 t="d" 儲存格，值是 ISO 8601 的日期文字。儲存格型別已經說明它是日期，
            // 所以在這裡處理，不讓它走到下面被當成數字解析而失敗。值帶有時間時只取日期。
            var raw = cell.CellValue?.InnerText?.Trim() ?? string.Empty;
            if (DateTime.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
                    out var date))
            {
                return new ExtractedCellValue(
                    date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    LegacyFieldKind.Date,
                    DecimalPlaces: null);
            }

            return new ExtractedCellValue(raw, LegacyFieldKind.Date, DecimalPlaces: null);
        }

        // 其餘是數值儲存格：沒有型別屬性，或 t="n"。公式儲存格沒有存計算結果時當成空白，讀取時不重算公式。
        var rawValue = cell.CellValue?.InnerText;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return new ExtractedCellValue(string.Empty, LegacyFieldKind.Number, DecimalPlaces: 0);
        }

        if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            // 數值儲存格的內容解析不成數字時，仍然保留原文，確認配對時會報錯。
            // 欄位型別照儲存格型別記成數字，不因為內容像文字就改記成文字。
            return new ExtractedCellValue(rawValue.Trim(), LegacyFieldKind.Number, DecimalPlaces: 0);
        }

        var kind = StyleKindOf(cell.StyleIndex, context.StyleKinds);

        if (kind == ExcelNumberKind.Date)
        {
            if (TryFormatSerialDate(number, context.Date1904, out var isoDate))
            {
                return new ExtractedCellValue(isoDate, LegacyFieldKind.Date, DecimalPlaces: null);
            }

            return new ExtractedCellValue(
                DateNormalizer.ExcelSerialText(context.Date1904 ? number + 1462 : number),
                LegacyFieldKind.Date,
                DecimalPlaces: null);
        }

        if (kind == ExcelNumberKind.Time)
        {
            return new ExtractedCellValue(
                TimeSpan.FromDays(number).ToString("c", CultureInfo.InvariantCulture),
                LegacyFieldKind.Time,
                DecimalPlaces: null);
        }

        var normalized = NormalizeNumericValue(number);
        return new ExtractedCellValue(
            normalized.Value,
            LegacyFieldKind.Number,
            normalized.DecimalPlaces);
    }

    private static ExtractedCellValue TextValue(string value) =>
        new(value, LegacyFieldKind.Text, DecimalPlaces: null);

    private readonly record struct NormalizedNumericValue(string Value, int DecimalPlaces);

    private static NormalizedNumericValue NormalizeNumericValue(double number)
    {
        try
        {
            var value = (decimal)number;
            var decimalPlaces = (decimal.GetBits(value)[3] >> 16) & 0xFF;
            return new NormalizedNumericValue(
                value.ToString(CultureInfo.InvariantCulture),
                decimalPlaces);
        }
        catch (OverflowException)
        {
            return new NormalizedNumericValue(
                number.ToString("R", CultureInfo.InvariantCulture),
                DecimalPlaces: 0);
        }
    }

    private static ExcelNumberKind StyleKindOf(UInt32Value? styleIndex, ExcelNumberKind[] styleKinds)
    {
        return styleIndex?.Value is { } index && index < (uint)styleKinds.Length
            ? styleKinds[index]
            : ExcelNumberKind.None;
    }

    private static bool TryFormatSerialDate(double serial, bool date1904, out string isoDate)
    {
        isoDate = string.Empty;

        // 1904 日期系統的序列值原點晚 1462 天
        var oaDate = date1904 ? serial + 1462 : serial;

        // 超出 DateTime.FromOADate 能接受的範圍時，例如負數，不轉成日期；呼叫端改交出序列值的文字，確認配對時再判斷。
        if (!double.IsFinite(oaDate) || oaDate is < -657434 or >= 2958466)
        {
            return false;
        }

        var date = DateTime.FromOADate(oaDate);
        if (!DateNormalizer.IsSupportedExcelDate(date)) return false;
        isoDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }

    // ---- 參考解析 ----

    /// <summary>從儲存格參照取出欄號，例如 "AB678400" 是第 28 欄。參照是 null 或開頭沒有字母時回 null，呼叫端改用前一欄的欄號加一。</summary>
    private static int? ParseColumnNumber(string? cellReference)
    {
        if (string.IsNullOrEmpty(cellReference))
        {
            return null;
        }

        var column = 0;
        foreach (var ch in cellReference)
        {
            if (ch is >= 'A' and <= 'Z')
            {
                column = column * 26 + (ch - 'A' + 1);
            }
            else if (ch is >= 'a' and <= 'z')
            {
                column = column * 26 + (ch - 'a' + 1);
            }
            else
            {
                break;
            }
        }

        return column == 0 ? null : column;
    }

    /// <summary>dimension ref（"A1:AB678400" 或 "A1"）的末列號。</summary>
    private static bool TryParseLastRow(string? dimensionReference, out int lastRow)
    {
        lastRow = 0;

        if (string.IsNullOrEmpty(dimensionReference))
        {
            return false;
        }

        var separator = dimensionReference.IndexOf(':');
        var lastCell = separator < 0 ? dimensionReference : dimensionReference[(separator + 1)..];

        var digitStart = 0;
        while (digitStart < lastCell.Length && !char.IsAsciiDigit(lastCell[digitStart]))
        {
            digitStart++;
        }

        return digitStart < lastCell.Length
            && int.TryParse(lastCell[digitStart..], NumberStyles.None, CultureInfo.InvariantCulture, out lastRow);
    }
}
