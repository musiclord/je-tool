using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Test-only OpenXML content oracle. It retains one logical worksheet row at a time,
/// emits hashes rather than cell values, and deliberately ignores all appearance data.
/// </summary>
internal sealed class SpreadsheetContentFingerprint
{
    internal const int SchemaVersion = 1;

    private const int DigestLength = 32;
    private const uint ExcelMaxRow = 1_048_576;
    private const uint ExcelMaxColumn = 16_384;

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    private SpreadsheetContentFingerprint()
    {
    }

    internal static SpreadsheetContentFingerprintCapture Capture(
        string workbookPath,
        string destinationPath,
        string reportId,
        Func<string, SpreadsheetContentHeaderRule> headerRule)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);
        ArgumentNullException.ThrowIfNull(headerRule);
        if (reportId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-'))
        {
            throw new ArgumentException(
                "Content fingerprint report id must be a fixed safe identifier.",
                nameof(reportId));
        }

        var source = Path.GetFullPath(workbookPath);
        var target = Path.GetFullPath(destinationPath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Content fingerprint source workbook is unavailable.", source);
        }
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new IOException("Content fingerprint target already exists; overwrite was refused.");
        }

        var directory = Path.GetDirectoryName(target)
            ?? throw new ArgumentException(
                "Content fingerprint target must have a parent directory.",
                nameof(destinationPath));
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");

        try
        {
            SpreadsheetContentFingerprintCapture capture;
            using (var output = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 128 * 1024,
                       FileOptions.SequentialScan))
            using (var writer = new StreamWriter(
                       output,
                       Utf8WithoutBom,
                       bufferSize: 128 * 1024,
                       leaveOpen: true)
                   {
                       NewLine = "\n",
                   })
            using (var document = SpreadsheetDocument.Open(source, false))
            {
                var workbookPart = document.WorkbookPart
                    ?? throw new InvalidDataException(
                        "Content fingerprint workbook has no workbook part.");
                using var sharedStrings = SharedStringDigestStore.Create(workbookPart, directory);
                capture = CaptureWorkbook(
                    workbookPart,
                    target,
                    reportId,
                    headerRule,
                    sharedStrings,
                    writer);
                writer.Flush();
                output.Flush(flushToDisk: true);
            }

            File.Move(temporary, target, overwrite: false);
            return capture;
        }
        catch
        {
            TryDeleteOwnedTemporary(temporary);
            throw;
        }
    }

    internal static SpreadsheetContentFingerprintDifference? DescribeFirstDifference(
        string expectedPath,
        string actualPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(actualPath);
        var expected = Path.GetFullPath(expectedPath);
        var actual = Path.GetFullPath(actualPath);
        if (FilesEqual(expected, actual))
        {
            return null;
        }

        using var expectedReader = OpenFingerprintReader(expected);
        using var actualReader = OpenFingerprintReader(actual);
        while (true)
        {
            var expectedLine = expectedReader.ReadLine();
            var actualLine = actualReader.ReadLine();
            if (string.Equals(expectedLine, actualLine, StringComparison.Ordinal))
            {
                if (expectedLine is null)
                {
                    break;
                }
                continue;
            }

            return DifferencePosition(expectedLine)
                ?? DifferencePosition(actualLine)
                ?? new SpreadsheetContentFingerprintDifference("$workbook", null);
        }

        return new SpreadsheetContentFingerprintDifference("$workbook", null);
    }

    internal static bool FilesEqual(string expectedPath, string actualPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(actualPath);
        using var expected = new FileStream(
            Path.GetFullPath(expectedPath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        using var actual = new FileStream(
            Path.GetFullPath(actualPath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        if (expected.Length != actual.Length)
        {
            return false;
        }

        var expectedBuffer = new byte[128 * 1024];
        var actualBuffer = new byte[128 * 1024];
        while (true)
        {
            var expectedRead = expected.Read(expectedBuffer);
            var actualRead = actual.Read(actualBuffer);
            if (expectedRead != actualRead)
            {
                return false;
            }
            if (expectedRead == 0)
            {
                return true;
            }
            if (!expectedBuffer.AsSpan(0, expectedRead)
                    .SequenceEqual(actualBuffer.AsSpan(0, actualRead)))
            {
                return false;
            }
        }
    }

    private static SpreadsheetContentFingerprintCapture CaptureWorkbook(
        WorkbookPart workbookPart,
        string targetPath,
        string reportId,
        Func<string, SpreadsheetContentHeaderRule> headerRule,
        SharedStringDigestStore sharedStrings,
        StreamWriter writer)
    {
        var sheets = workbookPart.Workbook.Sheets?.Elements<Sheet>().ToArray()
            ?? throw new InvalidDataException(
                "Content fingerprint workbook has no worksheet collection.");
        WriteDocument(writer, reportId);

        long contentRows = 0;
        long dataRows = 0;
        for (var index = 0; index < sheets.Length; index++)
        {
            var sheet = sheets[index];
            var name = sheet.Name?.Value
                ?? throw new InvalidDataException(
                    "Content fingerprint worksheet has no name.");
            var relationshipId = sheet.Id?.Value
                ?? throw new InvalidDataException(
                    $"Content fingerprint worksheet '{name}' has no relationship id.");
            if (workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
            {
                throw new InvalidDataException(
                    $"Content fingerprint worksheet '{name}' has no worksheet part.");
            }

            var rule = headerRule(name)
                ?? throw new InvalidDataException(
                    $"Content fingerprint worksheet '{name}' has no header rule.");
            WriteSheet(writer, checked(index + 1), name);
            var summary = CaptureWorksheet(
                worksheetPart,
                name,
                rule,
                sharedStrings,
                writer);
            contentRows = checked(contentRows + summary.ContentRowCount);
            dataRows = checked(dataRows + summary.DataRowCount);
        }

        WriteDocumentSummary(writer, sheets.Length, contentRows, dataRows);
        return new SpreadsheetContentFingerprintCapture(
            targetPath,
            sheets.Length,
            contentRows,
            dataRows);
    }

    private static SpreadsheetContentSheetSummary CaptureWorksheet(
        WorksheetPart worksheetPart,
        string sheetName,
        SpreadsheetContentHeaderRule headerRule,
        SharedStringDigestStore sharedStrings,
        StreamWriter writer)
    {
        using var reader = OpenXmlReader.Create(worksheetPart);
        var sawSheetData = false;
        RowFingerprint? pending = null;
        RowFingerprint? repeatPrototype = null;
        long contentRows = 0;
        long dataRows = 0;
        var headerRows = 0;
        uint maximumColumn = 0;

        void Emit(RowFingerprint row)
        {
            contentRows = checked(contentRows + 1);
            maximumColumn = Math.Max(maximumColumn, row.MaximumColumn);
            var isHeader = headerRule.FixedRows.Contains(row.RowIndex);
            if (headerRule.RepeatedHeaderPrototypeRow == row.RowIndex)
            {
                repeatPrototype = row;
                isHeader = true;
            }
            else if (repeatPrototype is not null
                     && row.RowIndex > repeatPrototype.RowIndex
                     && row.HasSameCellSequence(repeatPrototype))
            {
                isHeader = true;
            }
            else if (headerRule.TextSignatures.Any(signature =>
                         HasSameTextCellSequence(row, signature)))
            {
                isHeader = true;
            }

            WriteRow(writer, sheetName, row);
            if (isHeader)
            {
                headerRows++;
                WriteHeader(writer, sheetName, row);
            }
            else
            {
                dataRows = checked(dataRows + 1);
            }
        }

        while (reader.Read())
        {
            if (reader.IsStartElement && reader.ElementType == typeof(SheetData))
            {
                if (sawSheetData)
                {
                    throw WorksheetError(sheetName, null, "sheet-data-duplicate");
                }
                sawSheetData = true;
                continue;
            }
            if (!reader.IsStartElement || reader.ElementType != typeof(Row))
            {
                continue;
            }
            if (!sawSheetData)
            {
                throw WorksheetError(sheetName, null, "row-before-sheet-data");
            }

            var row = reader.LoadCurrentElement() as Row
                ?? throw WorksheetError(sheetName, null, "row-load");
            var captured = CaptureRow(row, sheetName, sharedStrings);
            if (captured is null)
            {
                continue;
            }
            if (pending is null)
            {
                pending = captured;
                continue;
            }
            if (captured.RowIndex < pending.RowIndex)
            {
                throw WorksheetError(sheetName, captured.RowIndex, "row-order");
            }
            if (captured.RowIndex == pending.RowIndex)
            {
                pending = pending.Merge(captured, sheetName);
                continue;
            }

            Emit(pending);
            pending = captured;
        }

        if (!sawSheetData)
        {
            throw WorksheetError(sheetName, null, "sheet-data-missing");
        }
        if (pending is not null)
        {
            Emit(pending);
        }

        WriteSheetSummary(
            writer,
            sheetName,
            contentRows,
            dataRows,
            maximumColumn,
            headerRows);
        return new SpreadsheetContentSheetSummary(
            contentRows,
            dataRows,
            maximumColumn,
            headerRows);
    }

    private static RowFingerprint? CaptureRow(
        Row row,
        string sheetName,
        SharedStringDigestStore sharedStrings)
    {
        uint? rowIndex = row.RowIndex?.Value;
        if (rowIndex is 0 or > ExcelMaxRow)
        {
            throw WorksheetError(sheetName, rowIndex, "row-index");
        }

        var cells = new SortedDictionary<uint, CellFingerprint>();
        foreach (var cell in row.Elements<Cell>())
        {
            if (!HasContent(cell))
            {
                continue;
            }

            var reference = cell.CellReference?.Value
                ?? throw WorksheetError(sheetName, rowIndex, "cell-reference");
            var (column, referencedRow) = ParseReference(reference, sheetName);
            if (rowIndex.HasValue && rowIndex.Value != referencedRow)
            {
                throw WorksheetError(sheetName, referencedRow, "cell-row-mismatch");
            }
            rowIndex ??= referencedRow;
            if (!cells.TryAdd(column, CaptureCell(cell, sharedStrings, sheetName, referencedRow)))
            {
                throw WorksheetError(sheetName, referencedRow, "cell-duplicate");
            }
        }

        if (cells.Count == 0)
        {
            return null;
        }
        if (!rowIndex.HasValue)
        {
            throw WorksheetError(sheetName, null, "row-index-missing");
        }
        return new RowFingerprint(rowIndex.Value, cells);
    }

    private static CellFingerprint CaptureCell(
        Cell cell,
        SharedStringDigestStore sharedStrings,
        string sheetName,
        uint rowIndex)
    {
        var value = CaptureValue(cell, sharedStrings, sheetName, rowIndex);
        if (cell.CellFormula is null)
        {
            return value;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "formula/v1");
        Append(hash, CanonicalFormula(cell.CellFormula));
        Span<byte> valueKind = stackalloc byte[1] { value.Kind };
        hash.AppendData(valueKind);
        hash.AppendData(value.Digest);
        return new CellFingerprint(CellKinds.Formula, hash.GetHashAndReset());
    }

    private static CellFingerprint CaptureValue(
        Cell cell,
        SharedStringDigestStore sharedStrings,
        string sheetName,
        uint rowIndex)
    {
        var dataType = cell.DataType?.Value;
        if (dataType == CellValues.SharedString)
        {
            if (!int.TryParse(
                    cell.CellValue?.InnerText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var index)
                || index < 0)
            {
                throw WorksheetError(sheetName, rowIndex, "shared-string-index");
            }
            return new CellFingerprint(CellKinds.Text, sharedStrings.Resolve(index));
        }
        if (dataType == CellValues.InlineString || cell.InlineString is not null)
        {
            return Digest(CellKinds.Text, cell.InlineString?.InnerText ?? string.Empty);
        }

        var raw = cell.CellValue?.InnerText ?? string.Empty;
        if (dataType == CellValues.String)
        {
            return Digest(CellKinds.Text, raw);
        }
        if (dataType == CellValues.Boolean)
        {
            var canonical = raw switch
            {
                "1" or "true" or "TRUE" => "true",
                "0" or "false" or "FALSE" => "false",
                _ => throw WorksheetError(sheetName, rowIndex, "boolean-value"),
            };
            return Digest(CellKinds.Boolean, canonical);
        }
        if (dataType == CellValues.Error)
        {
            return Digest(CellKinds.Error, raw);
        }
        if (dataType == CellValues.Date)
        {
            var canonical = DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed.ToString("O", CultureInfo.InvariantCulture)
                : raw;
            return Digest(CellKinds.Date, canonical);
        }

        return Digest(CellKinds.Number, CanonicalNumber(raw));
    }

    private static CellFingerprint Digest(byte kind, string value)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> kindBytes = stackalloc byte[1] { kind };
        hash.AppendData(kindBytes);
        Append(hash, value);
        return new CellFingerprint(kind, hash.GetHashAndReset());
    }

    private static bool HasSameTextCellSequence(
        RowFingerprint row,
        SpreadsheetContentTextHeaderSignature signature)
    {
        if (row.Cells.Count != signature.Cells.Count)
        {
            return false;
        }

        foreach (var (column, expectedValue) in signature.Cells)
        {
            if (!row.Cells.TryGetValue(column, out var actual)
                || actual.Kind != CellKinds.Text
                || !actual.Digest.AsSpan().SequenceEqual(
                    Digest(CellKinds.Text, expectedValue).Digest))
            {
                return false;
            }
        }
        return true;
    }

    private static string CanonicalNumber(string raw)
    {
        if (decimal.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var decimalValue))
        {
            return decimalValue.ToString("G29", CultureInfo.InvariantCulture);
        }
        if (double.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var doubleValue)
            && double.IsFinite(doubleValue))
        {
            return doubleValue.ToString("R", CultureInfo.InvariantCulture);
        }
        return raw;
    }

    private static string CanonicalFormula(CellFormula formula)
    {
        var attributes = formula.GetAttributes()
            .OrderBy(attribute => attribute.NamespaceUri, StringComparer.Ordinal)
            .ThenBy(attribute => attribute.LocalName, StringComparer.Ordinal)
            .Select(attribute =>
            {
                var namespaceUri = attribute.NamespaceUri ?? string.Empty;
                var localName = attribute.LocalName ?? string.Empty;
                var value = attribute.Value ?? string.Empty;
                return $"{namespaceUri.Length}:{namespaceUri}"
                    + $"{localName.Length}:{localName}"
                    + $"{value.Length}:{value}";
            });
        return string.Join("", attributes)
            + $"|{formula.InnerText.Length}:{formula.InnerText}";
    }

    private static bool HasContent(Cell cell) =>
        cell.CellFormula is not null
        || cell.CellValue is not null
        || cell.InlineString is not null;

    private static (uint Column, uint Row) ParseReference(
        string reference,
        string sheetName)
    {
        var split = 0;
        while (split < reference.Length && char.IsAsciiLetter(reference[split]))
        {
            split++;
        }
        if (split == 0 || split == reference.Length)
        {
            throw WorksheetError(sheetName, null, "cell-reference");
        }

        uint column = 0;
        for (var index = 0; index < split; index++)
        {
            var letter = char.ToUpperInvariant(reference[index]);
            if (letter is < 'A' or > 'Z')
            {
                throw WorksheetError(sheetName, null, "cell-reference");
            }
            column = checked(column * 26 + (uint)(letter - 'A' + 1));
        }
        if (column is 0 or > ExcelMaxColumn
            || !uint.TryParse(
                reference.AsSpan(split),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var row)
            || row is 0 or > ExcelMaxRow)
        {
            throw WorksheetError(sheetName, null, "cell-reference");
        }
        return (column, row);
    }

    private static void WriteDocument(StreamWriter writer, string reportId)
    {
        writer.Write("{\"kind\":\"document\",\"schema\":");
        writer.Write(SchemaVersion.ToString(CultureInfo.InvariantCulture));
        writer.Write(",\"report\":");
        WriteJsonString(writer, reportId);
        writer.WriteLine('}');
    }

    private static void WriteSheet(StreamWriter writer, int ordinal, string sheetName)
    {
        writer.Write("{\"kind\":\"sheet\",\"ordinal\":");
        writer.Write(ordinal.ToString(CultureInfo.InvariantCulture));
        writer.Write(",\"sheet\":");
        WriteJsonString(writer, sheetName);
        writer.WriteLine('}');
    }

    private static void WriteRow(
        StreamWriter writer,
        string sheetName,
        RowFingerprint row)
    {
        writer.Write("{\"kind\":\"row\",\"sheet\":");
        WriteJsonString(writer, sheetName);
        writer.Write(",\"row\":");
        writer.Write(row.RowIndex.ToString(CultureInfo.InvariantCulture));
        writer.Write(",\"hash\":");
        WriteJsonString(writer, Convert.ToBase64String(row.Digest()));
        writer.WriteLine('}');
    }

    private static void WriteHeader(
        StreamWriter writer,
        string sheetName,
        RowFingerprint row)
    {
        writer.Write("{\"kind\":\"header\",\"sheet\":");
        WriteJsonString(writer, sheetName);
        writer.Write(",\"row\":");
        writer.Write(row.RowIndex.ToString(CultureInfo.InvariantCulture));
        writer.Write(",\"cells\":[");
        var first = true;
        foreach (var (column, cell) in row.Cells)
        {
            if (!first)
            {
                writer.Write(',');
            }
            first = false;
            writer.Write("{\"column\":");
            writer.Write(column.ToString(CultureInfo.InvariantCulture));
            writer.Write(",\"hash\":");
            WriteJsonString(writer, Convert.ToBase64String(cell.Digest));
            writer.Write('}');
        }
        writer.WriteLine("]}");
    }

    private static void WriteSheetSummary(
        StreamWriter writer,
        string sheetName,
        long contentRows,
        long dataRows,
        uint columns,
        int headerRows)
    {
        writer.Write("{\"kind\":\"sheetSummary\",\"sheet\":");
        WriteJsonString(writer, sheetName);
        writer.Write(",\"contentRows\":");
        writer.Write(contentRows.ToString(CultureInfo.InvariantCulture));
        writer.Write(",\"dataRows\":");
        writer.Write(dataRows.ToString(CultureInfo.InvariantCulture));
        writer.Write(",\"columns\":");
        writer.Write(columns.ToString(CultureInfo.InvariantCulture));
        writer.Write(",\"headerRows\":");
        writer.Write(headerRows.ToString(CultureInfo.InvariantCulture));
        writer.WriteLine('}');
    }

    private static void WriteDocumentSummary(
        StreamWriter writer,
        int sheets,
        long contentRows,
        long dataRows)
    {
        writer.Write("{\"kind\":\"documentSummary\",\"sheets\":");
        writer.Write(sheets.ToString(CultureInfo.InvariantCulture));
        writer.Write(",\"contentRows\":");
        writer.Write(contentRows.ToString(CultureInfo.InvariantCulture));
        writer.Write(",\"dataRows\":");
        writer.Write(dataRows.ToString(CultureInfo.InvariantCulture));
        writer.WriteLine('}');
    }

    private static void WriteJsonString(StreamWriter writer, string value) =>
        writer.Write(JsonSerializer.Serialize(value));

    private static StreamReader OpenFingerprintReader(string path) => new(
        new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan),
        Utf8WithoutBom,
        detectEncodingFromByteOrderMarks: false,
        bufferSize: 128 * 1024,
        leaveOpen: false);

    private static SpreadsheetContentFingerprintDifference? DifferencePosition(string? line)
    {
        if (line is null)
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var sheet = root.TryGetProperty("sheet", out var sheetElement)
                && sheetElement.ValueKind == JsonValueKind.String
                    ? sheetElement.GetString() ?? "$workbook"
                    : "$workbook";
            uint? row = root.TryGetProperty("row", out var rowElement)
                && rowElement.TryGetUInt32(out var parsedRow)
                    ? parsedRow
                    : null;
            return new SpreadsheetContentFingerprintDifference(sheet, row);
        }
        catch (JsonException)
        {
            return new SpreadsheetContentFingerprintDifference("$workbook", null);
        }
    }

    private static InvalidDataException WorksheetError(
        string sheetName,
        uint? rowIndex,
        string reason) =>
        new(
            $"Content fingerprint failed at worksheet '{sheetName}'"
            + (rowIndex.HasValue ? $" row {rowIndex.Value}" : string.Empty)
            + $" ({reason}); cell values were suppressed.");

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void TryDeleteOwnedTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            // Preserve the primary capture failure. The GUID-owned file remains under
            // the caller-selected fingerprint directory and is never treated as evidence.
        }
    }

    private static class CellKinds
    {
        internal const byte Text = 1;
        internal const byte Number = 2;
        internal const byte Boolean = 3;
        internal const byte Error = 4;
        internal const byte Date = 5;
        internal const byte Formula = 6;
    }

    private sealed record CellFingerprint(byte Kind, byte[] Digest);

    private sealed class RowFingerprint
    {
        internal RowFingerprint(
            uint rowIndex,
            SortedDictionary<uint, CellFingerprint> cells)
        {
            RowIndex = rowIndex;
            Cells = cells;
        }

        internal uint RowIndex { get; }

        internal SortedDictionary<uint, CellFingerprint> Cells { get; }

        internal uint MaximumColumn => Cells.Keys.Last();

        internal RowFingerprint Merge(RowFingerprint other, string sheetName)
        {
            var merged = new SortedDictionary<uint, CellFingerprint>(Cells);
            foreach (var (column, cell) in other.Cells)
            {
                if (!merged.TryAdd(column, cell))
                {
                    throw WorksheetError(sheetName, RowIndex, "row-fragment-overlap");
                }
            }
            return new RowFingerprint(RowIndex, merged);
        }

        internal bool HasSameCellSequence(RowFingerprint other)
        {
            if (Cells.Count != other.Cells.Count)
            {
                return false;
            }
            using var left = Cells.GetEnumerator();
            using var right = other.Cells.GetEnumerator();
            while (left.MoveNext() && right.MoveNext())
            {
                if (left.Current.Key != right.Current.Key
                    || left.Current.Value.Kind != right.Current.Value.Kind
                    || !left.Current.Value.Digest.AsSpan()
                        .SequenceEqual(right.Current.Value.Digest))
                {
                    return false;
                }
            }
            return true;
        }

        internal byte[] Digest()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendUInt32(hash, RowIndex);
            Span<byte> kind = stackalloc byte[1];
            foreach (var (column, cell) in Cells)
            {
                AppendUInt32(hash, column);
                kind[0] = cell.Kind;
                hash.AppendData(kind);
                hash.AppendData(cell.Digest);
            }
            return hash.GetHashAndReset();
        }

        private static void AppendUInt32(IncrementalHash hash, uint value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            hash.AppendData(bytes);
        }
    }

    private sealed class SharedStringDigestStore : IDisposable
    {
        private readonly string? _path;
        private readonly FileStream? _stream;
        private bool _disposed;

        private SharedStringDigestStore(string? path, FileStream? stream, int count)
        {
            _path = path;
            _stream = stream;
            Count = count;
        }

        internal int Count { get; }

        internal static SharedStringDigestStore Create(
            WorkbookPart workbookPart,
            string scratchDirectory)
        {
            if (workbookPart.SharedStringTablePart is not { } part)
            {
                return new SharedStringDigestStore(null, null, 0);
            }

            var path = Path.Combine(
                scratchDirectory,
                $".content-fingerprint-shared-{Guid.NewGuid():N}.tmp");
            FileStream? stream = null;
            try
            {
                stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    FileOptions.RandomAccess | FileOptions.DeleteOnClose);
                var count = 0;
                using var reader = OpenXmlReader.Create(part);
                while (reader.Read())
                {
                    if (!reader.IsStartElement
                        || reader.ElementType != typeof(SharedStringItem))
                    {
                        continue;
                    }
                    var item = reader.LoadCurrentElement() as SharedStringItem
                        ?? throw new InvalidDataException(
                            "Content fingerprint could not load a shared-string item.");
                    var digest = Digest(CellKinds.Text, item.InnerText).Digest;
                    stream.Write(digest);
                    count = checked(count + 1);
                }
                stream.Flush();
                return new SharedStringDigestStore(path, stream, count);
            }
            catch
            {
                stream?.Dispose();
                TryDeleteOwnedTemporary(path);
                throw;
            }
        }

        internal byte[] Resolve(int index)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stream is null || index < 0 || index >= Count)
            {
                throw new InvalidDataException(
                    "Content fingerprint shared-string index is outside the table; value was suppressed.");
            }

            var digest = new byte[DigestLength];
            _stream.Position = checked((long)index * DigestLength);
            _stream.ReadExactly(digest);
            return digest;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _stream?.Dispose();
            if (_path is not null)
            {
                TryDeleteOwnedTemporary(_path);
            }
            _disposed = true;
        }
    }
}

