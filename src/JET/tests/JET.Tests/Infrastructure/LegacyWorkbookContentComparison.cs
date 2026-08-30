using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Legacy 工作簿比對使用的 normalized content 模型。物理 sheet 只保存名稱、列數、
/// 逐列 canonical hash 與 aggregate cell-kind counts；direct-physical 家族另建
/// 「欄名對齊」的邏輯投影：標題列欄名 hash 序列＋以欄名為 key、跨續頁合併的
/// 資料列 value hash。不保存任何 cell 原文，capture 檔案本身即為去識別化 evidence。
/// </summary>
internal sealed record LegacyAuditParityNormalizedSheet(
    string SheetName,
    int WrittenRowCount,
    long CellCount,
    long FormulaCellCount,
    IReadOnlyDictionary<string, long> CellKindCounts,
    IReadOnlyList<string> RowHashes)
{
    public override string ToString() => "[redacted-normalized-sheet]";
}

/// <summary>
/// Direct-physical 家族的欄名對齊投影：`ColumnNameHashes` 是標題列（依 §7.2
/// 的固定標題列位置）逐 ordinal 的欄名 hash；
/// `RowValueHashes` 是跨續頁合併、排除標題列、以欄名 key 排序後的資料列
/// semantic value hash（含 §3.1.2／§3.1.3 正規化與零值摒除）。
/// </summary>
internal sealed record LegacyAuditParityNormalizedFamilyColumn(
    string Key,
    string MultisetDigest,
    string SequenceDigest,
    int ValueCount,
    IReadOnlyList<int> BucketCounts);

internal sealed record LegacyAuditParityNormalizedDirectFamily(
    string BaseSheetName,
    IReadOnlyList<string> ColumnNameHashes,
    int DataRowCount,
    IReadOnlyList<LegacyAuditParityNormalizedFamilyColumn> Columns)
{
    public override string ToString() => "[redacted-normalized-direct-family]";
}

