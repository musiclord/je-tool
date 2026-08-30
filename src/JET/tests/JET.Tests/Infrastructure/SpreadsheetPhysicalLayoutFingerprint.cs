using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Test-only, value-blind fingerprint for physical worksheet layout details that are
/// intentionally normalized away by <see cref="SpreadsheetAppearanceFingerprint"/>.
/// Worksheet XML is streamed and every cell subtree is skipped without inspection.
/// </summary>
internal sealed class SpreadsheetPhysicalLayoutFingerprint
{
    private const string RelationshipNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string StrictRelationshipNamespace =
        "http://purl.oclc.org/ooxml/officeDocument/relationships";
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

    private static readonly IReadOnlySet<string> PageMarginNumericAttributes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "left", "right", "top", "bottom", "header", "footer"
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly IReadOnlyDictionary<PhysicalLayoutKey, string> _properties;

    private SpreadsheetPhysicalLayoutFingerprint(
        IReadOnlyDictionary<PhysicalLayoutKey, string> properties)
    {
        _properties = properties;
        Entries = properties
            .OrderBy(item => item.Key.Location, StringComparer.Ordinal)
            .ThenBy(item => item.Key.Property, StringComparer.Ordinal)
            .Select(item => new PhysicalLayoutEntry(
                item.Key.Location,
                item.Key.Property,
                item.Value))
            .ToArray();
        Json = JsonSerializer.Serialize(
            new PhysicalLayoutDocument(1, Entries),
            JsonOptions) + "\n";
    }

    internal IReadOnlyList<PhysicalLayoutEntry> Entries { get; }

    internal string Json { get; }

    internal byte[] JsonBytes => Encoding.UTF8.GetBytes(Json);

    internal static SpreadsheetPhysicalLayoutFingerprint Capture(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("OpenXML workbook has no workbook part.");
        var sheets = workbookPart.Workbook.Sheets?.Elements<Sheet>().ToArray()
            ?? throw new InvalidDataException("OpenXML workbook has no worksheets.");
        var properties = new Dictionary<PhysicalLayoutKey, string>();

        for (var index = 0; index < sheets.Length; index++)
        {
            var sheet = sheets[index];
            var name = sheet.Name?.Value
                ?? throw new InvalidDataException("OpenXML worksheet has no name.");
            var relationshipId = sheet.Id?.Value
                ?? throw new InvalidDataException(
                    $"OpenXML worksheet '{name}' has no relationship id.");
            if (workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
            {
                throw new InvalidDataException(
                    $"OpenXML worksheet '{name}' has no worksheet part.");
            }

            Add(
                properties,
                $"$workbook!sheet:{(index + 1).ToString("D4", CultureInfo.InvariantCulture)}",
                "name",
                name);
            CaptureWorksheet(worksheetPart, name, properties);
        }

        return new SpreadsheetPhysicalLayoutFingerprint(properties);
    }

    internal string? DescribeFirstDifference(SpreadsheetPhysicalLayoutFingerprint actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        var keys = _properties.Keys
            .Concat(actual._properties.Keys)
            .Distinct()
            .OrderBy(key => key.Location, StringComparer.Ordinal)
            .ThenBy(key => key.Property, StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var expectedValue = _properties.GetValueOrDefault(key, "<missing>");
            var actualValue = actual._properties.GetValueOrDefault(key, "<missing>");
            if (!string.Equals(expectedValue, actualValue, StringComparison.Ordinal))
            {
                return $"{key.Location} {key.Property} " +
                       $"expected={expectedValue} actual={actualValue}";
            }
        }

        return null;
    }

    internal string? Value(string location, string property) =>
        _properties.GetValueOrDefault(new PhysicalLayoutKey(location, property));

    private static void CaptureWorksheet(
        WorksheetPart worksheetPart,
        string sheetName,
        Dictionary<PhysicalLayoutKey, string> properties)
    {
        using var stream = worksheetPart.GetStream(FileMode.Open, FileAccess.Read);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true
        });

