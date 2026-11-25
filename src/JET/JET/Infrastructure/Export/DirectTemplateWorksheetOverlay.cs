using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using Spreadsheet = DocumentFormat.OpenXml.Spreadsheet;

namespace JET.Infrastructure;

internal sealed record DirectTemplateWorksheetOverlayPlan(
    Func<uint, bool> IsDynamicRow,
    Func<uint, uint, bool> IsDynamicCell,
    IReadOnlySet<string>? UseSourcePostSheetDataElements,
    bool UseSourceDimension,
    bool PreserveTemplateExtent,
    bool UseSourceColumnsWhenTemplateMissing,
    bool UseSourceColumns,
    bool MergeSourceColumnWidths,
    Func<uint, uint, bool> UseSourceCellStyle,
    Func<uint, uint, uint, uint>? MapTemplateCellStyle = null,
    Func<uint, uint, uint?, uint, uint>? MergeCellStyle = null,
    Func<uint, uint, uint>? MapTemplateRowStyle = null,
    Func<uint, bool>? UseSourceRowHeight = null,
    Func<uint, uint>? MapTemplateColumnStyle = null);

/// <summary>
/// 把呼叫端一列一列產生的 XML 疊寫到一張既有的範本工作表上，由上往下寫，不回頭。
/// 範本原本的 XML 先複製到關閉時自動刪除的暫存檔；記憶體裡只放目前處理的兩列，也就是範本的一列與產生的一列，
/// 以及資料列以外的工作表設定，這部分的大小有上限。
/// 內容寫完時由 <see cref="Complete"/> 關閉輸出；沒有寫完就釋放時只放掉輸出與暫存檔，不補寫結尾，
/// 這時匯出已經失敗或取消，這張工作表的內容不要了。
/// </summary>
internal sealed class DirectTemplateWorksheetOverlayWriter : IDisposable
{
    private static readonly XNamespace SpreadsheetNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static readonly IReadOnlyDictionary<string, int> PostSheetDataOrder =
        new[]
        {
            "sheetCalcPr", "sheetProtection", "protectedRanges", "scenarios",
            "autoFilter", "sortState", "dataConsolidate", "customSheetViews",
            "mergeCells", "phoneticPr", "conditionalFormatting", "dataValidations",
            "hyperlinks", "printOptions", "pageMargins", "pageSetup", "headerFooter",
            "rowBreaks", "colBreaks", "customProperties", "cellWatches",
            "ignoredErrors", "smartTags", "drawing", "legacyDrawing",
            "legacyDrawingHF", "picture", "oleObjects", "controls",
            "webPublishItems", "tableParts", "extLst"
        }
        .Select((name, index) => (name, index))
        .ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);

    private readonly DirectTemplateWorksheetOverlayPlan _plan;
    private readonly string _worksheetName;
    private readonly CancellationToken _cancellationToken;
    private readonly FileStream _templateCopy;
    private readonly StreamingWorksheet _template;
    private readonly Stream _content;
    private readonly XmlWriter _output;
    private XElement? _templateRow;
    private uint _lastSourceRow;
    private bool _started;
    private bool _completed;
    private bool _disposed;

    /// <param name="createTemplateCopy">
    /// 建立放範本副本的暫存檔；沒有指定時用 <see cref="CreateTemporaryWorksheetStream"/>。
    /// 測試從這裡留住副本，核對寫入器釋放或建構失敗之後副本已經關閉。
    /// </param>
    internal DirectTemplateWorksheetOverlayWriter(
        string worksheetName,
        WorksheetPart templatePart,
        DirectTemplateWorksheetOverlayPlan plan,
        CancellationToken cancellationToken,
        Func<FileStream>? createTemplateCopy = null)
    {
        ArgumentNullException.ThrowIfNull(templatePart);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(worksheetName);
        _worksheetName = worksheetName;
        _plan = plan;
        if (plan.UseSourceColumns && plan.MergeSourceColumnWidths)
        {
            throw new ArgumentException(
                "Direct-template overlay cannot replace and merge source columns at the same time.",
                nameof(plan));
        }
        _cancellationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();

        _templateCopy = (createTemplateCopy ?? CreateTemporaryWorksheetStream)();
        try
        {
            using (var input = templatePart.GetStream(FileMode.Open, FileAccess.Read))
            {
                input.CopyTo(_templateCopy);
            }
            _templateCopy.Position = 0;
            _template = new StreamingWorksheet(
                worksheetName,
                _templateCopy,
                cancellationToken);
            _content = FormalWorkbookArchive.OpenWorksheetContent(templatePart);
            _output = XmlWriter.Create(_content, WriterSettings());
        }
        catch
        {
            // 建構失敗時呼叫端拿不到這個寫入器，暫存的範本副本與已經開啟的項目要在這裡放掉。
            ExportResources.ReleaseAll(_output, _content, _template, _templateCopy);
            throw;
        }
    }

    internal void Start(XElement? generatedDimension, XElement? generatedColumns)
    {
        if (_started)
        {
            return;
        }
        _started = true;
        _cancellationToken.ThrowIfCancellationRequested();

        _output.WriteStartDocument();
        WriteStartElement(_output, _template.Root);
        var templateHasColumns = _template.Preamble.Any(
            element => element.Name == SpreadsheetNamespace + "cols");
        var dimensionWritten = false;
        foreach (var child in _template.Preamble)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (child.Name == SpreadsheetNamespace + "dimension"
                && _plan.UseSourceDimension)
            {
                var mergedDimension = _plan.PreserveTemplateExtent
                    ? UnionDimensions(child, generatedDimension)
                    : generatedDimension ?? child;
                mergedDimension.WriteTo(_output);
                dimensionWritten = true;
                continue;
            }
            if (child.Name == SpreadsheetNamespace + "cols"
                && _plan.UseSourceColumns)
            {
                continue;
            }
            if (child.Name == SpreadsheetNamespace + "cols"
                && _plan.MergeSourceColumnWidths
                && generatedColumns is not null)
            {
                MapColumnStyles(MergeMaximumColumnWidths(child, generatedColumns)).WriteTo(_output);
                continue;
            }
            if (child.Name == SpreadsheetNamespace + "cols")
            {
                MapColumnStyles(new XElement(child)).WriteTo(_output);
                continue;
            }
            child.WriteTo(_output);
        }

        if (_plan.UseSourceDimension
            && !dimensionWritten
            && generatedDimension is not null)
        {
            generatedDimension.WriteTo(_output);
        }
        if ((_plan.UseSourceColumns
             || (_plan.UseSourceColumnsWhenTemplateMissing
                 || _plan.MergeSourceColumnWidths)
                && !templateHasColumns)
            && generatedColumns is not null)
        {
            generatedColumns.WriteTo(_output);
        }

        WriteStartElement(_output, _template.SheetData);
        _templateRow = _template.ReadNextRow(_cancellationToken);
    }

    internal void WriteRow(XElement sourceRow)
    {
        ArgumentNullException.ThrowIfNull(sourceRow);
        EnsureStarted();
        _cancellationToken.ThrowIfCancellationRequested();
        var sourceIndex = RowIndex(sourceRow);
        if (!AdvanceToSourceRow(sourceIndex))
        {
            return;
        }
        WriteSourceRow(sourceRow, sourceIndex);
    }

    /// <summary>
    /// 寫一列產生端建立的列。明細大多落在範本列之後，而且每一格都由產生端決定；這種列直接寫出，
    /// 不先轉成文字再解析成 XML 物件，寫出的內容和 <see cref="WriteRow(XElement)"/> 相同。
    /// 範本在同一列有內容、有格子不歸產生端，或樣式要和範本合併卻沒有帶樣式時，照原本的做法合併。
    /// 樣式要合併時會直接改 <paramref name="sourceRow"/> 裡格子的樣式，呼叫端寫完後不再使用這一列。
    /// </summary>
    internal void WriteRow(Spreadsheet.Row sourceRow)
    {
        ArgumentNullException.ThrowIfNull(sourceRow);
        EnsureStarted();
        _cancellationToken.ThrowIfCancellationRequested();
        var sourceIndex = sourceRow.RowIndex?.Value
            ?? throw new InvalidDataException("WorkingPaper worksheet row 缺少有效 index。");
        if (!AdvanceToSourceRow(sourceIndex))
        {
            return;
        }
        if ((_templateRow is null || RowIndex(_templateRow) > sourceIndex)
            && TryPrepareGeneratedRow(sourceRow, sourceIndex))
        {
            WriteGeneratedRow(sourceRow);
            return;
        }
        WriteSourceRow(
            XElement.Parse(sourceRow.OuterXml, LoadOptions.PreserveWhitespace),
            sourceIndex);
    }

    /// <summary>
    /// 確認來源列沒有倒退，並把排在它前面的範本列寫完。這一列不歸產生端時回傳 false，呼叫端略過它。
    /// </summary>
    private bool AdvanceToSourceRow(uint sourceIndex)
    {
        if (sourceIndex < _lastSourceRow)
        {
            throw new InvalidDataException(
                $"WorkingPaper worksheet '{_worksheetName}' direct SAX source rows 不得倒退；"
                + $"last={_lastSourceRow}, current={sourceIndex}。");
        }
        _lastSourceRow = sourceIndex;
        if (!_plan.IsDynamicRow(sourceIndex))
        {
            return false;
        }

        while (_templateRow is not null && RowIndex(_templateRow) < sourceIndex)
        {
            WriteTemplateOnlyRow(_templateRow);
            _templateRow = _template.ReadNextRow(_cancellationToken);
        }
        return true;
    }

    /// <summary>
    /// 判斷這一列能不能直接寫出，可以的話先把要和範本合併的樣式算好。
    /// 先整列看過一次、確定可以才動樣式，回傳 false 時這一列沒有被改過。
    /// </summary>
    private bool TryPrepareGeneratedRow(Spreadsheet.Row sourceRow, uint sourceIndex)
    {
        if (string.IsNullOrEmpty(sourceRow.Prefix)
            || sourceRow.NamespaceDeclarations.Any()
            || sourceRow.ExtendedAttributes.Any()
            || sourceRow.MCAttributes is not null)
        {
            return false;
        }
        var mergeCellStyle = _plan.MergeCellStyle;
        foreach (var child in sourceRow.ChildElements)
        {
            if (child is not Spreadsheet.Cell cell
                || cell.CellReference?.Value is not { } reference
                || !_plan.IsDynamicCell(sourceIndex, ParseReference(reference).Column))
            {
                return false;
            }
            if (mergeCellStyle is not null
                && cell.StyleIndex is null
                && _plan.UseSourceCellStyle(sourceIndex, ParseReference(reference).Column))
            {
                return false;
            }
        }
        if (mergeCellStyle is null)
        {
            return true;
        }

        foreach (var child in sourceRow.ChildElements)
        {
            var cell = (Spreadsheet.Cell)child;
            var column = ParseReference(cell.CellReference!.Value!).Column;
            if (_plan.UseSourceCellStyle(sourceIndex, column))
            {
                cell.StyleIndex = mergeCellStyle(
                    sourceIndex,
                    column,
                    null,
                    cell.StyleIndex!.Value);
            }
        }
        return true;
    }

    /// <summary>
    /// 列的元素帶著命名空間前置詞。前置詞的宣告明確寫在每一列上，和交 XML 物件時寫出的內容相同，
    /// 也不會因為範本的根元素有沒有宣告同一個前置詞而不同。
    /// </summary>
    private void WriteGeneratedRow(Spreadsheet.Row row)
    {
        _output.WriteStartElement(row.Prefix, row.LocalName, row.NamespaceUri);
        foreach (var attribute in row.GetAttributes())
        {
            _output.WriteAttributeString(
                attribute.Prefix,
                attribute.LocalName,
                attribute.NamespaceUri,
                attribute.Value);
        }
        _output.WriteAttributeString("xmlns", row.Prefix, null, row.NamespaceUri);
        foreach (var child in row.ChildElements)
        {
            child.WriteTo(_output);
        }
        _output.WriteEndElement();
    }

    private void WriteSourceRow(XElement sourceRow, uint sourceIndex)
    {
        if (_templateRow is null || RowIndex(_templateRow) > sourceIndex)
        {
            var generated = new XElement(sourceRow);
            KeepDynamicCells(generated, sourceIndex, _plan.IsDynamicCell);
            MergeGeneratedCellStyles(
                generated,
                sourceIndex,
                _plan.UseSourceCellStyle,
                _plan.MergeCellStyle);
            generated.WriteTo(_output);
            return;
        }

        var merged = new XElement(_templateRow);
        MapTemplateCellStyles(merged, sourceIndex);
        if (_plan.UseSourceRowHeight?.Invoke(sourceIndex) == true)
        {
            CopyCustomRowHeight(merged, sourceRow);
        }
        ClearDynamicCells(merged, sourceIndex, _plan.IsDynamicCell);
        OverlayDynamicCells(
            merged,
            sourceRow,
            sourceIndex,
            _plan.IsDynamicCell,
            _plan.UseSourceCellStyle,
            _plan.MergeCellStyle);
        merged.WriteTo(_output);
        _templateRow = _template.ReadNextRow(_cancellationToken);
    }

    internal void Complete(IReadOnlyList<XElement> generatedPost)
    {
        ArgumentNullException.ThrowIfNull(generatedPost);
        if (_completed)
        {
            return;
        }
        EnsureStarted();
        while (_templateRow is not null)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            WriteTemplateOnlyRow(_templateRow);
            _templateRow = _template.ReadNextRow(_cancellationToken);
        }
        _output.WriteEndElement();

        WritePostSheetDataElements(
            _output,
            _template.ReadPostElements(_cancellationToken),
            _plan.UseSourcePostSheetDataElements
                ?? new HashSet<string>(StringComparer.Ordinal),
            generatedPost);
        _output.WriteEndElement();
        _output.WriteEndDocument();
        _output.Flush();
        _completed = true;
        // 內容寫完後在這裡關閉輸出：關閉壓縮檔項目時還會寫出最後一段，失敗代表工作表不完整，要往外丟。
        _output.Dispose();
        _content.Dispose();
    }

    /// <summary>只放掉輸出與暫存的範本副本，不補寫結尾，也不丟出例外。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        ExportResources.ReleaseAll(_output, _content, _template, _templateCopy);
    }

    private void EnsureStarted()
    {
        if (!_started)
        {
            Start(generatedDimension: null, generatedColumns: null);
        }
    }

    private void WriteTemplateOnlyRow(XElement templateRow)
    {
        var rowIndex = RowIndex(templateRow);
        var row = new XElement(templateRow);
        MapTemplateCellStyles(row, rowIndex);
        if (_plan.IsDynamicRow(rowIndex))
        {
            ClearDynamicCells(row, rowIndex, _plan.IsDynamicCell);
        }
        row.WriteTo(_output);
    }

    /// <summary>
    /// 範本欄的預設樣式決定沒有寫值的格子能不能編輯；受保護的底稿要讓這些格子保持可填。
    /// 沒有設定時原樣保留。
    /// </summary>
    private XElement MapColumnStyles(XElement columns)
    {
        if (_plan.MapTemplateColumnStyle is null)
        {
            return columns;
        }
        foreach (var column in columns.Elements(SpreadsheetNamespace + "col"))
        {
            var style = ParseOptionalStyle(column.Attribute("style"));
            var mapped = _plan.MapTemplateColumnStyle(style);
            if (mapped != style)
            {
                column.SetAttributeValue(
                    "style",
                    mapped.ToString(CultureInfo.InvariantCulture));
            }
        }
        return columns;
    }

    private void MapTemplateCellStyles(XElement row, uint rowIndex)
    {
        if (_plan.MapTemplateRowStyle is not null
            && row.Attribute("s") is { } rowStyleAttribute)
        {
            var style = ParseOptionalStyle(rowStyleAttribute);
            var mapped = _plan.MapTemplateRowStyle(rowIndex, style);
            if (mapped != style)
            {
                rowStyleAttribute.Value = mapped.ToString(CultureInfo.InvariantCulture);
            }
        }
        if (_plan.MapTemplateCellStyle is null)
        {
            return;
        }
        foreach (var cell in row.Elements(SpreadsheetNamespace + "c"))
        {
            var column = ParseReference((string?)cell.Attribute("r") ?? "A1").Column;
            var style = ParseOptionalStyle(cell.Attribute("s"));
            var mapped = _plan.MapTemplateCellStyle(rowIndex, column, style);
            if (mapped != style)
            {
                cell.SetAttributeValue(
                    "s",
                    mapped.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private static uint ParseOptionalStyle(XAttribute? attribute)
    {
        if (attribute is null)
        {
            return 0U;
        }
        if (!uint.TryParse(
                attribute.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value))
        {
            throw new InvalidDataException("Template WorkingPaper cell style is invalid.");
        }
        return value;
    }

    private static void ClearDynamicCells(
        XElement row,
        uint rowIndex,
        Func<uint, uint, bool> isDynamicCell)
    {
        foreach (var cell in row.Elements(SpreadsheetNamespace + "c"))
        {
            var column = ParseReference((string?)cell.Attribute("r") ?? "A1").Column;
            if (isDynamicCell(rowIndex, column))
            {
                ClearCellValue(cell);
            }
        }
    }

    private static void KeepDynamicCells(
        XElement row,
        uint rowIndex,
        Func<uint, uint, bool> isDynamicCell)
    {
        foreach (var cell in row.Elements(SpreadsheetNamespace + "c").ToArray())
        {
            var column = ParseReference((string?)cell.Attribute("r") ?? "A1").Column;
            if (!isDynamicCell(rowIndex, column))
            {
                cell.Remove();
            }
        }
    }

    private static void OverlayDynamicCells(
        XElement targetRow,
        XElement sourceRow,
        uint rowIndex,
        Func<uint, uint, bool> isDynamicCell,
        Func<uint, uint, bool> useSourceCellStyle,
        Func<uint, uint, uint?, uint, uint>? mergeCellStyle)
    {
        foreach (var sourceCell in sourceRow.Elements(SpreadsheetNamespace + "c"))
        {
            var reference = (string?)sourceCell.Attribute("r")
                ?? throw new InvalidDataException("Generated WorkingPaper cell 缺少 reference。");
            var column = ParseReference(reference).Column;
            if (!isDynamicCell(rowIndex, column))
            {
                continue;
            }

            var targetCell = targetRow.Elements(SpreadsheetNamespace + "c")
                .SingleOrDefault(cell => string.Equals(
                    (string?)cell.Attribute("r"),
                    reference,
                    StringComparison.OrdinalIgnoreCase));
            if (targetCell is null)
            {
                var inserted = new XElement(sourceCell);
                if (useSourceCellStyle(rowIndex, column)
                    && mergeCellStyle is not null)
                {
                    inserted.SetAttributeValue(
                        "s",
                        mergeCellStyle(
                            rowIndex,
                            column,
                            null,
                            ParseOptionalStyle(sourceCell.Attribute("s")))
                        .ToString(CultureInfo.InvariantCulture));
                }
                InsertCell(targetRow, inserted, column);
            }
            else
            {
                var sourceOwnsStyle = useSourceCellStyle(rowIndex, column);
                var mergedStyle = sourceOwnsStyle
                    ? mergeCellStyle?.Invoke(
                        rowIndex,
                        column,
                        (uint?)ParseOptionalStyle(targetCell.Attribute("s")),
                        ParseOptionalStyle(sourceCell.Attribute("s")))
                    : null;
                CopyCellPayload(
                    targetCell,
                    sourceCell,
                    preserveTargetStyle: !sourceOwnsStyle,
                    mergedStyle);
            }
        }
    }

    private static void MergeGeneratedCellStyles(
        XElement row,
        uint rowIndex,
        Func<uint, uint, bool> useSourceCellStyle,
        Func<uint, uint, uint?, uint, uint>? mergeCellStyle)
    {
        if (mergeCellStyle is null)
        {
            return;
        }
        foreach (var cell in row.Elements(SpreadsheetNamespace + "c"))
        {
            var column = ParseReference((string?)cell.Attribute("r") ?? "A1").Column;
            if (!useSourceCellStyle(rowIndex, column))
            {
                continue;
            }
            cell.SetAttributeValue(
                "s",
                mergeCellStyle(
                    rowIndex,
                    column,
                    null,
                    ParseOptionalStyle(cell.Attribute("s")))
                .ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void InsertCell(XElement row, XElement cell, uint column)
    {
        var next = row.Elements(SpreadsheetNamespace + "c")
            .FirstOrDefault(item =>
                ParseReference((string?)item.Attribute("r") ?? "A1").Column > column);
        if (next is null)
        {
            row.Add(cell);
        }
        else
        {
            next.AddBeforeSelf(cell);
        }
    }

    private static void CopyCellPayload(
        XElement target,
        XElement source,
        bool preserveTargetStyle,
        uint? mergedStyle)
    {
        var reference = (string?)target.Attribute("r");
        var style = (string?)target.Attribute("s");
        target.RemoveAttributes();
        target.RemoveNodes();
        if (reference is not null)
        {
            target.SetAttributeValue("r", reference);
        }
        if (mergedStyle is { } styleIndex)
        {
            target.SetAttributeValue("s", styleIndex.ToString(CultureInfo.InvariantCulture));
        }
        else if (preserveTargetStyle && style is not null)
        {
            target.SetAttributeValue("s", style);
        }
        else if ((string?)source.Attribute("s") is { } sourceStyle)
        {
            target.SetAttributeValue("s", sourceStyle);
        }
        foreach (var attribute in source.Attributes().Where(
                     attribute => attribute.Name.LocalName is not ("r" or "s")))
        {
            target.Add(new XAttribute(attribute));
        }
        foreach (var child in source.Nodes())
        {
            target.Add(CloneNode(child));
        }
    }

    // 產生端為換行的內容指定了列高時，蓋過範本同一列的高度；產生端沒有指定時，保留範本原本的列高。
    private static void CopyCustomRowHeight(XElement target, XElement source)
    {
        if ((string?)source.Attribute("customHeight") is not ("1" or "true")
            || source.Attribute("ht") is not { } height)
        {
            return;
        }
        target.SetAttributeValue("ht", height.Value);
        target.SetAttributeValue("customHeight", "1");
    }

    private static void ClearCellValue(XElement cell)
    {
        cell.Attributes().Where(attribute => attribute.Name.LocalName == "t").Remove();
        cell.Elements().Where(element => element.Name.LocalName is "f" or "v" or "is").Remove();
    }

    private static XNode CloneNode(XNode node) => node switch
    {
        XElement element => new XElement(element),
        XCData data => new XCData(data.Value),
        XText text => new XText(text.Value),
        XComment comment => new XComment(comment.Value),
        XProcessingInstruction instruction =>
            new XProcessingInstruction(instruction.Target, instruction.Data),
        _ => throw new InvalidDataException(
            $"Unsupported WorkingPaper worksheet XML node '{node.NodeType}'.")
    };

    private static XElement UnionDimensions(XElement target, XElement? source)
    {
        if (source is null)
        {
            return target;
        }
        var targetReference = (string?)target.Attribute("ref");
        var sourceReference = (string?)source.Attribute("ref");
        if (string.IsNullOrWhiteSpace(targetReference))
        {
            return source;
        }
        if (string.IsNullOrWhiteSpace(sourceReference))
        {
            return target;
        }

        var targetRange = ParseRange(targetReference);
        var sourceRange = ParseRange(sourceReference);
        var startColumn = Math.Min(targetRange.Start.Column, sourceRange.Start.Column);
        var startRow = Math.Min(targetRange.Start.Row, sourceRange.Start.Row);
        var endColumn = Math.Max(targetRange.End.Column, sourceRange.End.Column);
        var endRow = Math.Max(targetRange.End.Row, sourceRange.End.Row);
        return new XElement(
            target.Name,
            target.Attributes().Where(attribute => attribute.Name.LocalName != "ref"),
            new XAttribute(
                "ref",
                $"{ColumnName(startColumn)}{startRow}:{ColumnName(endColumn)}{endRow}"));
    }

    private static XElement MergeMaximumColumnWidths(
        XElement templateColumns,
        XElement generatedColumns)
    {
        var requested = new Dictionary<uint, RequestedColumn>();
        foreach (var column in generatedColumns.Elements(
                     SpreadsheetNamespace + "col"))
        {
            var minimum = ParsePositiveUInt(column.Attribute("min"), "source column min");
            var maximum = ParsePositiveUInt(column.Attribute("max"), "source column max");
            var width = ParsePositiveWidth(column.Attribute("width"), "source column width");
            var autoFit = ParseBoolean(column.Attribute("bestFit"), "source column bestFit");
            if (maximum < minimum || maximum > 16_384)
            {
                throw new InvalidDataException(
                    $"Generated WorkingPaper column range {minimum}:{maximum} is invalid.");
            }
            for (var index = minimum; index <= maximum; index++)
            {
                requested[index] = new RequestedColumn(width, autoFit);
            }
        }

        var merged = new List<XElement>();
        var covered = new HashSet<uint>();
        foreach (var templateColumn in templateColumns.Elements(
                     SpreadsheetNamespace + "col"))
        {
            var minimum = ParsePositiveUInt(templateColumn.Attribute("min"), "template column min");
            var maximum = ParsePositiveUInt(templateColumn.Attribute("max"), "template column max");
            if (maximum < minimum || maximum > 16_384)
            {
                throw new InvalidDataException(
                    $"Template WorkingPaper column range {minimum}:{maximum} is invalid.");
            }
            var currentWidth = ParsePositiveWidth(
                templateColumn.Attribute("width"),
                "template column width");
            var segmentStart = minimum;
            var segmentRequest = requested.GetValueOrDefault(minimum);
            var segmentWidth = Math.Max(currentWidth, segmentRequest.Width);
            var segmentAutoFit = segmentRequest.AutoFit;
            for (var index = minimum; index <= maximum; index++)
            {
                covered.Add(index);
                var request = requested.GetValueOrDefault(index);
                var width = Math.Max(currentWidth, request.Width);
                if (width.Equals(segmentWidth) && request.AutoFit == segmentAutoFit)
                {
                    continue;
                }
                merged.Add(ResizeColumn(
                    templateColumn,
                    segmentStart,
                    index - 1,
                    currentWidth,
                    segmentWidth,
                    segmentAutoFit));
                segmentStart = index;
                segmentWidth = width;
                segmentAutoFit = request.AutoFit;
            }
            merged.Add(ResizeColumn(
                templateColumn,
                segmentStart,
                maximum,
                currentWidth,
                segmentWidth,
                segmentAutoFit));
        }

        foreach (var item in requested
                     .Where(item => !covered.Contains(item.Key))
                     .OrderBy(item => item.Key))
        {
            merged.Add(new XElement(
                SpreadsheetNamespace + "col",
                new XAttribute("min", item.Key),
                new XAttribute("max", item.Key),
                new XAttribute(
                    "width",
                    item.Value.Width.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("bestFit", "1"),
                new XAttribute("customWidth", "1")));
        }

        return new XElement(
            templateColumns.Name,
            templateColumns.Attributes(),
            merged.OrderBy(column =>
                ParsePositiveUInt(column.Attribute("min"), "merged column min")));
    }

    private static XElement ResizeColumn(
        XElement prototype,
        uint minimum,
        uint maximum,
        double currentWidth,
        double mergedWidth,
        bool autoFit)
    {
        var column = new XElement(prototype);
        column.SetAttributeValue("min", minimum);
        column.SetAttributeValue("max", maximum);
        if (mergedWidth > currentWidth)
        {
            column.SetAttributeValue(
                "width",
                mergedWidth.ToString(CultureInfo.InvariantCulture));
            column.SetAttributeValue("bestFit", "1");
            column.SetAttributeValue("customWidth", "1");
        }
        if (autoFit)
        {
            column.SetAttributeValue("bestFit", "1");
            column.SetAttributeValue("customWidth", "1");
        }
        return column;
    }

    private static bool ParseBoolean(XAttribute? attribute, string label)
    {
        if (attribute is null)
        {
            return false;
        }
        return attribute.Value switch
        {
            "1" => true,
            "0" => false,
            _ when attribute.Value.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            _ when attribute.Value.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            _ => throw new InvalidDataException($"WorkingPaper {label} is invalid.")
        };
    }

    private readonly record struct RequestedColumn(double Width, bool AutoFit);

    private static uint ParsePositiveUInt(XAttribute? attribute, string label)
    {
        if (!uint.TryParse(
                attribute?.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value)
            || value == 0)
        {
            throw new InvalidDataException($"WorkingPaper {label} is invalid.");
        }
        return value;
    }

    private static double ParsePositiveWidth(XAttribute? attribute, string label)
    {
        if (!double.TryParse(
                attribute?.Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value)
            || !double.IsFinite(value)
            || value <= 0D
            || value > 255D)
        {
            throw new InvalidDataException($"WorkingPaper {label} is invalid.");
        }
        return value;
    }

    private static (
        (uint Column, uint Row) Start,
        (uint Column, uint Row) End) ParseRange(string reference)
    {
        var parts = reference.Split(':', 2, StringSplitOptions.TrimEntries);
        var start = ParseReference(parts[0]);
        return (start, parts.Length == 1 ? start : ParseReference(parts[1]));
    }

    private static (uint Column, uint Row) ParseReference(string reference)
    {
        var column = 0U;
        var index = 0;
        while (index < reference.Length && char.IsLetter(reference[index]))
        {
            column = checked(
                column * 26
                + (uint)(char.ToUpperInvariant(reference[index]) - 'A' + 1));
            index++;
        }
        if (column == 0
            || !uint.TryParse(
                reference.AsSpan(index),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var row)
            || row == 0)
        {
            throw new InvalidDataException($"Invalid WorkingPaper cell reference '{reference}'.");
        }
        return (column, row);
    }

    private static string ColumnName(uint column)
    {
        if (column == 0)
        {
            throw new InvalidDataException("WorkingPaper worksheet column index 必須大於零。");
        }
        Span<char> buffer = stackalloc char[8];
        var position = buffer.Length;
        while (column > 0)
        {
            column--;
            buffer[--position] = (char)('A' + column % 26);
            column /= 26;
        }
        return new string(buffer[position..]);
    }

    private static uint RowIndex(XElement row) =>
        uint.TryParse(
            (string?)row.Attribute("r"),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : throw new InvalidDataException("WorkingPaper worksheet row 缺少有效 index。");

    private static void WritePostSheetDataElements(
        XmlWriter output,
        IReadOnlyList<XElement> templatePost,
        IReadOnlySet<string> sourceNames,
        IReadOnlyList<XElement> generatedPost)
    {
        var children = templatePost.Select(element => new XElement(element)).ToList();
        var sourcePost = generatedPost
            .Where(element => sourceNames.Contains(element.Name.LocalName))
            .ToDictionary(element => element.Name.LocalName, StringComparer.Ordinal);
        foreach (var name in sourceNames)
        {
            if (!sourcePost.TryGetValue(name, out var replacement))
            {
                children.RemoveAll(child => child.Name.LocalName == name);
                continue;
            }

            var existingIndex = children.FindIndex(child => child.Name.LocalName == name);
            if (existingIndex >= 0)
            {
                children[existingIndex] = new XElement(replacement);
                continue;
            }

            var rank = PostRank(name);
            var insertionIndex = children.FindIndex(
                child => PostRank(child.Name.LocalName) > rank);
            if (insertionIndex < 0)
            {
                children.Add(new XElement(replacement));
            }
            else
            {
                children.Insert(insertionIndex, new XElement(replacement));
            }
        }

        foreach (var child in children)
        {
            child.WriteTo(output);
        }
    }

    private static int PostRank(string name) =>
        PostSheetDataOrder.TryGetValue(name, out var rank)
            ? rank
            : int.MaxValue - 1;

    private static void WriteStartElement(XmlWriter writer, XElement element)
    {
        writer.WriteStartElement(
            element.GetPrefixOfNamespace(element.Name.Namespace),
            element.Name.LocalName,
            element.Name.NamespaceName);
        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                var namespacePrefix = attribute.Name.LocalName == "xmlns"
                    ? null
                    : attribute.Name.LocalName;
                if (namespacePrefix is null)
                {
                    writer.WriteAttributeString("xmlns", attribute.Value);
                }
                else
                {
                    writer.WriteAttributeString(
                        "xmlns",
                        namespacePrefix,
                        "http://www.w3.org/2000/xmlns/",
                        attribute.Value);
                }
            }
            else
            {
                writer.WriteAttributeString(
                    element.GetPrefixOfNamespace(attribute.Name.Namespace),
                    attribute.Name.LocalName,
                    attribute.Name.NamespaceName,
                    attribute.Value);
            }
        }
    }

    private static XElement ReadStartElement(XmlReader reader)
    {
        var element = new XElement(XName.Get(reader.LocalName, reader.NamespaceURI));
        if (!reader.HasAttributes)
        {
            return element;
        }
        reader.MoveToFirstAttribute();
        do
        {
            if (reader.Prefix == "xmlns")
            {
                element.Add(new XAttribute(XNamespace.Xmlns + reader.LocalName, reader.Value));
            }
            else if (reader.Name == "xmlns")
            {
                element.Add(new XAttribute("xmlns", reader.Value));
            }
            else
            {
                element.Add(new XAttribute(
                    XName.Get(reader.LocalName, reader.NamespaceURI),
                    reader.Value));
            }
        }
        while (reader.MoveToNextAttribute());
        reader.MoveToElement();
        return element;
    }

    private static XmlReaderSettings ReaderSettings(bool closeInput) =>
        new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = false,
            IgnoreWhitespace = false,
            CloseInput = closeInput
        };

    private static XmlWriterSettings WriterSettings() =>
        new()
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            OmitXmlDeclaration = false,
            CloseOutput = false
        };

    /// <summary>在 Windows 暫存資料夾建立放範本副本的暫存檔，關閉時自動刪除。</summary>
    internal static FileStream CreateTemporaryWorksheetStream() =>
        ExportSpoolFile.CreateStream(".jet-working-paper-worksheet-", bufferSize: 128 * 1024);

    private sealed class StreamingWorksheet : IDisposable
    {
        private readonly string _worksheetName;
        private readonly XmlReader _reader;
        private bool _sheetDataEnded;
        private bool _postRead;

        internal StreamingWorksheet(
            string worksheetName,
            Stream input,
            CancellationToken cancellationToken)
        {
            _worksheetName = worksheetName;
            _reader = XmlReader.Create(input, ReaderSettings(closeInput: false));
            _reader.MoveToContent();
            if (_reader.NodeType != XmlNodeType.Element
                || _reader.LocalName != "worksheet"
                || _reader.NamespaceURI != SpreadsheetNamespace.NamespaceName)
            {
                throw new InvalidDataException(
                    "WorkingPaper worksheet part 沒有 SpreadsheetML worksheet root。");
            }
            Root = ReadStartElement(_reader);

            var preamble = new List<XElement>();
            while (_reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }
                if (_reader.LocalName == "sheetData"
                    && _reader.NamespaceURI == SpreadsheetNamespace.NamespaceName)
                {
                    SheetData = ReadStartElement(_reader);
                    Preamble = preamble;
                    _sheetDataEnded = _reader.IsEmptyElement;
                    return;
                }
                preamble.Add(ReadCurrentElement());
            }
            throw new InvalidDataException("WorkingPaper worksheet 缺少 sheetData。");
        }

        internal XElement Root { get; }

        internal XElement SheetData { get; }

        internal IReadOnlyList<XElement> Preamble { get; }

        internal XElement? ReadNextRow(CancellationToken cancellationToken)
        {
            if (_sheetDataEnded)
            {
                return null;
            }
            while (_reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_reader.NodeType == XmlNodeType.EndElement
                    && _reader.LocalName == "sheetData"
                    && _reader.NamespaceURI == SpreadsheetNamespace.NamespaceName)
                {
                    _sheetDataEnded = true;
                    return null;
                }
                if (_reader.NodeType == XmlNodeType.Element
                    && _reader.LocalName == "row"
                    && _reader.NamespaceURI == SpreadsheetNamespace.NamespaceName)
                {
                    return ReadCurrentElement();
                }
            }
            throw new InvalidDataException(
                $"WorkingPaper worksheet '{_worksheetName}' 在 sheetData 結束前意外終止。");
        }

        internal IReadOnlyList<XElement> ReadPostElements(
            CancellationToken cancellationToken)
        {
            if (!_sheetDataEnded)
            {
                while (ReadNextRow(cancellationToken) is not null)
                {
                }
            }
            if (_postRead)
            {
                throw new InvalidOperationException(
                    "WorkingPaper worksheet post elements 已讀取。");
            }
            _postRead = true;

            var result = new List<XElement>();
            while (_reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_reader.NodeType == XmlNodeType.EndElement
                    && _reader.LocalName == "worksheet"
                    && _reader.NamespaceURI == SpreadsheetNamespace.NamespaceName)
                {
                    return result;
                }
                if (_reader.NodeType == XmlNodeType.Element)
                {
                    result.Add(ReadCurrentElement());
                }
            }
            throw new InvalidDataException(
                $"WorkingPaper worksheet '{_worksheetName}' 在 root 結束前意外終止。");
        }

        public void Dispose() => _reader.Dispose();

        private XElement ReadCurrentElement()
        {
            using var subtree = _reader.ReadSubtree();
            subtree.MoveToContent();
            return XElement.Load(subtree, LoadOptions.PreserveWhitespace);
        }
    }
}
