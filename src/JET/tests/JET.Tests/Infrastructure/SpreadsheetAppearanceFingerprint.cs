using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Test-only, value-blind OpenXML appearance oracle. Worksheet XML is streamed and cell
/// subtrees are skipped after reading only the address and style attributes.
/// </summary>
internal sealed class SpreadsheetAppearanceFingerprint
{
    internal const uint ExplicitCellRowLimit = 200;

    private const string WorkbookBaseline = "$workbook";
    private const string RelationshipNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string SpreadsheetNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string StrictSpreadsheetNamespace =
        "http://purl.oclc.org/ooxml/spreadsheetml/main";

    private static readonly IReadOnlySet<string> PageSetupNumericAttributes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "paperSize", "scale", "firstPageNumber", "fitToWidth", "fitToHeight",
            "horizontalDpi", "verticalDpi", "copies"
        };

    private static readonly IReadOnlySet<string> PageSetupBooleanAttributes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "usePrinterDefaults", "blackAndWhite", "draft", "useFirstPageNumber"
        };

    private static readonly IReadOnlySet<string> PageSetupAllowedAttributes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "paperSize", "scale", "firstPageNumber", "fitToWidth", "fitToHeight",
            "pageOrder", "orientation", "usePrinterDefaults", "blackAndWhite", "draft",
            "cellComments", "useFirstPageNumber", "errors", "horizontalDpi", "verticalDpi",
            "copies"
        };

    private static readonly IReadOnlySet<string> SheetProtectionBooleanAttributes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "sheet", "objects", "scenarios", "formatCells", "formatColumns", "formatRows",
            "insertColumns", "insertRows", "insertHyperlinks", "deleteColumns", "deleteRows",
            "selectLockedCells", "sort", "autoFilter", "pivotTables", "selectUnlockedCells"
        };

    private static readonly IReadOnlyDictionary<string, string> PageSetupDefaults =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["paperSize"] = "1",
            ["scale"] = "100",
            ["firstPageNumber"] = "1",
            ["fitToWidth"] = "1",
            ["fitToHeight"] = "1",
            ["pageOrder"] = "downThenOver",
            ["orientation"] = "default",
            ["usePrinterDefaults"] = "true",
            ["blackAndWhite"] = "false",
            ["draft"] = "false",
            ["cellComments"] = "none",
            ["useFirstPageNumber"] = "false",
            ["errors"] = "displayed",
            ["horizontalDpi"] = "600",
            ["verticalDpi"] = "600",
            ["copies"] = "1"
        };

    private static readonly IReadOnlyDictionary<string, string> SheetProtectionDefaults =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sheet"] = "false",
            ["objects"] = "false",
            ["scenarios"] = "false",
            ["formatCells"] = "true",
            ["formatColumns"] = "true",
            ["formatRows"] = "true",
            ["insertColumns"] = "true",
            ["insertRows"] = "true",
            ["insertHyperlinks"] = "true",
            ["deleteColumns"] = "true",
            ["deleteRows"] = "true",
            ["selectLockedCells"] = "false",
            ["sort"] = "true",
            ["autoFilter"] = "true",
            ["pivotTables"] = "true",
            ["selectUnlockedCells"] = "false"
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly IReadOnlyDictionary<AppearanceKey, string> _properties;
    private readonly IReadOnlyDictionary<string, string> _baseline;

    private SpreadsheetAppearanceFingerprint(
        IReadOnlyDictionary<AppearanceKey, string> properties,
        IReadOnlyDictionary<string, string> baseline)
    {
        _properties = properties;
        _baseline = baseline;
        Entries = properties
            .OrderBy(item => item.Key.Location, StringComparer.Ordinal)
            .ThenBy(item => item.Key.Property, StringComparer.Ordinal)
            .Select(item => new AppearanceEntry(item.Key.Location, item.Key.Property, item.Value))
            .ToArray();
        Json = SerializeEntries(Entries);
    }

    internal IReadOnlyList<AppearanceEntry> Entries { get; }

    internal string Json { get; }

    internal byte[] JsonBytes => Encoding.UTF8.GetBytes(Json);

    internal static string SerializeEntries(IReadOnlyList<AppearanceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return JsonSerializer.Serialize(
            new AppearanceDocument(1, entries),
            JsonOptions) + "\n";
    }

    internal static bool IsKnownBuiltInNumberFormatCode(string value) =>
        BuiltInNumberFormats.Values.Contains(value, StringComparer.Ordinal);

    internal static SpreadsheetAppearanceFingerprint Capture(string path) =>
        Capture(path, excludedWorksheetNames: null);

    internal static SpreadsheetAppearanceFingerprint Capture(
        string path,
        IReadOnlySet<string>? excludedWorksheetNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("OpenXML workbook has no workbook part.");
        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet ?? CreateCanonicalStylesheet();
        var resolver = new StyleResolver(stylesheet, ThemePalette.Read(workbookPart));
        var properties = new Dictionary<AppearanceKey, string>();

        foreach (var (property, value) in resolver.Baseline)
        {
            if (!string.Equals(
                    CanonicalExcelDefaults.GetValueOrDefault(property),
                    value,
                    StringComparison.Ordinal))
            {
                properties.Add(new AppearanceKey(WorkbookBaseline, $"default.{property}"), value);
            }
        }

        var sheets = workbookPart.Workbook.Sheets?.Elements<Sheet>().ToArray()
            ?? throw new InvalidDataException("OpenXML workbook has no worksheets.");
        foreach (var sheet in sheets)
        {
            var name = sheet.Name?.Value
                ?? throw new InvalidDataException("OpenXML worksheet has no name.");
            if (excludedWorksheetNames?.Contains(name) == true)
            {
                continue;
            }
            var relationshipId = sheet.Id?.Value
                ?? throw new InvalidDataException($"OpenXML worksheet '{name}' has no relationship id.");
            if (workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
            {
                throw new InvalidDataException($"OpenXML worksheet '{name}' has no worksheet part.");
            }

            CaptureWorksheet(worksheetPart, name, resolver, properties);
        }

        return new SpreadsheetAppearanceFingerprint(properties, resolver.Baseline);
    }

    internal static SpreadsheetAppearanceFingerprint LoadJson(
        ReadOnlyMemory<byte> bytes,
        LegacyReportKind report)
    {
        var entries = SpreadsheetAppearanceSnapshotSchemaGuard.ReadEntries(bytes, report);
        var properties = entries.ToDictionary(
            entry => new AppearanceKey(entry.Location, entry.Property),
            entry => entry.Value);
        var baseline = new Dictionary<string, string>(CanonicalExcelDefaults, StringComparer.Ordinal);
        foreach (var entry in entries.Where(entry =>
                     entry.Location == WorkbookBaseline
                     && entry.Property.StartsWith("default.", StringComparison.Ordinal)))
        {
            baseline[entry.Property["default.".Length..]] = entry.Value;
        }
        return new SpreadsheetAppearanceFingerprint(properties, baseline);
    }

    internal string? DescribeFirstDifference(SpreadsheetAppearanceFingerprint actual)
    {
        var difference = DescribeDifferences(actual, maximumCount: 1).SingleOrDefault();
        return difference is null
            ? null
            : $"{difference.Location} {difference.Property} "
                + $"expected={difference.ExpectedValue} actual={difference.ActualValue}";
    }

    internal IReadOnlyList<SpreadsheetAppearanceDifference> DescribeDifferences(
        SpreadsheetAppearanceFingerprint actual,
        int maximumCount = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(actual);
        if (maximumCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        var differences = new List<SpreadsheetAppearanceDifference>();
        var keys = _properties.Keys
            .Concat(actual._properties.Keys)
            .Distinct()
            .OrderBy(key => key.Location, StringComparer.Ordinal)
            .ThenBy(key => key.Property, StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var expectedValue = EffectiveValue(key);
            var actualValue = actual.EffectiveValue(key);
            if (string.Equals(expectedValue, actualValue, StringComparison.Ordinal))
            {
                continue;
            }

            differences.Add(new SpreadsheetAppearanceDifference(
                key.Location,
                key.Property,
                expectedValue,
                actualValue));
            if (differences.Count == maximumCount)
            {
                break;
            }
        }

        return differences;
    }

    internal int CountDifferences(SpreadsheetAppearanceFingerprint actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        var count = 0;
        foreach (var key in _properties.Keys.Concat(actual._properties.Keys).Distinct())
        {
            if (!string.Equals(
                    EffectiveValue(key),
                    actual.EffectiveValue(key),
                    StringComparison.Ordinal))
            {
                count = checked(count + 1);
            }
        }
        return count;
    }

    internal IReadOnlyList<SpreadsheetAppearanceDifferenceGroup> DescribeDifferenceGroups(
        SpreadsheetAppearanceFingerprint actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        var groups = new Dictionary<SpreadsheetAppearanceDifferenceGroupKey, DifferenceAccumulator>();
        var keys = _properties.Keys
            .Concat(actual._properties.Keys)
            .Distinct()
            .OrderBy(key => key.Location, StringComparer.Ordinal)
            .ThenBy(key => key.Property, StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var expectedValue = EffectiveValue(key);
            var actualValue = actual.EffectiveValue(key);
            if (string.Equals(expectedValue, actualValue, StringComparison.Ordinal))
            {
                continue;
            }

            var (sheetName, scopeKind) = DifferenceScope(key.Location);
            var groupKey = new SpreadsheetAppearanceDifferenceGroupKey(
                sheetName,
                scopeKind,
                key.Property,
                expectedValue,
                actualValue);
            if (!groups.TryGetValue(groupKey, out var accumulator))
            {
                groups.Add(groupKey, new DifferenceAccumulator(1, key.Location));
            }
            else
            {
                groups[groupKey] = accumulator with
                {
                    Count = checked(accumulator.Count + 1),
                };
            }
        }

        return groups
            .Select(item => new SpreadsheetAppearanceDifferenceGroup(
                item.Key.SheetName,
                item.Key.ScopeKind,
                item.Key.Property,
                item.Key.ExpectedValue,
                item.Key.ActualValue,
                item.Value.Count,
                item.Value.FirstLocation))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.SheetName, StringComparer.Ordinal)
            .ThenBy(item => item.ScopeKind, StringComparer.Ordinal)
            .ThenBy(item => item.Property, StringComparer.Ordinal)
            .ThenBy(item => item.ExpectedValue, StringComparer.Ordinal)
            .ThenBy(item => item.ActualValue, StringComparer.Ordinal)
            .ToArray();
    }

    private static (string SheetName, string ScopeKind) DifferenceScope(string location)
    {
        if (location == WorkbookBaseline)
        {
            return (WorkbookBaseline, "workbook");
        }
        var separator = location.IndexOf('!');
        var sheetName = separator > 0 ? location[..separator] : "<invalid>";
        var scope = separator >= 0 && separator < location.Length - 1
            ? location[(separator + 1)..]
            : string.Empty;
        var scopeKind = scope switch
        {
            "cellStream" => "cellStream",
            "sheetFormat" => "sheetFormat",
            "pageSetup" => "pageSetup",
            "sheetProtection" => "sheetProtection",
            _ when scope.StartsWith("column:", StringComparison.Ordinal) => "column",
            _ when scope.StartsWith("row:", StringComparison.Ordinal) => "row",
            _ when scope.StartsWith("merge:", StringComparison.Ordinal) => "merge",
            _ when scope.StartsWith("sheetView:", StringComparison.Ordinal) => "sheetView",
            _ => "cell",
        };
        return (sheetName, scopeKind);
    }

    internal string? Value(string location, string property) =>
        _properties.GetValueOrDefault(new AppearanceKey(location, property));

    private string EffectiveValue(AppearanceKey key)
    {
        if (_properties.TryGetValue(key, out var value))
        {
            return value;
        }

        if (key.Location == WorkbookBaseline &&
            key.Property.StartsWith("default.", StringComparison.Ordinal))
        {
            return CanonicalExcelDefaults.GetValueOrDefault(
                key.Property["default.".Length..],
                "<default>");
        }

        var semanticProperty = key.Property.StartsWith("style.", StringComparison.Ordinal)
            ? key.Property["style.".Length..]
            : key.Property;
        return _baseline.GetValueOrDefault(semanticProperty, "<default>");
    }

    private static void CaptureWorksheet(
        WorksheetPart worksheetPart,
        string sheetName,
        StyleResolver resolver,
        Dictionary<AppearanceKey, string> properties)
    {
        var state = new WorksheetCaptureState();
        using var cellHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var styleSignatures = new Dictionary<uint, string>();
        IReadOnlyDictionary<uint, uint>? columnStyles = null;
        uint? currentRowIndex = null;
        uint? currentRowStyle = null;
        ulong cellCount = 0;

        IReadOnlyDictionary<uint, uint> EnsureColumnStyles() =>
            columnStyles ??= CaptureColumns(sheetName, state, resolver, properties);

        using var stream = worksheetPart.GetStream(FileMode.Open, FileAccess.Read);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true
        });

        reader.MoveToContent();
        var viewIndex = 0;
        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            if (reader.NamespaceURI is not SpreadsheetNamespace and not StrictSpreadsheetNamespace)
            {
                reader.Read();
                continue;
            }

            switch (reader.LocalName)
            {
                case "c":
                    CaptureCellAppearance(
                        reader,
                        sheetName,
                        currentRowIndex,
                        currentRowStyle,
                        EnsureColumnStyles(),
                        resolver,
                        styleSignatures,
                        cellHash,
                        properties,
                        ref cellCount);
                    reader.Skip();
                    continue;
                case "col":
                    state.Columns.Add(ReadColumn(reader, sheetName));
                    break;
                case "row":
                    var row = ReadRow(reader, sheetName);
                    currentRowIndex = row.Index;
                    currentRowStyle = row.StyleIndex;
                    CaptureRowAppearance(sheetName, row, state, resolver, properties);
                    break;
                case "sheetFormatPr":
                    CaptureSheetFormat(reader, sheetName, state, properties);
                    break;
                case "mergeCell":
                    if (reader.GetAttribute("ref") is { Length: > 0 } mergedRange)
                    {
                        properties[new AppearanceKey($"{sheetName}!merge:{mergedRange}", "present")] = "true";
                    }
                    break;
                case "sheetView":
                    viewIndex++;
                    CaptureSheetView(reader, sheetName, viewIndex, properties);
                    break;
                case "pane":
                    CapturePane(reader, sheetName, viewIndex, properties);
                    break;
                case "pageSetup":
                    CaptureAttributes(
                        reader,
                        properties,
                        $"{sheetName}!pageSetup",
                        ignoredLocalNames: new[] { "id" },
                        allowedLocalNames: PageSetupAllowedAttributes);
                    break;
                case "sheetProtection":
                    CaptureAttributes(
                        reader,
                        properties,
                        $"{sheetName}!sheetProtection",
                        ignoredLocalNames: Array.Empty<string>(),
                        allowedLocalNames: SheetProtectionBooleanAttributes);
                    break;
            }

            reader.Read();
        }

        _ = EnsureColumnStyles();
        var cellStreamLocation = $"{sheetName}!cellStream";
        properties[new AppearanceKey(cellStreamLocation, "cellCount")] =
            cellCount.ToString(CultureInfo.InvariantCulture);
        properties[new AppearanceKey(cellStreamLocation, "sha256")] =
            Convert.ToHexString(cellHash.GetHashAndReset());
    }

    private static void CaptureCellAppearance(
        XmlReader reader,
        string sheetName,
        uint? currentRowIndex,
        uint? currentRowStyle,
        IReadOnlyDictionary<uint, uint> columnStyles,
        StyleResolver resolver,
        Dictionary<uint, string> styleSignatures,
        IncrementalHash cellHash,
        Dictionary<AppearanceKey, string> properties,
        ref ulong cellCount)
    {
        var cell = ReadCell(reader, sheetName);
        var (column, row) = ParseCellReference(cell.Reference, sheetName);
        var inheritedRowStyle = currentRowIndex == row ? currentRowStyle : null;
        var styleIndex = cell.StyleIndex
            ?? inheritedRowStyle
            ?? columnStyles.GetValueOrDefault(column, 0U);
        if (!styleSignatures.TryGetValue(styleIndex, out var signature))
        {
            signature = resolver.StyleSignature(styleIndex);
            styleSignatures.Add(styleIndex, signature);
        }

        AppendCellHashToken(cellHash, cell.Reference, signature);
        cellCount = checked(cellCount + 1UL);
        if (row <= ExplicitCellRowLimit)
        {
            AddStyleDeltas(
                properties,
                $"{sheetName}!{cell.Reference}",
                string.Empty,
                resolver,
                styleIndex);
        }
    }

    private static void AppendCellHashToken(
        IncrementalHash cellHash,
        string reference,
        string styleSignature)
    {
        var token = string.Create(
            CultureInfo.InvariantCulture,
            $"{reference.Length}:{reference}{styleSignature.Length}:{styleSignature}\n");
        cellHash.AppendData(Encoding.UTF8.GetBytes(token));
    }

    private static CellDescriptor ReadCell(
        XmlReader reader,
        string sheetName)
    {
        var reference = reader.GetAttribute("r")
            ?? throw new InvalidDataException($"Worksheet '{sheetName}' has a cell without an address.");
        var rawStyle = reader.GetAttribute("s");
        return new CellDescriptor(
            reference.ToUpperInvariant(),
            rawStyle is null ? null : ParseUInt(rawStyle, "cell style"));
    }

    private static ColumnDescriptor ReadColumn(
        XmlReader reader,
        string sheetName)
    {
        var min = ParseUInt(
            reader.GetAttribute("min")
                ?? throw new InvalidDataException($"Worksheet '{sheetName}' has a column without min."),
            "column min");
        var max = ParseUInt(reader.GetAttribute("max") ?? min.ToString(CultureInfo.InvariantCulture), "column max");
        if (min == 0U || max < min || max > 16_384U)
        {
            throw new InvalidDataException(
                $"Worksheet '{sheetName}' has invalid column range {min}-{max}.");
        }

        return new ColumnDescriptor(
            min,
            max,
            reader.GetAttribute("width") is { } width ? CanonicalNumber(width, "column width") : null,
            reader.GetAttribute("hidden") is { } hidden && XmlBoolean(hidden),
            reader.GetAttribute("style") is { } style ? ParseUInt(style, "column style") : 0U);
    }

    private static RowDescriptor ReadRow(
        XmlReader reader,
        string sheetName)
    {
        var index = ParseUInt(
            reader.GetAttribute("r")
                ?? throw new InvalidDataException($"Worksheet '{sheetName}' has a row without an index."),
            "row index");
        if (index == 0U || index > 1_048_576U)
        {
            throw new InvalidDataException($"Worksheet '{sheetName}' has invalid row index {index}.");
        }

        return new RowDescriptor(
            index,
            reader.GetAttribute("ht") is { } height ? CanonicalNumber(height, "row height") : null,
            reader.GetAttribute("hidden") is { } hidden && XmlBoolean(hidden),
            reader.GetAttribute("customFormat") is { } customFormat && XmlBoolean(customFormat) &&
            reader.GetAttribute("s") is { } style
                ? ParseUInt(style, "row style")
                : null);
    }

    private static void CaptureSheetFormat(
        XmlReader reader,
        string sheetName,
        WorksheetCaptureState state,
        Dictionary<AppearanceKey, string> properties)
    {
        const string canonicalDefaultRowHeight = "15";
        const string canonicalDefaultColumnWidth = "8.43";
        const string canonicalBaseColumnWidth = "8";
        var location = $"{sheetName}!sheetFormat";

        if (reader.GetAttribute("defaultRowHeight") is { } rowHeight)
        {
            state.DefaultRowHeight = CanonicalNumber(rowHeight, "default row height");
            if (!string.Equals(state.DefaultRowHeight, canonicalDefaultRowHeight, StringComparison.Ordinal))
            {
                properties[new AppearanceKey(location, "defaultRowHeight")] = state.DefaultRowHeight;
            }
        }

        if (reader.GetAttribute("defaultColWidth") is { } columnWidth)
        {
            state.DefaultColumnWidth = CanonicalNumber(columnWidth, "default column width");
            if (!string.Equals(state.DefaultColumnWidth, canonicalDefaultColumnWidth, StringComparison.Ordinal))
            {
                properties[new AppearanceKey(location, "defaultColumnWidth")] = state.DefaultColumnWidth;
            }
        }

        if (reader.GetAttribute("baseColWidth") is { } baseColumnWidth)
        {
            var canonical = CanonicalNumber(baseColumnWidth, "base column width");
            if (!string.Equals(canonical, canonicalBaseColumnWidth, StringComparison.Ordinal))
            {
                properties[new AppearanceKey(location, "baseColumnWidth")] = canonical;
            }
        }

        AddTrue(properties, location, "zeroHeight", reader.GetAttribute("zeroHeight"));
    }

    private static IReadOnlyDictionary<uint, uint> CaptureColumns(
        string sheetName,
        WorksheetCaptureState state,
        StyleResolver resolver,
        Dictionary<AppearanceKey, string> properties)
    {
        var effective = new SortedDictionary<uint, ColumnAppearance>();
        foreach (var column in state.Columns)
        {
            var appearance = new ColumnAppearance(
                column.Width ?? state.DefaultColumnWidth,
                column.Hidden,
                column.StyleIndex,
                resolver.StyleSignature(column.StyleIndex));
            for (var index = column.Min; index <= column.Max; index++)
            {
                effective[index] = appearance;
            }
        }

        uint? runStart = null;
        uint previous = 0U;
        ColumnAppearance? runAppearance = null;
        foreach (var (index, appearance) in effective)
        {
            if (runStart is not null &&
                index == previous + 1U &&
                runAppearance!.HasSameEffectiveAppearance(appearance))
            {
                previous = index;
                continue;
            }

            if (runStart is not null)
            {
                EmitColumnRun(
                    sheetName,
                    runStart.Value,
                    previous,
                    runAppearance!,
                    state.DefaultColumnWidth,
                    resolver,
                    properties);
            }

            runStart = index;
            previous = index;
            runAppearance = appearance;
        }

        if (runStart is not null)
        {
            EmitColumnRun(
                sheetName,
                runStart.Value,
                previous,
                runAppearance!,
                state.DefaultColumnWidth,
                resolver,
                properties);
        }

        return effective.ToDictionary(item => item.Key, item => item.Value.StyleIndex);
    }

    private static void EmitColumnRun(
        string sheetName,
        uint start,
        uint end,
        ColumnAppearance appearance,
        string defaultColumnWidth,
        StyleResolver resolver,
        Dictionary<AppearanceKey, string> properties)
    {
        var location = $"{sheetName}!column:{start}-{end}";
        if (!string.Equals(appearance.Width, defaultColumnWidth, StringComparison.Ordinal))
        {
            properties[new AppearanceKey(location, "width")] = appearance.Width;
        }

        if (appearance.Hidden)
        {
            properties[new AppearanceKey(location, "hidden")] = "true";
        }

        AddStyleDeltas(properties, location, "style.", resolver, appearance.StyleIndex);
    }

    private static void CaptureRowAppearance(
        string sheetName,
        RowDescriptor row,
        WorksheetCaptureState state,
        StyleResolver resolver,
        Dictionary<AppearanceKey, string> properties)
    {
        var location = $"{sheetName}!row:{row.Index}";
        if (row.Height is not null &&
            !string.Equals(row.Height, state.DefaultRowHeight, StringComparison.Ordinal))
        {
            properties[new AppearanceKey(location, "height")] = row.Height;
        }

        if (row.Hidden)
        {
            properties[new AppearanceKey(location, "hidden")] = "true";
        }

        if (row.StyleIndex is { } styleIndex)
        {
            AddStyleDeltas(properties, location, "style.", resolver, styleIndex);
        }
    }

    private static void AddStyleDeltas(
        Dictionary<AppearanceKey, string> properties,
        string location,
        string propertyPrefix,
        StyleResolver resolver,
        uint styleIndex)
    {
        foreach (var (property, value) in resolver.Deltas(styleIndex))
        {
            properties[new AppearanceKey(location, propertyPrefix + property)] = value;
        }
    }

    private static void CaptureSheetView(
        XmlReader reader,
        string sheetName,
        int viewIndex,
        Dictionary<AppearanceKey, string> properties)
    {
        var location = $"{sheetName}!sheetView:{viewIndex}";
        if (reader.GetAttribute("showGridLines") is { } gridLines && !XmlBoolean(gridLines))
        {
            properties[new AppearanceKey(location, "showGridLines")] = "false";
        }

        if (reader.GetAttribute("zoomScale") is { } zoom)
        {
            var canonicalZoom = CanonicalNumber(zoom, "zoom scale");
            if (canonicalZoom != "100")
            {
                properties[new AppearanceKey(location, "zoomScale")] = canonicalZoom;
            }
        }
    }

    private static void CapturePane(
        XmlReader reader,
        string sheetName,
        int viewIndex,
        Dictionary<AppearanceKey, string> properties)
    {
        var location = $"{sheetName}!sheetView:{Math.Max(viewIndex, 1)}.pane";
        foreach (var attribute in new[] { "state", "xSplit", "ySplit", "topLeftCell", "activePane" })
        {
            var value = reader.GetAttribute(attribute);
            if (value is not null && attribute is "xSplit" or "ySplit")
            {
                value = CanonicalNumber(value, $"pane {attribute}");
            }

            AddIfPresent(properties, location, attribute, value);
        }
    }

    private static void CaptureAttributes(
        XmlReader reader,
        Dictionary<AppearanceKey, string> properties,
        string location,
        IReadOnlyList<string> ignoredLocalNames,
        IReadOnlySet<string>? allowedLocalNames)
    {
        if (!reader.HasAttributes)
        {
            return;
        }

        while (reader.MoveToNextAttribute())
        {
            if (reader.Prefix == "xmlns" ||
                ignoredLocalNames.Contains(reader.LocalName) ||
                allowedLocalNames is not null && !allowedLocalNames.Contains(reader.LocalName) ||
                reader.NamespaceURI == RelationshipNamespace)
            {
                continue;
            }

            var normalized = NormalizeAttribute(
                location,
                reader.LocalName,
                reader.Value);
            if (!IsDefaultAttribute(location, reader.LocalName, normalized))
            {
                properties[new AppearanceKey(location, reader.LocalName)] = normalized;
            }
        }

        reader.MoveToElement();
    }

    private static void AddIfPresent(
        Dictionary<AppearanceKey, string> properties,
        string location,
        string property,
        string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            properties[new AppearanceKey(location, property)] = value;
        }
    }

    private static void AddTrue(
        Dictionary<AppearanceKey, string> properties,
        string location,
        string property,
        string? rawValue)
    {
        if (rawValue is not null && XmlBoolean(rawValue))
        {
            properties[new AppearanceKey(location, property)] = "true";
        }
    }

    private static string NormalizeAttribute(string location, string name, string value)
    {
        if (location.EndsWith("!pageSetup", StringComparison.Ordinal) &&
            PageSetupNumericAttributes.Contains(name))
        {
            return CanonicalNumber(value, $"page setup {name}");
        }

        if ((location.EndsWith("!pageSetup", StringComparison.Ordinal) &&
             PageSetupBooleanAttributes.Contains(name)) ||
            (location.EndsWith("!sheetProtection", StringComparison.Ordinal) &&
             SheetProtectionBooleanAttributes.Contains(name)))
        {
            return XmlBoolean(value) ? "true" : "false";
        }

        if (location.EndsWith("!sheetProtection", StringComparison.Ordinal) && name == "spinCount")
        {
            return CanonicalNumber(value, "sheet protection spin count");
        }

        return value;
    }

    private static bool IsDefaultAttribute(string location, string name, string value)
    {
        var defaults = location.EndsWith("!pageSetup", StringComparison.Ordinal)
            ? PageSetupDefaults
            : location.EndsWith("!sheetProtection", StringComparison.Ordinal)
                ? SheetProtectionDefaults
                : null;
        return defaults is not null &&
               defaults.TryGetValue(name, out var defaultValue) &&
               string.Equals(value, defaultValue, StringComparison.Ordinal);
    }

    private static uint ParseUInt(string value, string kind)
    {
        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidDataException($"Invalid {kind} '{value}'.");
        }

        return parsed;
    }

    private static string CanonicalNumber(string value, string kind)
    {
        if (!decimal.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new InvalidDataException($"Invalid {kind} '{value}'.");
        }

        return parsed.ToString("0.############################", CultureInfo.InvariantCulture);
    }

    private static (uint Column, uint Row) ParseCellReference(string reference, string sheetName)
    {
        var index = 0;
        if (reference.StartsWith('$'))
        {
            index++;
        }

        uint column = 0U;
        var letterCount = 0;
        while (index < reference.Length && char.IsAsciiLetter(reference[index]))
        {
            column = checked(column * 26U + (uint)(char.ToUpperInvariant(reference[index]) - 'A' + 1));
            index++;
            letterCount++;
        }

        if (index < reference.Length && reference[index] == '$')
        {
            index++;
        }

        if (letterCount == 0 ||
            column == 0U ||
            column > 16_384U ||
            !uint.TryParse(reference.AsSpan(index), NumberStyles.None, CultureInfo.InvariantCulture, out var row) ||
            row == 0U ||
            row > 1_048_576U)
        {
            throw new InvalidDataException(
                $"Worksheet '{sheetName}' has invalid cell reference '{reference}'.");
        }

        return (column, row);
    }

    private static bool XmlBoolean(string value) =>
        value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);

    private sealed class StyleResolver
    {
        private static readonly string[] SemanticProperties =
        [
            "font.name", "font.size", "font.bold", "font.italic", "font.colorArgb",
            "fill.pattern", "fill.foregroundArgb",
            "border.left.style", "border.left.colorArgb",
            "border.right.style", "border.right.colorArgb",
            "border.top.style", "border.top.colorArgb",
            "border.bottom.style", "border.bottom.colorArgb",
            "numberFormat.code",
            "alignment.horizontal", "alignment.vertical", "alignment.wrapText",
            "alignment.indent", "alignment.shrinkToFit",
            "protection.locked"
        ];

        private readonly ThemePalette _theme;
        private readonly IReadOnlyList<CellFormat> _cellFormats;
        private readonly IReadOnlyList<CellFormat> _styleFormats;
        private readonly IReadOnlyList<DocumentFormat.OpenXml.Spreadsheet.Font> _fonts;
        private readonly IReadOnlyList<Fill> _fills;
        private readonly IReadOnlyList<Border> _borders;
        private readonly Dictionary<uint, string> _numberFormats;
        private readonly string?[] _indexedPalette;
        private readonly Dictionary<uint, IReadOnlyDictionary<string, string>> _cache = [];

        internal StyleResolver(Stylesheet stylesheet, ThemePalette theme)
        {
            _theme = theme;
            var canonical = CreateCanonicalStylesheet();
            _cellFormats = stylesheet.CellFormats?.Elements<CellFormat>().ToArray() is { Length: > 0 } cellFormats
                ? cellFormats
                : canonical.CellFormats!.Elements<CellFormat>().ToArray();
            _styleFormats = stylesheet.CellStyleFormats?.Elements<CellFormat>().ToArray() is { Length: > 0 } styleFormats
                ? styleFormats
                : canonical.CellStyleFormats!.Elements<CellFormat>().ToArray();
            _fonts = stylesheet.Fonts?.Elements<DocumentFormat.OpenXml.Spreadsheet.Font>().ToArray() is { Length: > 0 } fonts
                ? fonts
                : canonical.Fonts!.Elements<DocumentFormat.OpenXml.Spreadsheet.Font>().ToArray();
            _fills = stylesheet.Fills?.Elements<Fill>().ToArray() is { Length: > 0 } fills
                ? fills
                : canonical.Fills!.Elements<Fill>().ToArray();
            _borders = stylesheet.Borders?.Elements<Border>().ToArray() is { Length: > 0 } borders
                ? borders
                : canonical.Borders!.Elements<Border>().ToArray();
            _numberFormats = stylesheet.NumberingFormats?.Elements<NumberingFormat>()
                .Where(format => format.NumberFormatId is not null && format.FormatCode is not null)
                .ToDictionary(
                    format => format.NumberFormatId!.Value,
                    format => format.FormatCode!.Value!,
                    EqualityComparer<uint>.Default) ?? [];
            _indexedPalette = stylesheet.Colors?.IndexedColors?
                .Elements<RgbColor>()
                .Select(color => color.Rgb?.Value)
                .ToArray() ?? [];
            Baseline = Resolve(0);
        }

        internal IReadOnlyDictionary<string, string> Baseline { get; }

        internal IReadOnlyDictionary<string, string> Deltas(uint styleIndex)
        {
            var style = Resolve(styleIndex);
            return SemanticProperties
                .Where(property => !string.Equals(
                    Baseline[property],
                    style[property],
                    StringComparison.Ordinal))
                .ToDictionary(property => property, property => style[property], StringComparer.Ordinal);
        }

        internal string StyleSignature(uint styleIndex) => string.Join(
            '\u001F',
            Resolve(styleIndex)
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"{item.Key}={item.Value}"));

        internal IReadOnlyDictionary<string, string> Resolve(uint styleIndex)
        {
            if (_cache.TryGetValue(styleIndex, out var cached))
            {
                return cached;
            }

            if (styleIndex >= _cellFormats.Count)
            {
                throw new InvalidDataException($"Cell references unknown style index {styleIndex}.");
            }

            var format = _cellFormats[checked((int)styleIndex)];
            var baseFormat = BaseFormat(format);
            var font = Require(_fonts, format.FontId?.Value ?? baseFormat?.FontId?.Value ?? 0U, "font");
            var fill = Require(_fills, format.FillId?.Value ?? baseFormat?.FillId?.Value ?? 0U, "fill");
            var border = Require(_borders, format.BorderId?.Value ?? baseFormat?.BorderId?.Value ?? 0U, "border");
            var alignment = format.Alignment ?? baseFormat?.Alignment;
            var protection = format.Protection ?? baseFormat?.Protection;
            var numberFormatId = format.NumberFormatId?.Value ?? baseFormat?.NumberFormatId?.Value ?? 0U;

            var fillPattern = fill.PatternFill?.PatternType?.InnerText ?? "none";
            var result = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["font.name"] = FontName(font),
                ["font.size"] = (font.FontSize?.Val?.Value ?? 11D).ToString("0.########", CultureInfo.InvariantCulture),
                ["font.bold"] = OnOff(font.Bold).ToString().ToLowerInvariant(),
                ["font.italic"] = OnOff(font.Italic).ToString().ToLowerInvariant(),
                ["font.colorArgb"] = ResolveColor(font.Color) ?? "FF000000",
                ["fill.pattern"] = fillPattern,
                ["fill.foregroundArgb"] = fillPattern is "none" or "gray125"
                    ? "00000000"
                    : ResolveColor(fill.PatternFill?.ForegroundColor) ?? "00000000",
                ["numberFormat.code"] = NumberFormatCode(numberFormatId),
                ["alignment.horizontal"] = alignment?.Horizontal?.InnerText ?? "general",
                ["alignment.vertical"] = alignment?.Vertical?.InnerText ?? "bottom",
                ["alignment.wrapText"] = (alignment?.WrapText?.Value ?? false).ToString().ToLowerInvariant(),
                ["alignment.indent"] = (alignment?.Indent?.Value ?? 0U).ToString(CultureInfo.InvariantCulture),
                ["alignment.shrinkToFit"] = (alignment?.ShrinkToFit?.Value ?? false).ToString().ToLowerInvariant(),
                ["protection.locked"] = (protection?.Locked?.Value ?? true).ToString().ToLowerInvariant()
            };
            AddBorder(result, "left", border.LeftBorder);
            AddBorder(result, "right", border.RightBorder);
            AddBorder(result, "top", border.TopBorder);
            AddBorder(result, "bottom", border.BottomBorder);
            _cache.Add(styleIndex, result);
            return result;
        }

        private CellFormat? BaseFormat(CellFormat format)
        {
            var index = format.FormatId?.Value ?? 0U;
            return index < _styleFormats.Count ? _styleFormats[checked((int)index)] : null;
        }

        private string FontName(DocumentFormat.OpenXml.Spreadsheet.Font font)
        {
            if (font.FontName?.Val?.Value is { Length: > 0 } explicitName)
            {
                return explicitName;
            }

            var scheme = font.FontScheme?.Val?.Value;
            if (scheme == FontSchemeValues.Major)
            {
                return _theme.MajorTypeface ?? "Calibri";
            }

            if (scheme == FontSchemeValues.Minor)
            {
                return _theme.MinorTypeface ?? "Calibri";
            }

            return "Calibri";
        }

        private string NumberFormatCode(uint id)
        {
            if (_numberFormats.TryGetValue(id, out var custom))
            {
                return custom;
            }

            return BuiltInNumberFormats.GetValueOrDefault(id, $"builtin:{id}");
        }

        private string? ResolveColor(ColorType? color)
        {
            if (color is null)
            {
                return null;
            }

            string? argb = null;
            if (color.Rgb?.Value is { } rgb)
            {
                argb = NormalizeArgb(rgb);
            }
            else if (color.Indexed?.Value is { } indexed)
            {
                argb = IndexedColor(indexed);
            }
            else if (color.Theme?.Value is { } themeIndex)
            {
                argb = _theme.Colors.GetValueOrDefault(themeIndex)
                    ?? throw new InvalidDataException($"Theme color {themeIndex} is not defined.");
            }
            else if (color.Auto?.Value == true)
            {
                argb = "FF000000";
            }

            return argb is null || color.Tint?.Value is not { } tint
                ? argb
                : ApplyTint(argb, tint);
        }

        private string IndexedColor(uint index)
        {
            if (index < _indexedPalette.Length && _indexedPalette[checked((int)index)] is { } workbookColor)
            {
                return NormalizeArgb(workbookColor);
            }

            if (index == 64U)
            {
                return "FF000000"; // system foreground / automatic
            }

            if (index == 65U)
            {
                return "FFFFFFFF"; // system background
            }

            if (index >= DefaultIndexedPalette.Length)
            {
                throw new InvalidDataException($"Indexed color {index} is outside the Excel palette.");
            }

            return DefaultIndexedPalette[checked((int)index)];
        }

        private void AddBorder(
            Dictionary<string, string> result,
            string side,
            BorderPropertiesType? border)
        {
            var style = border?.Style?.InnerText ?? "none";
            result[$"border.{side}.style"] = style;
            result[$"border.{side}.colorArgb"] = style == "none"
                ? "00000000"
                : ResolveColor(border?.Color) ?? "FF000000";
        }

        private static bool OnOff(BooleanPropertyType? property) =>
            property is not null && (property.Val?.Value ?? true);

        private static T Require<T>(IReadOnlyList<T> items, uint index, string kind)
        {
            if (index >= items.Count)
            {
                throw new InvalidDataException($"Cell format references unknown {kind} index {index}.");
            }

            return items[checked((int)index)];
        }
    }

    private sealed record ThemePalette(
        IReadOnlyDictionary<uint, string> Colors,
        string? MajorTypeface,
        string? MinorTypeface)
    {
        private static readonly (uint Index, string Element)[] ThemeColorMap =
        [
            (0, "lt1"), (1, "dk1"), (2, "lt2"), (3, "dk2"),
            (4, "accent1"), (5, "accent2"), (6, "accent3"), (7, "accent4"),
            (8, "accent5"), (9, "accent6"), (10, "hlink"), (11, "folHlink")
        ];

        internal static ThemePalette Read(WorkbookPart workbookPart)
        {
            var themePart = workbookPart.ThemePart;
            if (themePart is null)
            {
                return new ThemePalette(new Dictionary<uint, string>(), null, null);
            }

            using var stream = themePart.GetStream(FileMode.Open, FileAccess.Read);
            var document = XDocument.Load(stream, LoadOptions.None);
            XNamespace drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
            var scheme = document.Descendants(drawing + "clrScheme").SingleOrDefault();
            var colors = new Dictionary<uint, string>();
            if (scheme is not null)
            {
                foreach (var (index, elementName) in ThemeColorMap)
                {
                    var colorElement = scheme.Element(drawing + elementName)?.Elements().SingleOrDefault();
                    var value = colorElement?.Name.LocalName == "sysClr"
                        ? colorElement.Attribute("lastClr")?.Value
                        : colorElement?.Attribute("val")?.Value;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        colors[index] = NormalizeArgb(value);
                    }
                }
            }

            var major = document.Descendants(drawing + "majorFont")
                .Elements(drawing + "latin")
                .SingleOrDefault()?.Attribute("typeface")?.Value;
            var minor = document.Descendants(drawing + "minorFont")
                .Elements(drawing + "latin")
                .SingleOrDefault()?.Attribute("typeface")?.Value;
            return new ThemePalette(colors, major, minor);
        }
    }

    private static Stylesheet CreateCanonicalStylesheet() => new(
        new Fonts(
            new DocumentFormat.OpenXml.Spreadsheet.Font(
                new FontSize { Val = 11D },
                new Color { Rgb = "FF000000" },
                new FontName { Val = "Calibri" }))
        { Count = 1U },
        new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }))
        { Count = 2U },
        new Borders(
            new Border(
                new LeftBorder(),
                new RightBorder(),
                new TopBorder(),
                new BottomBorder(),
                new DiagonalBorder()))
        { Count = 1U },
        new CellStyleFormats(
            new CellFormat { FontId = 0U, FillId = 0U, BorderId = 0U, NumberFormatId = 0U })
        { Count = 1U },
        new CellFormats(
            new CellFormat { FontId = 0U, FillId = 0U, BorderId = 0U, NumberFormatId = 0U })
        { Count = 1U },
        new CellStyles(new CellStyle { Name = "Normal", FormatId = 0U, BuiltinId = 0U })
        { Count = 1U });

    private static string NormalizeArgb(string value)
    {
        var hex = value.Trim().TrimStart('#').ToUpperInvariant();
        return hex.Length switch
        {
            6 => "FF" + hex,
            8 => hex,
            _ => throw new InvalidDataException($"Invalid RGB color '{value}'.")
        };
    }

    private static string ApplyTint(string argb, double tint)
    {
        if (tint is < -1D or > 1D)
        {
            throw new InvalidDataException($"Excel tint {tint} is outside [-1, 1].");
        }

        var red = byte.Parse(argb.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var green = byte.Parse(argb.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var blue = byte.Parse(argb.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        RgbToHsl(red / 255D, green / 255D, blue / 255D, out var hue, out var saturation, out var lightness);
        lightness = tint < 0D
            ? lightness * (1D + tint)
            : lightness * (1D - tint) + tint;
        HslToRgb(hue, saturation, lightness, out var tintedRed, out var tintedGreen, out var tintedBlue);
        return $"{argb[..2]}{ToByte(tintedRed):X2}{ToByte(tintedGreen):X2}{ToByte(tintedBlue):X2}";
    }

    private static void RgbToHsl(
        double red,
        double green,
        double blue,
        out double hue,
        out double saturation,
        out double lightness)
    {
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        lightness = (max + min) / 2D;
        if (Math.Abs(max - min) < double.Epsilon)
        {
            hue = 0D;
            saturation = 0D;
            return;
        }

        var delta = max - min;
        saturation = lightness > 0.5D
            ? delta / (2D - max - min)
            : delta / (max + min);
        hue = max == red
            ? (green - blue) / delta + (green < blue ? 6D : 0D)
            : max == green
                ? (blue - red) / delta + 2D
                : (red - green) / delta + 4D;
        hue /= 6D;
    }

    private static void HslToRgb(
        double hue,
        double saturation,
        double lightness,
        out double red,
        out double green,
        out double blue)
    {
        if (Math.Abs(saturation) < double.Epsilon)
        {
            red = green = blue = lightness;
            return;
        }

        var q = lightness < 0.5D
            ? lightness * (1D + saturation)
            : lightness + saturation - lightness * saturation;
        var p = 2D * lightness - q;
        red = HueToRgb(p, q, hue + 1D / 3D);
        green = HueToRgb(p, q, hue);
        blue = HueToRgb(p, q, hue - 1D / 3D);
    }

    private static double HueToRgb(double p, double q, double hue)
    {
        if (hue < 0D) hue += 1D;
        if (hue > 1D) hue -= 1D;
        if (hue < 1D / 6D) return p + (q - p) * 6D * hue;
        if (hue < 0.5D) return q;
        if (hue < 2D / 3D) return p + (q - p) * (2D / 3D - hue) * 6D;
        return p;
    }

    private static byte ToByte(double value) =>
        checked((byte)Math.Clamp(Math.Round(value * 255D, MidpointRounding.AwayFromZero), 0D, 255D));

    private static readonly IReadOnlyDictionary<uint, string> BuiltInNumberFormats =
        new Dictionary<uint, string>
        {
            [0] = "General", [1] = "0", [2] = "0.00", [3] = "#,##0", [4] = "#,##0.00",
            [5] = "\"$\"#,##0_);(\"$\"#,##0)", [6] = "\"$\"#,##0_);[Red](\"$\"#,##0)",
            [7] = "\"$\"#,##0.00_);(\"$\"#,##0.00)",
            [8] = "\"$\"#,##0.00_);[Red](\"$\"#,##0.00)",
            [9] = "0%", [10] = "0.00%", [11] = "0.00E+00", [12] = "# ?/?", [13] = "# ??/??",
            [14] = "mm-dd-yy", [15] = "d-mmm-yy", [16] = "d-mmm", [17] = "mmm-yy",
            [18] = "h:mm AM/PM", [19] = "h:mm:ss AM/PM", [20] = "h:mm", [21] = "h:mm:ss",
            [22] = "m/d/yy h:mm", [37] = "#,##0 ;(#,##0)", [38] = "#,##0 ;[Red](#,##0)",
            [39] = "#,##0.00;(#,##0.00)", [40] = "#,##0.00;[Red](#,##0.00)",
            [41] = "_(* #,##0_);_(* \\(#,##0\\);_(* \"-\"_);_(@_)",
            [42] = "_(\"$\"* #,##0_);_(\"$\"* \\(#,##0\\);_(\"$\"* \"-\"_);_(@_)",
            [43] = "_(* #,##0.00_);_(* \\(#,##0.00\\);_(* \"-\"??_);_(@_)",
            [44] = "_(\"$\"* #,##0.00_);_(\"$\"* \\(#,##0.00\\);_(\"$\"* \"-\"??_);_(@_)",
            [45] = "mm:ss", [46] = "[h]:mm:ss", [47] = "mmss.0", [48] = "##0.0E+0", [49] = "@"
        };

    private static readonly IReadOnlyDictionary<string, string> CanonicalExcelDefaults =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["font.name"] = "Calibri",
            ["font.size"] = "11",
            ["font.bold"] = "false",
            ["font.italic"] = "false",
            ["font.colorArgb"] = "FF000000",
            ["fill.pattern"] = "none",
            ["fill.foregroundArgb"] = "00000000",
            ["border.left.style"] = "none",
            ["border.left.colorArgb"] = "00000000",
            ["border.right.style"] = "none",
            ["border.right.colorArgb"] = "00000000",
            ["border.top.style"] = "none",
            ["border.top.colorArgb"] = "00000000",
            ["border.bottom.style"] = "none",
            ["border.bottom.colorArgb"] = "00000000",
            ["numberFormat.code"] = "General",
            ["alignment.horizontal"] = "general",
            ["alignment.vertical"] = "bottom",
            ["alignment.wrapText"] = "false",
            ["alignment.indent"] = "0",
            ["alignment.shrinkToFit"] = "false",
            ["protection.locked"] = "true"
        };

    private static readonly string[] DefaultIndexedPalette =
    [
        "FF000000", "FFFFFFFF", "FFFF0000", "FF00FF00", "FF0000FF", "FFFFFF00", "FFFF00FF", "FF00FFFF",
        "FF000000", "FFFFFFFF", "FFFF0000", "FF00FF00", "FF0000FF", "FFFFFF00", "FFFF00FF", "FF00FFFF",
        "FF800000", "FF008000", "FF000080", "FF808000", "FF800080", "FF008080", "FFC0C0C0", "FF808080",
        "FF9999FF", "FF993366", "FFFFFFCC", "FFCCFFFF", "FF660066", "FFFF8080", "FF0066CC", "FFCCCCFF",
        "FF000080", "FFFF00FF", "FFFFFF00", "FF00FFFF", "FF800080", "FF800000", "FF008080", "FF0000FF",
        "FF00CCFF", "FFCCFFFF", "FFCCFFCC", "FFFFFF99", "FF99CCFF", "FFFF99CC", "FFCC99FF", "FFFFCC99",
        "FF3366FF", "FF33CCCC", "FF99CC00", "FFFFCC00", "FFFF9900", "FFFF6600", "FF666699", "FF969696",
        "FF003366", "FF339966", "FF003300", "FF333300", "FF993300", "FF993366", "FF333399", "FF333333"
    ];

    private sealed class WorksheetCaptureState
    {
        internal string DefaultRowHeight { get; set; } = "15";

        internal string DefaultColumnWidth { get; set; } = "8.43";

        internal List<ColumnDescriptor> Columns { get; } = [];
    }

    private sealed record CellDescriptor(string Reference, uint? StyleIndex);

    private sealed record ColumnDescriptor(
        uint Min,
        uint Max,
        string? Width,
        bool Hidden,
        uint StyleIndex);

    private sealed record RowDescriptor(uint Index, string? Height, bool Hidden, uint? StyleIndex);

    private sealed record ColumnAppearance(
        string Width,
        bool Hidden,
        uint StyleIndex,
        string StyleSignature)
    {
        internal bool HasSameEffectiveAppearance(ColumnAppearance other) =>
            Width == other.Width &&
            Hidden == other.Hidden &&
            StyleSignature == other.StyleSignature;
    }

    private readonly record struct AppearanceKey(string Location, string Property);

    private readonly record struct SpreadsheetAppearanceDifferenceGroupKey(
        string SheetName,
        string ScopeKind,
        string Property,
        string ExpectedValue,
        string ActualValue);

    private sealed record DifferenceAccumulator(int Count, string FirstLocation);

    private sealed record AppearanceDocument(int SchemaVersion, IReadOnlyList<AppearanceEntry> Entries);
}

internal sealed record AppearanceEntry(string Location, string Property, string Value);

internal sealed record SpreadsheetAppearanceDifference(
    string Location,
    string Property,
    string ExpectedValue,
    string ActualValue);

internal sealed record SpreadsheetAppearanceDifferenceGroup(
    string SheetName,
    string ScopeKind,
    string Property,
    string ExpectedValue,
    string ActualValue,
    int Count,
    string FirstLocation);