        reader.MoveToContent();
        var columnOrdinal = 0;
        var rowOrdinal = 0;
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
                    reader.Skip();
                    continue;
                case "sheetView":
                    viewIndex++;
                    break;
                case "col":
                    columnOrdinal++;
                    CaptureColumn(reader, sheetName, columnOrdinal, properties);
                    break;
                case "row":
                    rowOrdinal++;
                    CaptureRow(reader, sheetName, rowOrdinal, properties);
                    break;
                case "pane":
                    CapturePane(reader, sheetName, Math.Max(viewIndex, 1), properties);
                    break;
                case "pageSetup":
                    CaptureElement(
                        reader,
                        properties,
                        $"{sheetName}!pageSetup",
                        PageSetupNumericAttributes,
                        PageSetupBooleanAttributes,
                        ignoreRelationshipId: true);
                    break;
                case "pageMargins":
                    CaptureElement(
                        reader,
                        properties,
                        $"{sheetName}!pageMargins",
                        PageMarginNumericAttributes,
                        booleanAttributes: null,
                        ignoreRelationshipId: false);
                    break;
            }

            reader.Read();
        }
    }

    private static void CaptureColumn(
        XmlReader reader,
        string sheetName,
        int ordinal,
        Dictionary<PhysicalLayoutKey, string> properties)
    {
        var min = ParseUInt(
            reader.GetAttribute("min")
                ?? throw new InvalidDataException(
                    $"Worksheet '{sheetName}' has a column without min."),
            "column min");
        var max = ParseUInt(
            reader.GetAttribute("max") ?? min.ToString(CultureInfo.InvariantCulture),
            "column max");
        if (min == 0U || max < min || max > 16_384U)
        {
            throw new InvalidDataException(
                $"Worksheet '{sheetName}' has invalid column range {min}-{max}.");
        }

        var location = $"{sheetName}!column:" +
                       $"{ordinal.ToString("D4", CultureInfo.InvariantCulture)}:{min}-{max}";
        Add(properties, location, "bestFit", EffectiveBoolean(reader.GetAttribute("bestFit")));
        Add(properties, location, "customWidth", EffectiveBoolean(reader.GetAttribute("customWidth")));
    }

    private static void CaptureRow(
        XmlReader reader,
        string sheetName,
        int ordinal,
        Dictionary<PhysicalLayoutKey, string> properties)
    {
        var index = ParseUInt(
            reader.GetAttribute("r")
                ?? throw new InvalidDataException(
                    $"Worksheet '{sheetName}' has a row without an index."),
            "row index");
        if (index == 0U || index > 1_048_576U)
        {
            throw new InvalidDataException(
                $"Worksheet '{sheetName}' has invalid row index {index}.");
        }

        var location = $"{sheetName}!row:" +
                       $"{ordinal.ToString("D7", CultureInfo.InvariantCulture)}:{index}";
        var rawHeight = reader.GetAttribute("ht");
        var customHeight = EffectiveBoolean(reader.GetAttribute("customHeight"));
        Add(properties, location, "customHeight", customHeight);
        Add(
            properties,
            location,
            "heightMode",
            rawHeight is null && customHeight == "false" ? "auto" : "explicit");
        if (rawHeight is not null)
        {
            Add(properties, location, "height", CanonicalNumber(rawHeight, "row height"));
        }
    }

    private static void CapturePane(
        XmlReader reader,
        string sheetName,
        int viewIndex,
        Dictionary<PhysicalLayoutKey, string> properties)
    {
        var location = $"{sheetName}!sheetView:{viewIndex}.pane";
        Add(properties, location, "present", "true");
        foreach (var attribute in new[] { "state", "xSplit", "ySplit", "topLeftCell", "activePane" })
        {
            var value = reader.GetAttribute(attribute);
            if (value is null)
            {
                continue;
            }

            if (attribute is "xSplit" or "ySplit")
            {
                value = CanonicalNumber(value, $"pane {attribute}");
            }

            Add(properties, location, attribute, value);
        }
    }

    private static void CaptureElement(
        XmlReader reader,
        Dictionary<PhysicalLayoutKey, string> properties,
        string location,
        IReadOnlySet<string>? numericAttributes,
        IReadOnlySet<string>? booleanAttributes,
        bool ignoreRelationshipId)
    {
        Add(properties, location, "present", "true");
        if (!reader.HasAttributes)
        {
            return;
        }

        while (reader.MoveToNextAttribute())
        {
            if (reader.Prefix == "xmlns" ||
                reader.NamespaceURI is RelationshipNamespace or StrictRelationshipNamespace ||
                (ignoreRelationshipId && reader.LocalName == "id"))
            {
                continue;
            }

            var value = numericAttributes?.Contains(reader.LocalName) == true
                ? CanonicalNumber(reader.Value, $"{location} {reader.LocalName}")
                : booleanAttributes?.Contains(reader.LocalName) == true
                    ? CanonicalBoolean(reader.Value)
                    : reader.Value;
            Add(properties, location, reader.LocalName, value);
        }

        reader.MoveToElement();
    }

    private static void Add(
        Dictionary<PhysicalLayoutKey, string> properties,
        string location,
        string property,
        string value)
    {
        if (!properties.TryAdd(new PhysicalLayoutKey(location, property), value))
        {
            throw new InvalidDataException(
                $"Duplicate physical-layout property '{location} {property}'.");
        }
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

    private static string EffectiveBoolean(string? value) =>
        value is null ? "false" : CanonicalBoolean(value);

    private static string CanonicalBoolean(string value) => value switch
    {
        "1" => "true",
        "0" => "false",
        _ when value.Equals("true", StringComparison.OrdinalIgnoreCase) => "true",
        _ when value.Equals("false", StringComparison.OrdinalIgnoreCase) => "false",
        _ => throw new InvalidDataException($"Invalid OpenXML boolean '{value}'.")
    };

    private readonly record struct PhysicalLayoutKey(string Location, string Property);

    private sealed record PhysicalLayoutDocument(
        int SchemaVersion,
        IReadOnlyList<PhysicalLayoutEntry> Entries);
}

internal sealed record PhysicalLayoutEntry(string Location, string Property, string Value);