internal sealed record LegacyAuditParityNormalizedWorkbook(
    string ReportSlug,
    IReadOnlyList<LegacyAuditParityNormalizedSheet> Sheets,
    IReadOnlyList<LegacyAuditParityNormalizedDirectFamily> DirectFamilies)
{
    public override string ToString() => $"normalized workbook ({ReportSlug})";

    internal string ContentDigest()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, "legacy-audit-parity-normalized-workbook/v6");
        AppendString(hash, ReportSlug);
        AppendInt(hash, Sheets.Count);
        foreach (var sheet in Sheets)
        {
            AppendString(hash, sheet.SheetName);
            AppendInt(hash, sheet.WrittenRowCount);
            AppendLong(hash, sheet.CellCount);
            AppendLong(hash, sheet.FormulaCellCount);
            AppendInt(hash, sheet.CellKindCounts.Count);
            foreach (var pair in sheet.CellKindCounts.OrderBy(
                         static item => item.Key, StringComparer.Ordinal))
            {
                AppendString(hash, pair.Key);
                AppendLong(hash, pair.Value);
            }
            AppendInt(hash, sheet.RowHashes.Count);
            foreach (var row in sheet.RowHashes)
            {
                AppendString(hash, row);
            }
        }
        AppendInt(hash, DirectFamilies.Count);
        foreach (var family in DirectFamilies)
        {
            AppendString(hash, family.BaseSheetName);
            AppendInt(hash, family.ColumnNameHashes.Count);
            foreach (var column in family.ColumnNameHashes)
            {
                AppendString(hash, column);
            }
            AppendInt(hash, family.DataRowCount);
            AppendInt(hash, family.Columns.Count);
            foreach (var column in family.Columns)
            {
                AppendString(hash, column.Key);
                AppendString(hash, column.MultisetDigest);
                AppendString(hash, column.SequenceDigest);
                AppendInt(hash, column.ValueCount);
                foreach (var bucket in column.BucketCounts)
                {
                    AppendInt(hash, bucket);
                }
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendInt(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt(IncrementalHash hash, int value) =>
        hash.AppendData(BitConverter.GetBytes(value));

    private static void AppendLong(IncrementalHash hash, long value) =>
        hash.AppendData(BitConverter.GetBytes(value));
}

/// <summary>
/// 讀取一份報表工作簿並產生 normalized content。storage 層：shared string 解出、
/// 數字 lexeme 收斂為 invariant G29、boolean 收斂為 TRUE/FALSE、formula 文字保留；
/// 空字串 cell 與純樣式 cell 不是 content。direct 家族的 value 層依 jet-guide
/// §3.1.2／§3.1.3 匯入可接受格式正規化金額與日期文字、摒除正規化後為 0 的
/// 無 formula cell（IDEA 匯出以 0 表來源空白，JET raw 回放忠實省略），並以
/// 欄名對齊取代 ordinal 對齊，讓欄位集合／順序差異與值差異分維量測。
/// </summary>
internal static class LegacyAuditParityNormalizedWorkbookReader
{
    /// <summary>18 張 direct-physical 基底頁的固定標題列位置。</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>
        DirectFamilyHeaderRows = new Dictionary<string, IReadOnlyDictionary<string, int>>(
            StringComparer.Ordinal)
        {
            ["validation-report"] = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["V_Report 3"] = 1,
                ["V_Report 4"] = 1,
                ["V_Report 5"] = 1,
            },
            ["inf-report"] = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["可靠性樣本_所有欄位"] = 1,
            },
            ["prescreen-report"] = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["R1"] = 1,
                ["R2"] = 1,
                ["R3"] = 1,
                ["R4"] = 1,
                ["R5"] = 1,
                ["R6"] = 1,
                ["R7"] = 1,
            },
            ["criteria-selection-report"] = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["#Criteria Select 1"] = 1,
                ["#Criteria Select 2"] = 1,
                ["#Criteria Select 3"] = 1,
                ["#Criteria Select 4"] = 1,
                ["#Criteria Select 5"] = 1,
                ["#Criteria Select 6"] = 1,
            },
            ["working-paper"] = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["step4-1 符合高風險條件傳票明細"] = 5,
            },
        };

    internal static LegacyAuditParityNormalizedWorkbook Read(
        string reportSlug,
        string workbookPath,
        bool excludeJetMetadataSheet) => Read(
            reportSlug,
            workbookPath,
            excludeJetMetadataSheet,
            contentMask: null);

    internal static LegacyAuditParityNormalizedWorkbook Read(
        string reportSlug,
        string workbookPath,
        bool excludeJetMetadataSheet,
        Func<string, int, int, bool>? contentMask)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        using var stream = new FileStream(
            workbookPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var document = SpreadsheetDocument.Open(stream, isEditable: false);
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("Normalized capture workbook has no workbook part.");
        var shared = workbookPart.SharedStringTablePart?.SharedStringTable
            .Elements<SharedStringItem>()
            .Select(item => string.Concat(item.Descendants<Text>().Select(static text => text.Text)))
            .ToArray() ?? [];
        var familyHeaderRows = DirectFamilyHeaderRows.GetValueOrDefault(reportSlug)
            ?? new Dictionary<string, int>(StringComparer.Ordinal);

        var sheets = new List<LegacyAuditParityNormalizedSheet>();
        var familyBuilders = new Dictionary<string, DirectFamilyBuilder>(StringComparer.Ordinal);
        foreach (var sheet in workbookPart.Workbook.Sheets?.Elements<Sheet>() ?? [])
        {
            if (sheet.Id?.Value is not { } relationshipId
                || !workbookPart.TryGetPartById(relationshipId, out var part)
                || part is not WorksheetPart worksheetPart
                || string.IsNullOrWhiteSpace(sheet.Name?.Value))
            {
                throw new InvalidDataException("Normalized capture worksheet is unresolvable.");
            }

            var sheetName = sheet.Name.Value;
            if (excludeJetMetadataSheet
                && string.Equals(
                    sheetName,
                    ReportWorkbookMetadataFormat.WorksheetName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var parsed = ParseSheet(worksheetPart, shared, sheetName, contentMask);
            sheets.Add(BuildPhysicalSheet(sheetName, parsed));

            var baseName = ResolveDirectFamilyBase(familyHeaderRows.Keys, sheetName);
            if (baseName is not null)
            {
                if (!familyBuilders.TryGetValue(baseName, out var builder))
                {
                    builder = new DirectFamilyBuilder(familyHeaderRows[baseName]);
                    familyBuilders.Add(baseName, builder);
                }
                builder.Append(parsed);
            }
        }

        if (sheets.Count == 0)
        {
            throw new InvalidDataException("Normalized capture workbook has no worksheets.");
        }
        return new LegacyAuditParityNormalizedWorkbook(
            reportSlug,
            sheets,
            familyBuilders
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Value.Build(pair.Key))
                .ToArray());
    }

    /// <summary>續頁沿用基底名稱＋「 (」起始的後綴（`(2)`／`(續2)` 兩型，§7.2）。</summary>
    private static string? ResolveDirectFamilyBase(
        IEnumerable<string> baseNames,
        string sheetName)
    {
        foreach (var baseName in baseNames)
        {
            if (string.Equals(sheetName, baseName, StringComparison.Ordinal)
                || (sheetName.StartsWith(baseName, StringComparison.Ordinal)
                    && sheetName.Length > baseName.Length
                    && sheetName.AsSpan(baseName.Length).StartsWith(" (")))
            {
                return baseName;
            }
        }
        return null;
    }

    private sealed record ParsedRow(
        int RowIndex,
        IReadOnlyList<(int Column, string Kind, string Value, string Formula)> Cells);

    private sealed record ParsedSheet(IReadOnlyList<ParsedRow> Rows);

    private sealed class DirectFamilyBuilder(int headerRowIndex)
    {
        private readonly Dictionary<string, List<(string Value, string Formula)>> _columns =
            new(StringComparer.Ordinal);
        private int _dataRowCount;
        private IReadOnlyList<string>? _columnNameHashes;
        private IReadOnlyDictionary<int, string>? _ordinalKeys;

        internal void Append(ParsedSheet parsed)
        {
            foreach (var row in parsed.Rows)
            {
                // 標題列與其上方的前導區（報表標題、期間、產表資訊）不屬於明細
                // 資料列；它們仍由物理 sheet 的 storage 層完整涵蓋。續頁重複欄標
                // （jet-guide §7.2），欄名對齊一律以首張基底頁的標題列為準。
                if (row.RowIndex <= headerRowIndex)
                {
                    if (row.RowIndex == headerRowIndex && _columnNameHashes is null)
                    {
                        CaptureHeader(row);
                    }
                    continue;
                }

                AppendDataRow(row);
            }
        }

        private void CaptureHeader(ParsedRow row)
        {
            var names = new List<string>();
            var ordinalKeys = new Dictionary<int, string>();
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (column, _, value, _) in row.Cells.OrderBy(static cell => cell.Column))
            {
                var nameHash = HashToken(value.Trim());
                names.Add(nameHash);
                var occurrence = occurrences.GetValueOrDefault(nameHash);
                occurrences[nameHash] = occurrence + 1;
                var key = occurrence == 0 ? nameHash : $"{nameHash}#{occurrence}";
                ordinalKeys[column] = key;
            }
            _columnNameHashes = names;
            _ordinalKeys = ordinalKeys;
        }

        private void AppendDataRow(ParsedRow row)
        {
            _dataRowCount++;
            var ordinalKeys = _ordinalKeys ?? new Dictionary<int, string>();
            foreach (var (column, kind, value, formula) in row.Cells)
            {
                var semantic = NormalizeSemanticValue(kind, value);
                if (semantic == "0" && formula.Length == 0)
                {
                    continue;
                }
                var key = ordinalKeys.GetValueOrDefault(
                    column,
                    $"o{column.ToString(CultureInfo.InvariantCulture)}");
                if (!_columns.TryGetValue(key, out var values))
                {
                    values = [];
                    _columns.Add(key, values);
                }
                values.Add((semantic, formula));
            }
        }

        internal LegacyAuditParityNormalizedDirectFamily Build(string baseName) => new(
            baseName,
            _columnNameHashes ?? [],
            _dataRowCount,
            _columns
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => new LegacyAuditParityNormalizedFamilyColumn(
                    pair.Key,
                    DigestValues(pair.Value
                        .OrderBy(static entry => entry.Value, StringComparer.Ordinal)
                        .ThenBy(static entry => entry.Formula, StringComparer.Ordinal)),
                    DigestValues(pair.Value),
                    pair.Value.Count,
                    BucketValues(pair.Value)))
                .ToArray());

        /// <summary>64-bucket 值分布向量：跨側 L1 距離即為每欄差值數的下界估計。</summary>
        private static int[] BucketValues(IEnumerable<(string Value, string Formula)> values)
        {
            var buckets = new int[64];
            foreach (var (value, formula) in values)
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                AppendHashString(hash, value);
                AppendHashString(hash, formula);
                buckets[hash.GetHashAndReset()[0] & 0x3F]++;
            }
            return buckets;
        }

        private static string DigestValues(IEnumerable<(string Value, string Formula)> values)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var (value, formula) in values)
            {
                AppendHashString(hash, value);
                AppendHashString(hash, formula);
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset().AsSpan(0, 16));
        }
    }

    private static ParsedSheet ParseSheet(
        WorksheetPart worksheetPart,
        IReadOnlyList<string> shared,
        string sheetName,
        Func<string, int, int, bool>? contentMask)
    {
        var rows = new List<ParsedRow>();
        using var reader = new OpenXmlPartReader(worksheetPart);
        var fallbackRowIndex = 0;
        while (reader.Read())
        {
            if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
            {
                continue;
            }

            var row = (Row)reader.LoadCurrentElement()!;
            var rowIndex = (int)(row.RowIndex?.Value ?? (uint)(fallbackRowIndex + 1));
            fallbackRowIndex = rowIndex;
            var cells = new List<(int Column, string Kind, string Value, string Formula)>();
            var fallbackColumn = 0;
            var maskedCellCount = 0;
            foreach (var cell in row.Elements<Cell>())
            {
                var column = ParseColumn(cell.CellReference?.Value) ?? fallbackColumn + 1;
                fallbackColumn = column;
                if (contentMask?.Invoke(sheetName, rowIndex, column) == true)
                {
                    maskedCellCount++;
                    continue;
                }
                var formula = cell.CellFormula?.Text?.Trim() ?? string.Empty;
                var (kind, value) = ReadCanonicalValue(cell, shared);
                if (value.Length == 0 && formula.Length == 0)
                {
                    continue;
                }
                cells.Add((column, kind, value, formula));
            }

            // A row whose content is entirely random still participates in row-count
            // comparison. The fixed sentinel carries no source value and never becomes
            // part of a receipt.
            if (cells.Count == 0 && maskedCellCount > 0)
            {
                cells.Add((0, "masked-row", "masked", string.Empty));
            }

            if (cells.Count > 0)
            {
                rows.Add(new ParsedRow(rowIndex, cells));
            }
        }
        return new ParsedSheet(rows);
    }

    private static LegacyAuditParityNormalizedSheet BuildPhysicalSheet(
        string sheetName,
        ParsedSheet parsed)
    {
        var rowHashes = new List<string>();
        var kindCounts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        long cellCount = 0;
        long formulaCellCount = 0;
        foreach (var row in parsed.Rows)
        {
            using var rowHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var (column, kind, value, formula) in row.Cells)
            {
                cellCount++;
                if (formula.Length > 0)
                {
                    formulaCellCount++;
                }
                kindCounts[kind] = kindCounts.GetValueOrDefault(kind) + 1;

                rowHash.AppendData(BitConverter.GetBytes(column));
                AppendHashString(rowHash, kind);
                AppendHashString(rowHash, value);
                AppendHashString(rowHash, formula);
            }
            rowHashes.Add(Convert.ToHexStringLower(rowHash.GetHashAndReset().AsSpan(0, 16)));
        }

        return new LegacyAuditParityNormalizedSheet(
            sheetName,
            rowHashes.Count,
            cellCount,
            formulaCellCount,
            kindCounts,
            rowHashes);
    }

    private static (string Kind, string Value) ReadCanonicalValue(
        Cell cell,
        IReadOnlyList<string> shared)
    {
        var raw = cell.CellValue?.InnerText ?? string.Empty;
        var dataType = cell.DataType?.Value;
        if (dataType == CellValues.SharedString)
        {
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                || index < 0
                || index >= shared.Count)
            {
                throw new InvalidDataException("Normalized capture met an unresolvable shared string.");
            }
            return ("text", shared[index]);
        }
        if (dataType == CellValues.InlineString || dataType == CellValues.String)
        {
            return ("text", dataType == CellValues.InlineString
                ? cell.InlineString is null
                    ? string.Empty
                    : string.Concat(cell.InlineString.Descendants<Text>()
                        .Select(static text => text.Text))
                : raw);
        }
        if (dataType == CellValues.Boolean)
        {
            return ("boolean", raw == "1" ? "TRUE" : "FALSE");
        }
        if (dataType == CellValues.Error)
        {
            return ("error", raw);
        }

        // 無 DataType 或 Number：OpenXML 數字 lexeme 收斂為 invariant G29。
        if (raw.Length == 0)
        {
            return ("number", string.Empty);
        }
        return ("number", decimal.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var exact)
            ? exact.ToString("G29", CultureInfo.InvariantCulture)
            : double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var wide)
                ? wide.ToString("R", CultureInfo.InvariantCulture)
                : raw);
    }

    /// <summary>
    /// jet-guide §3.1.2／§3.1.3 匯入可接受格式的 value 正規化。文字金額
    /// （千分位、前置正負號、會計零 `-`）收斂為 G29；文字日期（ISO、
    /// `yyyy/M/d`、`yyyy.M.d`、8 位西元、民國年 `1yy/M/d`／`1yyMMdd`）收斂為
    /// `yyyy-MM-dd`。不處理貨幣符號、括號負數、歐陸格式與兩位數年；
    /// 數字 serial 不臆測為日期。
    /// </summary>
    internal static string NormalizeSemanticValue(string kind, string value)
    {
        if (kind != "text")
        {
            return value;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return value;
        }
        if (trimmed == "-")
        {
            return "0";
        }
        if (TryNormalizeAmount(trimmed, out var amount))
        {
            return amount;
        }
        return TryNormalizeDate(trimmed, out var date) ? date : value;
    }

    private static bool TryNormalizeAmount(string trimmed, out string normalized)
    {
        normalized = string.Empty;
        var digits = 0;
        var separators = false;
        foreach (var character in trimmed)
        {
            if (char.IsAsciiDigit(character))
            {
                digits++;
                continue;
            }
            if (character is ',' or '.' or '+' or '-')
            {
                if (character is ',')
                {
                    separators = true;
                }
                continue;
            }
            return false;
        }
        if (digits == 0)
        {
            return false;
        }

        var candidate = separators
            ? trimmed.Replace(",", string.Empty, StringComparison.Ordinal)
            : trimmed;
        if (!decimal.TryParse(
                candidate,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var exact))
        {
            return false;
        }
        normalized = exact.ToString("G29", CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryNormalizeDate(string trimmed, out string normalized)
    {
        normalized = string.Empty;
        // 純數字 lexeme（8 位西元、7 位民國）在本比較器一律先被金額正規化收斂，
        // 兩側同形，因此這裡只處理帶分隔符的日期寫法。
        string[] westernFormats =
        [
            "yyyy-MM-dd",
            "yyyy/M/d",
            "yyyy/MM/dd",
            "yyyy.M.d",
            "yyyy.MM.dd",
        ];
        if (DateOnly.TryParseExact(
                trimmed,
                westernFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var western)
            && western.Year is >= 1900 and <= 2100)
        {
            normalized = western.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        // 民國年：3 位數年＋分隔（114/6/11、114.6.11）或 7 位數 1yyMMdd。
        var rocSeparated = trimmed.Split(['/', '.']);
        if (rocSeparated.Length == 3
            && rocSeparated[0].Length == 3
            && int.TryParse(rocSeparated[0], NumberStyles.None, CultureInfo.InvariantCulture, out var rocYear)
            && rocYear is >= 100 and <= 199
            && int.TryParse(rocSeparated[1], NumberStyles.None, CultureInfo.InvariantCulture, out var rocMonth)
            && int.TryParse(rocSeparated[2], NumberStyles.None, CultureInfo.InvariantCulture, out var rocDay)
            && TryCreateDate(rocYear + 1911, rocMonth, rocDay, out var separatedRoc))
        {
            normalized = separatedRoc;
            return true;
        }
        return false;
    }

    private static bool TryCreateDate(int year, int month, int day, out string normalized)
    {
        normalized = string.Empty;
        if (month is < 1 or > 12 || day < 1)
        {
            return false;
        }
        try
        {
            normalized = new DateOnly(year, month, day)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string HashToken(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToHexStringLower(SHA256.HashData(bytes).AsSpan(0, 16));
    }

    private static void AppendHashString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static int? ParseColumn(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        var value = 0;
        foreach (var character in reference)
        {
            if (!char.IsAsciiLetter(character))
            {
                break;
            }
            value = checked(value * 26 + char.ToUpperInvariant(character) - 'A' + 1);
        }
        return value == 0 ? null : value;
    }
}

internal enum LegacyAuditParityContentDimension
{
    WorksheetSetAndOrder,
    RowCount,
    ColumnHeaders,
    ColumnValuesUnmatched,
    RowValues,
    RowValueSequence,
    CellStorage,
    FormulaCells,
}

/// <summary>
/// 一筆 content 差異：只有維度、sheet／家族名稱與 aggregate count，沒有任何 cell 值。
/// </summary>
internal sealed record LegacyAuditParityContentDifference(
    string ReportSlug,
    string SheetName,
    LegacyAuditParityContentDimension Dimension,
    long DifferenceCount)
{
    public override string ToString() =>
        $"content-difference({ReportSlug}/{SheetName}/{Dimension}/{DifferenceCount})";
}

/// <summary>
/// Direct 家族的可比性覆蓋統計：共有欄（名稱對齊）、值配對欄（名稱不同但值
/// 多重集相等）、未配對獨有欄與差值共有欄的形狀。只含 counts 與 hash。
/// </summary>
internal sealed record LegacyAuditParityContentFamilyCoverage(
    string FamilyName,
    int SharedColumnCount,
    int ValueMatchedColumnCount,
    int UnmatchedExpectedColumnCount,
    int UnmatchedActualColumnCount,
    int ExpectedRowCount,
    int ActualRowCount,
    IReadOnlyList<LegacyAuditParityContentColumnMismatch> DifferingSharedColumns);

internal sealed record LegacyAuditParityContentColumnMismatch(
    string ColumnKey,
    int ExpectedValueCount,
    int ActualValueCount,
    int EstimatedDifferingValues);

internal sealed record LegacyAuditParityContentComparisonResult(
    string ReportSlug,
    int ComparedSheetCount,
    int ComparedDimensionCount,
    IReadOnlyList<LegacyAuditParityContentDifference> Differences,
    IReadOnlyList<LegacyAuditParityContentFamilyCoverage> FamilyCoverage);

/// <summary>
/// JET 輸出對 legacy reference 的 normalized content 比較。sheet 集合與順序是
/// workbook 級維度；物理 sheet 量測 RowCount 與 CellStorage／FormulaCells；
/// direct 家族以欄名對齊量測 ColumnHeaders（欄位集合／名稱／順序）、RowValues
/// （共有欄值多重集）與 RowValueSequence（值相等但列順序不同）。
/// </summary>
internal static class LegacyAuditParityNormalizedContentComparator
{
    internal static LegacyAuditParityContentComparisonResult Compare(
        LegacyAuditParityNormalizedWorkbook expected,
        LegacyAuditParityNormalizedWorkbook actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        if (!string.Equals(expected.ReportSlug, actual.ReportSlug, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Normalized content comparison requires the same report identity.");
        }

        var differences = new List<LegacyAuditParityContentDifference>();
        var expectedNames = expected.Sheets.Select(static sheet => sheet.SheetName).ToArray();
        var actualNames = actual.Sheets.Select(static sheet => sheet.SheetName).ToArray();
        var comparedDimensions = 1;
        if (!expectedNames.SequenceEqual(actualNames, StringComparer.Ordinal))
        {
            var onlyExpected = expectedNames.Except(actualNames, StringComparer.Ordinal).Count();
            var onlyActual = actualNames.Except(expectedNames, StringComparer.Ordinal).Count();
            var orderOnly = onlyExpected == 0 && onlyActual == 0;
            differences.Add(new LegacyAuditParityContentDifference(
                expected.ReportSlug,
                "*",
                LegacyAuditParityContentDimension.WorksheetSetAndOrder,
                orderOnly ? 1 : onlyExpected + onlyActual));
        }

        var actualByName = actual.Sheets
            .GroupBy(static sheet => sheet.SheetName, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var comparedSheets = 0;
        foreach (var expectedSheet in expected.Sheets)
        {
            if (!actualByName.TryGetValue(expectedSheet.SheetName, out var actualSheet))
            {
                continue;
            }

            comparedSheets++;
            comparedDimensions += 3;
            ComparePhysicalSheet(expected.ReportSlug, expectedSheet, actualSheet, differences);
        }

        var actualFamilies = actual.DirectFamilies
            .ToDictionary(
                static family => family.BaseSheetName,
                StringComparer.Ordinal);
        var familyNames = expected.DirectFamilies
            .Select(static family => family.BaseSheetName)
            .Union(
                actual.DirectFamilies.Select(static family => family.BaseSheetName),
                StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal);
        var expectedFamilies = expected.DirectFamilies
            .ToDictionary(
                static family => family.BaseSheetName,
                StringComparer.Ordinal);
        var coverage = new List<LegacyAuditParityContentFamilyCoverage>();
        foreach (var familyName in familyNames)
        {
            comparedDimensions += 2;
            coverage.Add(CompareDirectFamily(
                expected.ReportSlug,
                familyName,
                expectedFamilies.GetValueOrDefault(familyName),
                actualFamilies.GetValueOrDefault(familyName),
                differences));
        }

        return new LegacyAuditParityContentComparisonResult(
            expected.ReportSlug,
            comparedSheets,
            comparedDimensions,
            differences.ToArray(),
            coverage.ToArray());
    }

    private static void ComparePhysicalSheet(
        string reportSlug,
        LegacyAuditParityNormalizedSheet expected,
        LegacyAuditParityNormalizedSheet actual,
        ICollection<LegacyAuditParityContentDifference> differences)
    {
        if (expected.WrittenRowCount != actual.WrittenRowCount)
        {
            differences.Add(new LegacyAuditParityContentDifference(
                reportSlug,
                expected.SheetName,
                LegacyAuditParityContentDimension.RowCount,
                Math.Abs((long)expected.WrittenRowCount - actual.WrittenRowCount)));
        }

        var storageDifference = MultisetDifference(expected.RowHashes, actual.RowHashes);
        if (storageDifference == 0
            && !expected.RowHashes.SequenceEqual(actual.RowHashes, StringComparer.Ordinal))
        {
            storageDifference = 1;
        }
        if (storageDifference > 0)
        {
            differences.Add(new LegacyAuditParityContentDifference(
                reportSlug,
                expected.SheetName,
                LegacyAuditParityContentDimension.CellStorage,
                storageDifference));
        }

        if (expected.FormulaCellCount != actual.FormulaCellCount)
        {
            differences.Add(new LegacyAuditParityContentDifference(
                reportSlug,
                expected.SheetName,
                LegacyAuditParityContentDimension.FormulaCells,
                Math.Abs(expected.FormulaCellCount - actual.FormulaCellCount)));
        }
    }

    private static LegacyAuditParityContentFamilyCoverage CompareDirectFamily(
        string reportSlug,
        string familyName,
        LegacyAuditParityNormalizedDirectFamily? expected,
        LegacyAuditParityNormalizedDirectFamily? actual,
        ICollection<LegacyAuditParityContentDifference> differences)
    {
        var expectedColumns = expected?.ColumnNameHashes ?? [];
        var actualColumns = actual?.ColumnNameHashes ?? [];
        var columnDifference = MultisetDifference(expectedColumns, actualColumns);
        if (columnDifference == 0
            && !expectedColumns.SequenceEqual(actualColumns, StringComparer.Ordinal))
        {
            columnDifference = 1;
        }
        if (columnDifference > 0)
        {
            differences.Add(new LegacyAuditParityContentDifference(
                reportSlug,
                familyName,
                LegacyAuditParityContentDimension.ColumnHeaders,
                columnDifference));
        }

        var expectedRows = expected?.DataRowCount ?? 0;
        var actualRows = actual?.DataRowCount ?? 0;
        if (expectedRows != actualRows)
        {
            differences.Add(new LegacyAuditParityContentDifference(
                reportSlug,
                familyName,
                LegacyAuditParityContentDimension.RowCount,
                Math.Abs((long)expectedRows - actualRows)));
        }

        // 值層只比較兩側共有欄：欄位集合差異由 ColumnHeaders 承擔，值差異
        // 不因單側獨有欄而連坐。
        var expectedByKey = (expected?.Columns ?? [])
            .ToDictionary(static column => column.Key, StringComparer.Ordinal);
        var actualByKey = (actual?.Columns ?? [])
            .ToDictionary(static column => column.Key, StringComparer.Ordinal);
        long valueColumns = 0;
        long sequenceColumns = 0;
        var sharedColumns = 0;
        var differingShared = new List<LegacyAuditParityContentColumnMismatch>();
        foreach (var (key, expectedColumn) in expectedByKey
                     .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!actualByKey.TryGetValue(key, out var actualColumn))
            {
                continue;
            }
            sharedColumns++;
            if (!string.Equals(
                    expectedColumn.MultisetDigest,
                    actualColumn.MultisetDigest,
                    StringComparison.Ordinal))
            {
                valueColumns++;
                var estimate = 0;
                for (var bucket = 0; bucket < 64; bucket++)
                {
                    estimate += Math.Abs(
                        expectedColumn.BucketCounts.ElementAtOrDefault(bucket)
                        - actualColumn.BucketCounts.ElementAtOrDefault(bucket));
                }
                differingShared.Add(new LegacyAuditParityContentColumnMismatch(
                    key,
                    expectedColumn.ValueCount,
                    actualColumn.ValueCount,
                    estimate));
            }
            else if (!string.Equals(
                         expectedColumn.SequenceDigest,
                         actualColumn.SequenceDigest,
                         StringComparison.Ordinal))
            {
                sequenceColumns++;
            }
        }
        if (valueColumns > 0)
        {
            differences.Add(new LegacyAuditParityContentDifference(
                reportSlug,
                familyName,
                LegacyAuditParityContentDimension.RowValues,
                valueColumns));
        }
        if (sequenceColumns > 0)
        {
            differences.Add(new LegacyAuditParityContentDifference(
                reportSlug,
                familyName,
                LegacyAuditParityContentDimension.RowValueSequence,
                sequenceColumns));
        }

        // 名稱不共有的獨有欄再以值多重集互相配對：配對成功＝改名但值相等，
        // 配對失敗的殘餘（IDEA 衍生欄、被 IDEA 丟棄或轉換的來源欄）另計維度。
        var exclusiveExpected = (expected?.Columns ?? [])
            .Where(column => !actualByKey.ContainsKey(column.Key))
            .ToArray();
        var exclusiveActual = (actual?.Columns ?? [])
            .Where(column => !expectedByKey.ContainsKey(column.Key))
            .ToArray();
        var actualDigestCounts = exclusiveActual
            .GroupBy(static column => column.MultisetDigest, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Count(),
                StringComparer.Ordinal);
        var valueMatched = 0;
        foreach (var column in exclusiveExpected)
        {
            if (actualDigestCounts.TryGetValue(column.MultisetDigest, out var available)
                && available > 0)
            {
                valueMatched++;
                actualDigestCounts[column.MultisetDigest] = available - 1;
            }
        }
        var unmatchedExpected = exclusiveExpected.Length - valueMatched;
        var unmatchedActual = exclusiveActual.Length - valueMatched;
        if (unmatchedExpected + unmatchedActual > 0)
        {
            differences.Add(new LegacyAuditParityContentDifference(
                reportSlug,
                familyName,
                LegacyAuditParityContentDimension.ColumnValuesUnmatched,
                unmatchedExpected + unmatchedActual));
        }

        return new LegacyAuditParityContentFamilyCoverage(
            familyName,
            sharedColumns,
            valueMatched,
            unmatchedExpected,
            unmatchedActual,
            expectedRows,
            actualRows,
            differingShared.ToArray());
    }

    internal static long MultisetDifference(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var hash in expected)
        {
            counts[hash] = counts.GetValueOrDefault(hash) + 1;
        }
        foreach (var hash in actual)
        {
            counts[hash] = counts.GetValueOrDefault(hash) - 1;
        }
        return counts.Values.Sum(Math.Abs);
    }
}

/// <summary>
/// Normalized content capture 的 ignored 落地。JSON 內只有 sheet 名稱、counts 與
/// hash；寫入前驗證 root 已被 Git ignore，重讀驗證 canonical 等值。
/// </summary>
internal static class LegacyAuditParityNormalizedContentStore
{
    private const string Schema = "legacy-audit-parity-normalized-content/v6";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    internal static string Write(
        string destinationPath,
        LegacyAuditParityNormalizedWorkbook workbook)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(workbook);
        var target = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(target)
            ?? throw new ArgumentException(
                "Normalized content target must have a parent directory.",
                nameof(destinationPath));
        Directory.CreateDirectory(directory);
        var dto = new WorkbookDto
        {
            SchemaVersion = Schema,
            ReportSlug = workbook.ReportSlug,
            ContentDigest = workbook.ContentDigest(),
            Sheets = workbook.Sheets.Select(static sheet => new SheetDto
            {
                SheetName = sheet.SheetName,
                WrittenRowCount = sheet.WrittenRowCount,
                CellCount = sheet.CellCount,
                FormulaCellCount = sheet.FormulaCellCount,
                CellKindCounts = sheet.CellKindCounts.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value),
                RowHashes = sheet.RowHashes.ToArray(),
            }).ToArray(),
            DirectFamilies = workbook.DirectFamilies.Select(static family => new FamilyDto
            {
                BaseSheetName = family.BaseSheetName,
                ColumnNameHashes = family.ColumnNameHashes.ToArray(),
                DataRowCount = family.DataRowCount,
                Columns = family.Columns.Select(static column => new FamilyColumnDto
                {
                    Key = column.Key,
                    MultisetDigest = column.MultisetDigest,
                    SequenceDigest = column.SequenceDigest,
                    ValueCount = column.ValueCount,
                    BucketCounts = column.BucketCounts.ToArray(),
                }).ToArray(),
            }).ToArray(),
        };
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions));
            var reloaded = Load(temporary);
            if (!string.Equals(
                    reloaded.ContentDigest(),
                    workbook.ContentDigest(),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Normalized content capture did not survive canonical reload.");
            }
            File.Move(temporary, target, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        return target;
    }

    internal static LegacyAuditParityNormalizedWorkbook Load(string path)
    {
        var dto = JsonSerializer.Deserialize<WorkbookDto>(
                File.ReadAllBytes(Path.GetFullPath(path)),
                JsonOptions)
            ?? throw new InvalidDataException("Normalized content capture is unreadable.");
        if (!string.Equals(dto.SchemaVersion, Schema, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(dto.ReportSlug)
            || dto.Sheets is null
            || dto.Sheets.Length == 0
            || dto.DirectFamilies is null)
        {
            throw new InvalidDataException("Normalized content capture violates its schema.");
        }

        var workbook = new LegacyAuditParityNormalizedWorkbook(
            dto.ReportSlug!,
            dto.Sheets.Select(static sheet => new LegacyAuditParityNormalizedSheet(
                sheet!.SheetName ?? throw new InvalidDataException(
                    "Normalized content sheet has no name."),
                sheet.WrittenRowCount,
                sheet.CellCount,
                sheet.FormulaCellCount,
                new SortedDictionary<string, long>(
                    sheet.CellKindCounts ?? [],
                    StringComparer.Ordinal),
                sheet.RowHashes ?? [])).ToArray(),
            dto.DirectFamilies.Select(static family => new LegacyAuditParityNormalizedDirectFamily(
                family!.BaseSheetName ?? throw new InvalidDataException(
                    "Normalized content family has no base name."),
                family.ColumnNameHashes ?? [],
                family.DataRowCount,
                (family.Columns ?? []).Select(static column =>
                    new LegacyAuditParityNormalizedFamilyColumn(
                        column!.Key ?? throw new InvalidDataException(
                            "Normalized content family column has no key."),
                        column.MultisetDigest ?? string.Empty,
                        column.SequenceDigest ?? string.Empty,
                        column.ValueCount,
                        column.BucketCounts ?? [])).ToArray())).ToArray());
        if (!string.Equals(workbook.ContentDigest(), dto.ContentDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Normalized content capture digest mismatch.");
        }
        return workbook;
    }

    private sealed class WorkbookDto
    {
        public string? SchemaVersion { get; set; }

        public string? ReportSlug { get; set; }

        public string? ContentDigest { get; set; }

        public SheetDto?[]? Sheets { get; set; }

        public FamilyDto?[]? DirectFamilies { get; set; }
    }

    private sealed class SheetDto
    {
        public string? SheetName { get; set; }

        public int WrittenRowCount { get; set; }

        public long CellCount { get; set; }

        public long FormulaCellCount { get; set; }

        public Dictionary<string, long>? CellKindCounts { get; set; }

        public string[]? RowHashes { get; set; }
    }

    private sealed class FamilyDto
    {
        public string? BaseSheetName { get; set; }

        public string[]? ColumnNameHashes { get; set; }

        public int DataRowCount { get; set; }

        public FamilyColumnDto?[]? Columns { get; set; }
    }

    private sealed class FamilyColumnDto
    {
        public string? Key { get; set; }

        public string? MultisetDigest { get; set; }

        public string? SequenceDigest { get; set; }

        public int ValueCount { get; set; }

        public int[]? BucketCounts { get; set; }
    }
}
