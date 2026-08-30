namespace JET.Infrastructure;

/// <summary>
/// Excel worksheet 的硬上限與續頁命名。正式 writer 不得寫出超界的 row／column reference；
/// 大型明細在抵達上限前由各自的 orchestration emitter 換到下一張工作表。
/// </summary>
internal static class ExcelWorksheetConstraints
{
    public const uint MaxRows = 1_048_576;
    public const uint MaxColumns = 16_384;
    public const int MaxSheetNameLength = 31;

    private static readonly char[] InvalidSheetNameCharacters = ['[', ']', ':', '*', '?', '/', '\\'];

    public static void EnsureCell(uint row, uint column)
    {
        if (row is 0 or > MaxRows)
        {
            throw new InvalidOperationException(
                $"Excel row index {row} is outside 1..{MaxRows}.");
        }

        if (column is 0 or > MaxColumns)
        {
            throw new InvalidOperationException(
                $"Excel column index {column} is outside 1..{MaxColumns}.");
        }
    }

    public static string ContinuationSheetName(string baseName, int pageNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        if (pageNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        var safeBase = string.Concat(baseName.Select(character =>
            InvalidSheetNameCharacters.Contains(character) ? '_' : character)).Trim().Trim('\'');
        if (safeBase.Length == 0)
        {
            safeBase = "Sheet";
        }

        if (pageNumber == 1)
        {
            return safeBase[..Math.Min(safeBase.Length, MaxSheetNameLength)];
        }

        var suffix = $" (續{pageNumber})";
        var prefixLength = MaxSheetNameLength - suffix.Length;
        if (prefixLength <= 0)
        {
            throw new InvalidOperationException("Excel continuation suffix is longer than the sheet-name limit.");
        }

        var prefix = safeBase[..Math.Min(safeBase.Length, prefixLength)].TrimEnd().TrimEnd('\'');
        return prefix + suffix;
    }
}

/// <summary>
/// 測試可縮小大型明細的換頁列上限；正式執行預設使用 Excel 1,048,576 列硬上限。
/// 此選項只控制有續頁策略的明細，不會把其他固定版面誤縮到測試上限。
/// </summary>
internal sealed record WorkpaperWriterOptions(uint ContinuationRowLimit = ExcelWorksheetConstraints.MaxRows)
{
    public uint ValidateAndGetLimit()
    {
        if (ContinuationRowLimit is < 20 or > ExcelWorksheetConstraints.MaxRows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ContinuationRowLimit),
                $"Workpaper continuation row limit must be between 20 and {ExcelWorksheetConstraints.MaxRows}.");
        }

        return ContinuationRowLimit;
    }
}

/// <summary>
/// 測試可縮小 ValidationReport step1-3 條件表的換頁列上限；正式執行仍使用 Excel 硬上限。
/// 該表固定骨架占至第 16 列，因此至少保留第 17 列給一筆資料。
/// </summary>
internal sealed record LegacyReportWriterOptions(uint ContinuationRowLimit = ExcelWorksheetConstraints.MaxRows)
{
    public uint ValidateAndGetLimit()
    {
        if (ContinuationRowLimit is < 17 or > ExcelWorksheetConstraints.MaxRows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ContinuationRowLimit),
                $"Legacy report continuation row limit must be between 17 and {ExcelWorksheetConstraints.MaxRows}.");
        }

        return ContinuationRowLimit;
    }
}
