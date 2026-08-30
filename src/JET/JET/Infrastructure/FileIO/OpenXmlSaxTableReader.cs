using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// OpenXML SAX 串流 .xlsx 讀取器（guide §3.1.5）。單一讀取器、無檔案大小分支：
/// worksheet 不建 DOM、逐列 forward-only，百萬列活頁簿與小檔走同一條路。
/// - 標頭列 = 第一個含非空萃取值的列；ReadColumns / Inspect 讀完標頭即返回（early-exit）。
/// - 金額可能是文字儲存格，讀取階段不解析數值語意，只做型別正規化成字串（投影階段解析）。
/// - 數值一律 double→(decimal)→InvariantCulture（吸收浮點殘影；超出 decimal 退 "R"）。
/// - 標頭正規化走共用的 TabularHeaderNormalizer（與 csv reader 一字不差）；
///   標頭範圍外的資料 cell lazy 合成 COL_{欄號} 佔位欄，不靜默丟棄。
/// - request.SheetName 指定工作表（不分大小寫）；缺省 = 第一個資料工作表（chartsheet 跳過）。
/// </summary>
public sealed class OpenXmlSaxTableReader : ITabularFileReader
{
    public bool Supports(string filePath)
    {
        return string.Equals(Path.GetExtension(filePath), ".xlsx", StringComparison.OrdinalIgnoreCase);
    }

    public Task<IReadOnlyList<string>> ReadColumnsAsync(TabularSourceRequest request, CancellationToken cancellationToken)
    {
        using var handle = OpenDocument(request.FilePath);
        var (sheetName, part) = ResolveWorksheet(handle.Document, request);
        var context = CellContext.Create(handle.Document);

        var header = FindHeaderRow(part, context, onDimension: null, request.LeadingRowsToSkip)
            ?? throw EmptySheet(request.FilePath, sheetName);

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
                $"檔案 '{Path.GetFileName(filePath)}' 沒有任何工作表。");
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
                // 空工作表回空欄名清單（檢視是預覽，不是匯入——精靈呈現「此表無資料」而非整檔失敗）
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
        var skipped = 0;

        foreach (var row in EnumerateContentRows(part, context, onDimension: null))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (skipped < request.LeadingRowsToSkip)
            {
                skipped++;
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
                    // 標頭範圍外的資料欄：lazy 合成佔位欄（guide §3.1.5——有資料的欄絕不靜默丟棄）
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
            throw EmptySheet(request.FilePath, sheetName);
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
            stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new WorkbookHandle(SpreadsheetDocument.Open(stream, isEditable: false), stream);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException
            or ArgumentException or OpenXmlPackageException or InvalidDataException)
        {
            // FileFormatException（壞 zip / 非 OPC 套件）繼承 FormatException，已涵蓋
            stream?.Dispose();
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                $"無法讀取檔案 '{Path.GetFileName(filePath)}'：{ex.Message}");
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

        foreach (var sheet in workbookPart.Workbook.Sheets?.Elements<Sheet>() ?? [])
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
                $"檔案 '{fileName}' 沒有任何工作表。");
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
            $"檔案 '{fileName}' 沒有名為 '{request.SheetName}' 的工作表。");
    }

    private static JetActionException EmptySheet(string filePath, string sheetName)
    {
        return new JetActionException(
            JetErrorCodes.EmptyWorkbook,
            $"檔案 '{Path.GetFileName(filePath)}' 的工作表 '{sheetName}' 是空的。");
    }

    // ---- 串流列舉 ----

    private readonly record struct ContentCell(
        int ColumnNumber,
        string Value,
        LegacyFieldKind Kind,
        int? DecimalPlaces);

    private readonly record struct ContentRow(int RowNumber, List<ContentCell> Cells);

    /// <summary>第一個含非空值的列（標頭列）；讀到即停。leadingRowsToSkip 先丟棄前 N 個內容列（樣式標題列）。</summary>
    private static ContentRow? FindHeaderRow(
        WorksheetPart part, CellContext context, Action<string>? onDimension, int leadingRowsToSkip = 0)
    {
        var skipped = 0;
        foreach (var row in EnumerateContentRows(part, context, onDimension))
        {
            if (skipped < leadingRowsToSkip)
            {
                skipped++;
                continue;
            }

            return row;
        }

        return null;
    }

    /// <summary>
    /// 逐列走訪 worksheet（forward-only，不建 DOM），只產出含 ≥1 非空萃取值的列。
    /// row/cell 的 r 屬性缺席時以連續計數遞補（OOXML 允許省略）。
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

                var extracted = ExtractCellValue(cell, context);
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

    /// <summary>標頭列 cell（已含實際欄號）→ min..max 連續欄位範圍（縫隙 = 空白標頭）→ 正規化。</summary>
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

        // 與真實同名標頭衝突時走 normalizer 同款 _2/_3 去重（病態案例，順序不保證）
        var suffix = 2;
        while (!usedNames.Add(name))
        {
            name = $"COL_{columnNumber}_{suffix}";
            suffix++;
        }

        return name;
    }

    // ---- cell 值萃取 ----

    /// <summary>共用字串表 / 樣式表 / 1904 日期系統的一次性解析結果。</summary>
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

            var date1904 = workbookPart.Workbook.WorkbookProperties?.Date1904?.Value ?? false;
            return new CellContext(ReadSharedStrings(workbookPart), ReadStyleKinds(workbookPart), date1904);
        }
    }

    /// <summary>sharedStrings.xml 一次串流載入（不信任 count 屬性；排除注音子樹）。</summary>
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
                strings.Add(ExtractRichText(item));
            }
        }

        return strings;
    }

    /// <summary>只取 &lt;t&gt; 與 &lt;r&gt;&lt;t&gt;，排除 rPh / phoneticPr（guide §3.1.5）。</summary>
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
            var value = cell.InlineString is null ? string.Empty : ExtractRichText(cell.InlineString).Trim();
            return TextValue(value);
        }

        if (dataType == CellValues.String || dataType == CellValues.Error)
        {
            // 公式字串取快取值；錯誤 cell 取原文（#DIV/0! 等）
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
            // SpreadsheetML t="d" 的值是 ISO 8601；native type 本身就是權威，
            // 不應落入 numeric parse failure。JET 的 date-time 顯示仍沿用日期優先。
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

        // 數值（無型別屬性或 t="n"）；公式 cell 無快取值 → 空（串流讀取不重算公式）
        var rawValue = cell.CellValue?.InnerText;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return new ExtractedCellValue(string.Empty, LegacyFieldKind.Number, DecimalPlaces: 0);
        }

        if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            // 防衛：native numeric 內容即使無法解析，仍保留原文交由投影階段報錯；
            // metadata 不以內容猜成 Text。
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
                NormalizeNumericValue(number).Value,
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

        // 1904 日期系統的序列值原點晚 1462 天（guide §3.1.5）
        var oaDate = date1904 ? serial + 1462 : serial;

        // DateTime.FromOADate 的合法範圍外（如負數）退回數值呈現，交由投影階段判讀
        if (oaDate is < -657434 or >= 2958466)
        {
            return false;
        }

        isoDate = DateTime.FromOADate(oaDate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }

    // ---- 參考解析 ----

    /// <summary>"AB678400" → 28；無字母前綴或 null → null（呼叫端以連續計數遞補）。</summary>
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
