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
/// - 共用字串格的編號在共用字串表裡找不到時，檔案已經損壞，停下來指出工作表與儲存格，不當成空白讀進來。
/// - 有公式、卻沒有存計算結果的格子讀成空白；request.FormulasWithoutResult 有值時，讀資料列時順便記下格數與第一格。
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
        var context = CellContext.Create(handle.Document, cancellationToken);

        var header = FindHeaderRow(
                part, context, new SheetSource(request.FilePath, sheetName), onDimension: null, request.LeadingRowsToSkip)
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

        var context = CellContext.Create(handle.Document, cancellationToken);
        var worksheets = new List<WorksheetInspection>(sheets.Count);

        foreach (var (name, part) in sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? dimensionRef = null;
            var header = FindHeaderRow(part, context, new SheetSource(filePath, name), reference => dimensionRef = reference);

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
        var context = CellContext.Create(handle.Document, cancellationToken);

        Dictionary<int, string>? headerByColumn = null;
        HashSet<string>? usedNames = null;

        // 只有讀資料列時計數；略過的標題列不匯入，裡面的公式不提醒。
        Action<int, int>? onFormulaWithoutResult = null;
        if (request.FormulasWithoutResult is { } formulas)
        {
            onFormulaWithoutResult = (rowNumber, columnNumber) =>
            {
                if (rowNumber > request.LeadingRowsToSkip)
                {
                    formulas.Observe(sheetName, CellAddress(columnNumber, rowNumber));
                }
            };
        }

        var source = new SheetSource(request.FilePath, sheetName);
        foreach (var row in EnumerateContentRows(part, context, source, onDimension: null, onFormulaWithoutResult))
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

    /// <summary>正在讀的檔案與工作表，讀不到儲存格內容時用來寫出是哪個檔案的哪一張工作表。</summary>
    private readonly record struct SheetSource(string FilePath, string SheetName);

    /// <summary>略過前 N 個實際列號，再取第一個有內容的標頭；空白列也計入略過範圍。</summary>
    private static ContentRow? FindHeaderRow(
        WorksheetPart part, CellContext context, SheetSource source, Action<string>? onDimension, int leadingRowsToSkip = 0)
    {
        foreach (var row in EnumerateContentRows(part, context, source, onDimension))
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
    /// 有公式、卻沒有存計算結果的格子照樣當成空白，另外把列號與欄號交給 onFormulaWithoutResult；
    /// 只有這種格子的列不回傳，但仍然會計數。
    /// </summary>
    private static IEnumerable<ContentRow> EnumerateContentRows(
        WorksheetPart part,
        CellContext context,
        SheetSource source,
        Action<string>? onDimension,
        Action<int, int>? onFormulaWithoutResult = null)
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

                // 先看有沒有值：大部分格子有值，不必再找公式元素。
                if (onFormulaWithoutResult is not null && cell.CellValue is null && cell.CellFormula is not null)
                {
                    onFormulaWithoutResult(rowNumber, columnNumber);
                }

                ExtractedCellValue extracted;
                try { extracted = ExtractCellValue(cell, context); }
                catch (MissingSharedStringException)
                {
                    throw SourceFileErrors.MissingSharedString(
                        source.FilePath, source.SheetName, CellAddress(columnNumber, rowNumber));
                }
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

    /// <summary>
    /// 每個活頁簿只解析一次的資料：共用字串表、每個樣式是日期還是時間、哪些樣式的內建格式在繁體中文版只顯示時間，
    /// 以及是否使用 1904 日期系統。
    /// </summary>
    private sealed record CellContext(
        IReadOnlyList<string> SharedStrings,
        ExcelNumberKind[] StyleKinds,
        bool[] TraditionalChineseTimeStyles,
        bool Date1904)
    {
        public static CellContext Create(SpreadsheetDocument document, CancellationToken cancellationToken)
        {
            var workbookPart = document.WorkbookPart;
            if (workbookPart is null)
            {
                return new CellContext([], [], [], false);
            }

            var date1904 = workbookPart.Workbook?.WorkbookProperties?.Date1904?.Value ?? false;
            var numberFormatIds = ReadNumberFormatIds(workbookPart);
            return new CellContext(
                ReadSharedStrings(workbookPart, cancellationToken),
                ReadStyleKinds(workbookPart, numberFormatIds),
                numberFormatIds.Select(ExcelDateFormatDetector.IsTimeInTraditionalChinese).ToArray(),
                date1904);
        }
    }

    /// <summary>把 sharedStrings.xml 從頭到尾讀一次，載入全部共用字串。筆數以實際讀到的為準，不看 count 屬性；注音標示不算內容。</summary>
    private static List<string> ReadSharedStrings(WorkbookPart workbookPart, CancellationToken cancellationToken)
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
            // 大檔的共用字串可以有上百萬筆；讀的途中使用者按取消要停得下來。
            cancellationToken.ThrowIfCancellationRequested();
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
    /// 使用者在 Excel 看到什麼，JET 就匯入什麼。
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

    // 每個樣式的數字格式編號，依 cellXfs 的順序。styles.xml 恆小，DOM 載入；cellXfs 的 numFmtId 為準
    // （不依賴 applyNumberFormat 旗標，與 Excel 實際呈現一致）。
    private static int[] ReadNumberFormatIds(WorkbookPart workbookPart) =>
        (workbookPart.WorkbookStylesPart?.Stylesheet?.CellFormats?.Elements<CellFormat>() ?? [])
            .Select(xf => (int)(xf.NumberFormatId?.Value ?? 0))
            .ToArray();

    private static ExcelNumberKind[] ReadStyleKinds(WorkbookPart workbookPart, int[] numberFormatIds)
    {
        var customFormats = new Dictionary<int, string>();
        foreach (var format in workbookPart.WorkbookStylesPart?.Stylesheet?.NumberingFormats?.Elements<NumberingFormat>() ?? [])
        {
            if (format.NumberFormatId?.Value is { } id && format.FormatCode?.Value is { } code)
            {
                customFormats[(int)id] = code;
            }
        }

        return numberFormatIds
            .Select(numberFormatId => ExcelDateFormatDetector.Classify(numberFormatId, customFormats))
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
            return TextValue(SharedStringText(cell.CellValue?.InnerText, context.SharedStrings));
        }

        if (dataType == CellValues.InlineString)
        {
            var value = cell.InlineString is null ? string.Empty : ExcelText.Decode(ExtractRichText(cell.InlineString)).Trim();
            return TextValue(value);
        }

        if (dataType == CellValues.String || dataType == CellValues.Date)
        {
            // 公式字串取快取值，同樣是 Excel 的跳脫寫法。
            // t="d" 的值是 ISO 8601 的日期文字，和其他文字格子一樣照原文讀成文字：讀取器不解析文字，
            // 不換算時差，也不補上沒寫的日期或年份；這個值是不是日期，確認配對時由日期規則判讀。
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

            // 布林儲存格的值只有 1 與 0。其他程式寫進其他值時照原文保留，不猜它是哪一個；
            // 欄位型別照儲存格型別記成數字，和數值儲存格寫了不是數字的內容時相同。
            var value = raw switch
            {
                "1" => "true",
                "0" => "false",
                _ => raw
            };
            return new ExtractedCellValue(value, LegacyFieldKind.Number, DecimalPlaces: 0);
        }

        // 其餘是數值儲存格：沒有型別屬性，或 t="n"。公式儲存格沒有存計算結果時當成空白，讀取時不重算公式；
        // 這種格子由逐列讀取的地方計數，匯入結果會提醒使用者。
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
            // 繁體中文版只顯示時間的內建格式，值小於 1 時沒有日期的部分。換成日期只會得到同一個 1900 年 1 月 0 日，
            // 不同的時間就分不出來，所以交出序列值的文字，確認配對時這個值列為無法辨識的日期。
            if (!context.Date1904 && number is >= 0 and < 1
                && cell.StyleIndex?.Value is { } styleIndex && styleIndex < (uint)context.TraditionalChineseTimeStyles.Length
                && context.TraditionalChineseTimeStyles[styleIndex])
            {
                return new ExtractedCellValue(DateNormalizer.ExcelSerialText(number), LegacyFieldKind.Date, DecimalPlaces: null);
            }

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

    /// <summary>
    /// 共用字串格的值是共用字串表的編號，從 0 起算。沒有值的格子當成空白。有值卻找不到對應的文字時，
    /// 這一格原本的文字已經讀不到，丟出 <see cref="MissingSharedStringException"/>，由逐列讀取的地方補上工作表與儲存格位址。
    /// </summary>
    private static string SharedStringText(string? raw, IReadOnlyList<string> sharedStrings)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            || index < 0
            || index >= sharedStrings.Count)
        {
            throw new MissingSharedStringException();
        }

        return sharedStrings[index].Trim();
    }

    /// <summary>共用字串格的編號在共用字串表裡找不到。只在這個讀取器內部使用，對外改成指出儲存格位址的錯誤。</summary>
    private sealed class MissingSharedStringException : Exception
    {
    }

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

    // 照 Excel 顯示的年月日換算；Excel 顯示不出日期時，例如負數，呼叫端改交出序列值的文字，確認配對時再判斷。
    private static bool TryFormatSerialDate(double serial, bool date1904, out string isoDate)
    {
        isoDate = ExcelSerialDate.ShownDate(serial, date1904) ?? string.Empty;
        return isoDate.Length > 0;
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

    /// <summary>欄號與列號組成儲存格位址，例如第 3 欄第 5 列是 C5。</summary>
    private static string CellAddress(int columnNumber, int rowNumber)
    {
        var letters = new StringBuilder();
        for (var column = columnNumber; column > 0; column = (column - 1) / 26)
        {
            letters.Insert(0, (char)('A' + (column - 1) % 26));
        }

        return letters.Append(rowNumber.ToString(CultureInfo.InvariantCulture)).ToString();
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