internal sealed record SpreadsheetContentHeaderRule(
    IReadOnlySet<uint> FixedRows,
    uint? RepeatedHeaderPrototypeRow = null,
    IReadOnlyList<SpreadsheetContentTextHeaderSignature>? DynamicTextSignatures = null)
{
    internal IReadOnlyList<SpreadsheetContentTextHeaderSignature> TextSignatures =>
        DynamicTextSignatures ?? Array.Empty<SpreadsheetContentTextHeaderSignature>();

    internal static SpreadsheetContentHeaderRule None { get; } = new(
        new HashSet<uint>());

    internal static SpreadsheetContentHeaderRule Fixed(
        params uint[] rows) => new(new HashSet<uint>(rows));

    internal static SpreadsheetContentHeaderRule FixedWithRepeats(
        uint prototypeRow) => new(
            new HashSet<uint> { prototypeRow },
            prototypeRow);

    internal SpreadsheetContentHeaderRule WithExactTextSignature(
        params (uint Column, string Value)[] cells)
    {
        if (cells.Length == 0
            || cells.Any(cell => cell.Column == 0 || cell.Value is null)
            || cells.Select(cell => cell.Column).Distinct().Count() != cells.Length)
        {
            throw new ArgumentException(
                "Content header text signature must contain unique positive columns.",
                nameof(cells));
        }

        var signature = new SpreadsheetContentTextHeaderSignature(
            cells.ToDictionary(cell => cell.Column, cell => cell.Value));
        return this with
        {
            DynamicTextSignatures = [.. TextSignatures, signature]
        };
    }
}

internal sealed record SpreadsheetContentTextHeaderSignature(
    IReadOnlyDictionary<uint, string> Cells);

internal sealed record SpreadsheetContentFingerprintCapture(
    string Path,
    int SheetCount,
    long ContentRowCount,
    long DataRowCount);

internal sealed record SpreadsheetContentSheetSummary(
    long ContentRowCount,
    long DataRowCount,
    uint ColumnCount,
    int HeaderRowCount);

internal sealed record SpreadsheetContentFingerprintDifference(
    string SheetName,
    uint? RowIndex)
{
    public override string ToString() =>
        RowIndex.HasValue ? $"{SheetName}/{RowIndex.Value}" : $"{SheetName}/workbook";
}
