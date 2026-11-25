using System.Globalization;
using DocumentFormat.OpenXml.Spreadsheet;

namespace JET.Infrastructure;

/// <summary>
/// 一張工作表寫出的格子涵蓋的範圍：最上面一列到最下面一列、最左一欄到最右一欄，只有樣式、沒有值的格子也算。
/// 這個範圍寫在工作表開頭的 dimension，和 Excel 存檔時記下的使用範圍相同，其他讀 xlsx 的程式靠它知道工作表有多大。
/// 沒有任何格子的工作表，Excel 寫 A1。
/// </summary>
internal readonly record struct SheetUsedRange
{
    private SheetUsedRange(uint firstRow, uint firstColumn, uint lastRow, uint lastColumn)
    {
        FirstRow = firstRow;
        FirstColumn = firstColumn;
        LastRow = lastRow;
        LastColumn = lastColumn;
    }

    /// <summary>沒有任何格子。</summary>
    internal static SheetUsedRange Empty => default;

    internal uint FirstRow { get; }

    internal uint FirstColumn { get; }

    internal uint LastRow { get; }

    internal uint LastColumn { get; }

    internal bool IsEmpty => FirstRow == 0;

    /// <summary>dimension 的 ref：只有一格時寫那一格，例如 A1；多格時寫左上角到右下角，例如 A1:I3。沒有格子時寫 A1。</summary>
    internal string Reference
    {
        get
        {
            if (IsEmpty)
            {
                return "A1";
            }

            var first = ReportSheetWriter.Reference(FirstColumn, FirstRow);
            return FirstRow == LastRow && FirstColumn == LastColumn
                ? first
                : first + ":" + ReportSheetWriter.Reference(LastColumn, LastRow);
        }
    }

    /// <summary>左上角到右下角的範圍。兩角都要在 Excel 工作表的範圍內，左上角不能在右下角的右邊或下面。</summary>
    internal static SheetUsedRange Of(uint firstRow, uint firstColumn, uint lastRow, uint lastColumn)
    {
        ExcelWorksheetConstraints.EnsureCell(firstRow, firstColumn);
        ExcelWorksheetConstraints.EnsureCell(lastRow, lastColumn);
        if (firstRow > lastRow || firstColumn > lastColumn)
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstRow),
                $"Used range top-left ({firstRow}, {firstColumn}) is beyond bottom-right ({lastRow}, {lastColumn}).");
        }

        return new SheetUsedRange(firstRow, firstColumn, lastRow, lastColumn);
    }

    /// <summary>
    /// 從 A1 起、<paramref name="rowCount"/> 列 <paramref name="columnCount"/> 欄的表格，例如第 1 列是表頭、下面接資料列，
    /// 每一列都寫滿每一欄的明細。列數或欄數是 0 時沒有任何格子。
    /// </summary>
    internal static SheetUsedRange Table(uint rowCount, uint columnCount) =>
        rowCount == 0 || columnCount == 0
            ? Empty
            : Of(1, 1, rowCount, columnCount);

    /// <summary>加進一格之後的範圍。</summary>
    internal SheetUsedRange Include(uint row, uint column)
    {
        ExcelWorksheetConstraints.EnsureCell(row, column);
        return IsEmpty
            ? new SheetUsedRange(row, column, row, column)
            : new SheetUsedRange(
                Math.Min(FirstRow, row),
                Math.Min(FirstColumn, column),
                Math.Max(LastRow, row),
                Math.Max(LastColumn, column));
    }

    /// <summary>涵蓋兩個範圍的最小範圍。</summary>
    internal SheetUsedRange Union(SheetUsedRange other) =>
        other.IsEmpty
            ? this
            : Include(other.FirstRow, other.FirstColumn).Include(other.LastRow, other.LastColumn);

    internal bool Contains(uint row, uint column) =>
        !IsEmpty
        && row >= FirstRow && row <= LastRow
        && column >= FirstColumn && column <= LastColumn;

    /// <summary>
    /// 把要寫出的一列格子加進目前已寫出的範圍，回傳加進之後的範圍。每一格都要有位置，而且落在開頁時宣告的範圍
    /// <paramref name="declared"/> 裡；不然是程式算錯了範圍，在這一列寫出之前丟出 <see cref="InvalidOperationException"/>。
    /// </summary>
    internal SheetUsedRange IncludeDeclaredCells(SheetUsedRange declared, IEnumerable<Cell> cells, string sheetName)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var range = this;
        foreach (var cell in cells)
        {
            var reference = cell.CellReference?.Value;
            if (string.IsNullOrEmpty(reference))
            {
                throw new InvalidOperationException($"工作表「{sheetName}」要寫的格子沒有位置。");
            }

            var (row, column) = ParseCellReference(reference);
            if (!declared.Contains(row, column))
            {
                throw new InvalidOperationException(
                    $"工作表「{sheetName}」要寫的格子 {reference} 在開頁時宣告的範圍 {declared.Describe()} 之外。");
            }

            range = range.Include(row, column);
        }

        return range;
    }

    /// <summary>
    /// 關閉工作表之前核對實際寫出的範圍 <paramref name="written"/> 和開頁時宣告的 <paramref name="declared"/> 相同。
    /// 不同時 dimension 已經寫錯了，丟出 <see cref="InvalidOperationException"/>。
    /// </summary>
    internal static void EnsureWrittenAsDeclared(SheetUsedRange declared, SheetUsedRange written, string sheetName)
    {
        if (written != declared)
        {
            throw new InvalidOperationException(
                $"工作表「{sheetName}」實際寫出的範圍 {written.Describe()} 和開頁時宣告的 {declared.Describe()} 不同。");
        }
    }

    /// <summary>這些格子涵蓋的範圍。每一格都要有位置。</summary>
    internal static SheetUsedRange OfCells(IEnumerable<Cell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var range = Empty;
        foreach (var cell in cells)
        {
            var (row, column) = ParseCellReference(cell.CellReference?.Value);
            range = range.Include(row, column);
        }

        return range;
    }

    /// <summary>
    /// 依工作表目前所有的格子重算 dimension，給用 DOM 改過、往下多寫了列的範本工作表。
    /// 範本原本沒有 dimension 時，照工作表的元素順序加在 sheetPr 之後、其他元素之前。
    /// </summary>
    internal static void RefreshDimension(Worksheet worksheet)
    {
        ArgumentNullException.ThrowIfNull(worksheet);
        var reference = OfCells(worksheet.Descendants<Cell>()).Reference;
        if (worksheet.GetFirstChild<SheetDimension>() is { } dimension)
        {
            dimension.Reference = reference;
            return;
        }

        var added = new SheetDimension { Reference = reference };
        if (worksheet.GetFirstChild<SheetProperties>() is { } properties)
        {
            properties.InsertAfterSelf(added);
        }
        else
        {
            worksheet.InsertAt(added, 0);
        }
    }

    /// <summary>格子位置的列與欄，例如 AB12 是第 12 列第 28 欄。位置不合 Excel 的寫法時丟出 <see cref="InvalidDataException"/>。</summary>
    internal static (uint Row, uint Column) ParseCellReference(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            throw new InvalidDataException("Worksheet cell has no reference.");
        }

        var column = 0U;
        var index = 0;
        while (index < reference.Length && char.IsAsciiLetterUpper(reference[index]) && column <= ExcelWorksheetConstraints.MaxColumns)
        {
            column = column * 26U + (uint)(reference[index] - 'A' + 1);
            index++;
        }

        if (index == 0
            || column > ExcelWorksheetConstraints.MaxColumns
            || !uint.TryParse(reference.AsSpan(index), NumberStyles.None, CultureInfo.InvariantCulture, out var row)
            || row is 0 or > ExcelWorksheetConstraints.MaxRows)
        {
            throw new InvalidDataException($"Worksheet cell reference '{reference}' is invalid.");
        }

        return (row, column);
    }

    /// <summary>寫進錯誤訊息的說法：沒有格子時寫「沒有任何格子」，不寫看起來像有一格的 A1。</summary>
    internal string Describe() => IsEmpty ? "沒有任何格子" : Reference;

    public override string ToString() => Reference;
}
