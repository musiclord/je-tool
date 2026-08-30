using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;

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
    Func<uint, uint, uint>? MapTemplateRowStyle = null);

/// <summary>
/// 接受 emitter 逐列產生的 XML，直接 forward-only 覆寫一個既有範本 worksheet part。
/// 範本 XML 只 spool 到 DeleteOnClose stream；記憶體只持有當前兩列與有界 layout。
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
    private readonly XmlWriter _output;
    private XElement? _templateRow;
    private uint _lastSourceRow;
    private bool _started;
    private bool _completed;
    private bool _disposed;

    internal DirectTemplateWorksheetOverlayWriter(
        string worksheetName,
        WorksheetPart templatePart,
        DirectTemplateWorksheetOverlayPlan plan,
        CancellationToken cancellationToken)
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

        _templateCopy = CreateTemporaryWorksheetStream();
        using (var input = templatePart.GetStream(FileMode.Open, FileAccess.Read))
        {
            input.CopyTo(_templateCopy);
        }
        _templateCopy.Position = 0;
        _template = new StreamingWorksheet(
            worksheetName,
            _templateCopy,
            cancellationToken);
        _output = XmlWriter.Create(
            templatePart.GetStream(FileMode.Create, FileAccess.Write),
            WriterSettings());
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
                MergeMaximumColumnWidths(child, generatedColumns).WriteTo(_output);
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
        if (sourceIndex < _lastSourceRow)
        {
            throw new InvalidDataException(
                $"WorkingPaper worksheet '{_worksheetName}' direct SAX source rows 不得倒退；"
                + $"last={_lastSourceRow}, current={sourceIndex}。");
        }
        _lastSourceRow = sourceIndex;
        if (!_plan.IsDynamicRow(sourceIndex))
        {
            return;
        }

        while (_templateRow is not null && RowIndex(_templateRow) < sourceIndex)
        {
            WriteTemplateOnlyRow(_templateRow);
            _templateRow = _template.ReadNextRow(_cancellationToken);
        }

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
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _output.Dispose();
        _template.Dispose();
        _templateCopy.Dispose();
        _disposed = true;
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

    private static FileStream CreateTemporaryWorksheetStream()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $".jet-workpaper-worksheet-{Guid.NewGuid():N}.tmp");
        return new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan | FileOptions.DeleteOnClose);
    }

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
