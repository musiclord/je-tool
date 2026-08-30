using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace JET.Tests.Infrastructure;

internal static class SpreadsheetAppearanceSnapshotCompression
{
    internal const int SchemaVersion = 2;
    internal const int MaximumExpandedCellLocations = 1_000_000;
    internal const int MaximumAppearanceEntries = 1_000_000;

    private const uint ExcelMaximumColumn = 16_384;
    private const uint ExcelMaximumRow = 1_048_576;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    internal static byte[] Serialize(IReadOnlyList<AppearanceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var ordered = entries
            .OrderBy(entry => entry.Location, StringComparer.Ordinal)
            .ThenBy(entry => entry.Property, StringComparer.Ordinal)
            .ToArray();
        var structuralEntries = ordered
            .Where(entry => !TrySplitDirectCellLocation(
                entry.Location,
                out _,
                out _))
            .ToArray();
        var cells = ordered
            .Where(entry => TrySplitDirectCellLocation(
                entry.Location,
                out _,
                out _))
            .GroupBy(entry => entry.Location, StringComparer.Ordinal)
            .Select(group =>
            {
                _ = TrySplitDirectCellLocation(
                    group.Key,
                    out var sheetName,
                    out var reference);
                var properties = group
                    .OrderBy(entry => entry.Property, StringComparer.Ordinal)
                    .Select(entry => new CompressedAppearanceProperty(
                        entry.Property,
                        entry.Value))
                    .ToArray();
                return new CellSignatureAssignment(
                    sheetName,
                    reference,
                    SignatureKey(properties),
                    properties);
            })
            .ToArray();

        var signatureMaterials = cells
            .GroupBy(cell => cell.SignatureKey, StringComparer.Ordinal)
            .Select(group => new SignatureMaterial(
                group.Key,
                group.First().Properties,
                group.ToArray()))
            .OrderBy(signature => signature.Key, StringComparer.Ordinal)
            .ToArray();
        var signatureIds = signatureMaterials
            .Select((signature, index) => (signature.Key, Id: index + 1))
            .ToDictionary(item => item.Key, item => item.Id, StringComparer.Ordinal);
        var signatures = signatureMaterials
            .Select((signature, index) => new CompressedAppearanceSignature(
                index + 1,
                signature.Properties))
            .ToArray();
        var bindings = signatureMaterials
            .Select(signature => new CompressedAppearanceBinding(
                signatureIds[signature.Key],
                EncodeRanges(signature.Cells)))
            .ToArray();
        var document = new CompressedAppearanceDocument(
            SchemaVersion,
            signatures,
            bindings,
            structuralEntries);
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, JsonOptions) + "\n");
    }

    internal static bool TrySplitDirectCellLocation(
        string location,
        out string sheetName,
        out string reference)
    {
        sheetName = string.Empty;
        reference = string.Empty;
        var separator = location.IndexOf('!');
        if (separator <= 0
            || separator != location.LastIndexOf('!')
            || separator == location.Length - 1)
        {
            return false;
        }

        var candidate = location[(separator + 1)..];
        if (!TryParseCellReference(candidate, allowAbsolute: true, out _, out _))
        {
            return false;
        }
        sheetName = location[..separator];
        reference = candidate;
        return true;
    }

    internal static bool TryParseRange(
        string value,
        out SpreadsheetAppearanceEncodedRange range)
    {
        range = default;
        var separator = value.IndexOf('!');
        if (separator <= 0
            || separator != value.LastIndexOf('!')
            || separator == value.Length - 1)
        {
            return false;
        }

        var sheetName = value[..separator];
        var referenceRange = value[(separator + 1)..];
        var rangeSeparator = referenceRange.IndexOf(':');
        if (rangeSeparator < 0)
        {
            if (!TryParseCellReference(
                    referenceRange,
                    allowAbsolute: true,
                    out var column,
                    out var row))
            {
                return false;
            }
            range = new SpreadsheetAppearanceEncodedRange(
                sheetName,
                referenceRange,
                referenceRange,
                column,
                row,
                column,
                row,
                true);
            return true;
        }
        if (rangeSeparator == 0
            || rangeSeparator != referenceRange.LastIndexOf(':')
            || rangeSeparator == referenceRange.Length - 1)
        {
            return false;
        }

        var startReference = referenceRange[..rangeSeparator];
        var endReference = referenceRange[(rangeSeparator + 1)..];
        if (!TryParseCellReference(
                startReference,
                allowAbsolute: false,
                out var startColumn,
                out var startRow)
            || !TryParseCellReference(
                endReference,
                allowAbsolute: false,
                out var endColumn,
                out var endRow)
            || endColumn < startColumn
            || endRow < startRow)
        {
            return false;
        }

        range = new SpreadsheetAppearanceEncodedRange(
            sheetName,
            startReference,
            endReference,
            startColumn,
            startRow,
            endColumn,
            endRow,
            false);
        return true;
    }

    internal static IEnumerable<string> ExpandReferences(
        SpreadsheetAppearanceEncodedRange range)
    {
        if (range.PreserveSingletonReference)
        {
            yield return range.StartReference;
            yield break;
        }

        for (var row = range.StartRow; row <= range.EndRow; row++)
        {
            for (var column = range.StartColumn; column <= range.EndColumn; column++)
            {
                yield return ColumnName(column)
                    + row.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    private static string[] EncodeRanges(IReadOnlyList<CellSignatureAssignment> cells)
    {
        var ranges = new List<string>();
        var canonicalCells = new List<CellCoordinate>();
        foreach (var cell in cells)
        {
            if (TryParseCellReference(
                    cell.Reference,
                    allowAbsolute: false,
                    out var column,
                    out var row))
            {
                canonicalCells.Add(new CellCoordinate(cell.SheetName, column, row));
            }
            else
            {
                ranges.Add($"{cell.SheetName}!{cell.Reference}");
            }
        }

        foreach (var sheet in canonicalCells
                     .GroupBy(cell => cell.SheetName, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var horizontalRuns = new List<HorizontalRun>();
            foreach (var row in sheet
                         .GroupBy(cell => cell.Row)
                         .OrderBy(group => group.Key))
            {
                uint? start = null;
                var previous = 0U;
                foreach (var column in row
                             .Select(cell => cell.Column)
                             .Distinct()
                             .Order())
                {
                    if (start is null)
                    {
                        start = column;
                        previous = column;
                        continue;
                    }
                    if (column == previous + 1U)
                    {
                        previous = column;
                        continue;
                    }

                    horizontalRuns.Add(new HorizontalRun(
                        sheet.Key,
                        row.Key,
                        start.Value,
                        previous));
                    start = column;
                    previous = column;
                }
                if (start is not null)
                {
                    horizontalRuns.Add(new HorizontalRun(
                        sheet.Key,
                        row.Key,
                        start.Value,
                        previous));
                }
            }

            foreach (var runFamily in horizontalRuns
                         .GroupBy(run => (run.StartColumn, run.EndColumn))
                         .OrderBy(group => group.Key.StartColumn)
                         .ThenBy(group => group.Key.EndColumn))
            {
                uint? startRow = null;
                var previousRow = 0U;
                foreach (var run in runFamily.OrderBy(run => run.Row))
                {
                    if (startRow is null)
                    {
                        startRow = run.Row;
                        previousRow = run.Row;
                        continue;
                    }
                    if (run.Row == previousRow + 1U)
                    {
                        previousRow = run.Row;
                        continue;
                    }

                    ranges.Add(FormatRange(
                        run.SheetName,
                        run.StartColumn,
                        startRow.Value,
                        run.EndColumn,
                        previousRow));
                    startRow = run.Row;
                    previousRow = run.Row;
                }
                if (startRow is not null)
                {
                    var exemplar = runFamily.First();
                    ranges.Add(FormatRange(
                        exemplar.SheetName,
                        exemplar.StartColumn,
                        startRow.Value,
                        exemplar.EndColumn,
                        previousRow));
                }
            }
        }

        return ranges
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string FormatRange(
        string sheetName,
        uint startColumn,
        uint startRow,
        uint endColumn,
        uint endRow)
    {
        var start = ColumnName(startColumn)
            + startRow.ToString(CultureInfo.InvariantCulture);
        var end = ColumnName(endColumn)
            + endRow.ToString(CultureInfo.InvariantCulture);
        return startColumn == endColumn && startRow == endRow
            ? $"{sheetName}!{start}"
            : $"{sheetName}!{start}:{end}";
    }

    private static string SignatureKey(
        IReadOnlyList<CompressedAppearanceProperty> properties)
    {
        var builder = new StringBuilder();
        foreach (var property in properties)
        {
            builder.Append(property.Property.Length.ToString(CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(property.Property);
            builder.Append(property.Value.Length.ToString(CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(property.Value);
            builder.Append('\n');
        }
        return builder.ToString();
    }

    private static bool TryParseCellReference(
        string reference,
        bool allowAbsolute,
        out uint column,
        out uint row)
    {
        column = 0U;
        row = 0U;
        if (string.IsNullOrEmpty(reference))
        {
            return false;
        }

        var index = 0;
        if (reference[index] == '$')
        {
            if (!allowAbsolute)
            {
                return false;
            }
            index++;
        }
        var letters = 0;
        while (index < reference.Length && reference[index] is >= 'A' and <= 'Z')
        {
            if (letters == 3)
            {
                return false;
            }
            column = checked(column * 26U + (uint)(reference[index] - 'A' + 1));
            index++;
            letters++;
        }
        if (index < reference.Length && reference[index] == '$')
        {
            if (!allowAbsolute)
            {
                return false;
            }
            index++;
        }

        return letters is > 0 and <= 3
            && column is > 0 and <= ExcelMaximumColumn
            && index < reference.Length
            && reference[index] != '0'
            && uint.TryParse(
                reference.AsSpan(index),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out row)
            && row is > 0 and <= ExcelMaximumRow;
    }

    private static string ColumnName(uint column)
    {
        var builder = new StringBuilder(3);
        var current = column;
        do
        {
            current--;
            builder.Insert(0, (char)('A' + current % 26U));
            current /= 26U;
        }
        while (current > 0U);
        return builder.ToString();
    }

    private sealed record CellSignatureAssignment(
        string SheetName,
        string Reference,
        string SignatureKey,
        IReadOnlyList<CompressedAppearanceProperty> Properties);

    private sealed record SignatureMaterial(
        string Key,
        IReadOnlyList<CompressedAppearanceProperty> Properties,
        IReadOnlyList<CellSignatureAssignment> Cells);

    private readonly record struct CellCoordinate(
        string SheetName,
        uint Column,
        uint Row);

    private readonly record struct HorizontalRun(
        string SheetName,
        uint Row,
        uint StartColumn,
        uint EndColumn);
}

internal sealed record CompressedAppearanceDocument(
    int SchemaVersion,
    IReadOnlyList<CompressedAppearanceSignature> Signatures,
    IReadOnlyList<CompressedAppearanceBinding> Bindings,
    IReadOnlyList<AppearanceEntry> Entries);

internal sealed record CompressedAppearanceSignature(
    int Id,
    IReadOnlyList<CompressedAppearanceProperty> Properties);

internal sealed record CompressedAppearanceProperty(
    string Property,
    string Value);

internal sealed record CompressedAppearanceBinding(
    int SignatureId,
    IReadOnlyList<string> Ranges);

internal readonly record struct SpreadsheetAppearanceEncodedRange(
    string SheetName,
    string StartReference,
    string EndReference,
    uint StartColumn,
    uint StartRow,
    uint EndColumn,
    uint EndRow,
    bool PreserveSingletonReference)
{
    internal ulong CellCount => checked(
        (ulong)(EndColumn - StartColumn + 1U)
        * (EndRow - StartRow + 1U));
}
