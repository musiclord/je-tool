using DocumentFormat.OpenXml.Spreadsheet;
using SpreadsheetFont = DocumentFormat.OpenXml.Spreadsheet.Font;

namespace JET.Infrastructure;

/// <summary>
/// Validation Report 與 Working Paper 的欄位資訊頁共用的欄位、黃色表頭與欄寬上限。
/// 字級跟著所在報告：驗證報告 10 點、版本列不加藍底；底稿 11 點、保留藍底紅字的版本列（使用者 2026-10-29 裁定）。
/// </summary>
internal static class FormalFieldInfoAppearance
{
    internal const double HeaderFontSize = 12D;
    internal const double VersionFontSize = 16D;

    /// <summary>欄位名稱欄的寬度上限；「由系統產生」的長說明超過時換行，不再把整欄撐到一百多個字元。</summary>
    internal const double NameColumnMaxWidth = 40D;

    /// <summary>底稿的版本列。驗證報告的版本列只用粗體。</summary>
    internal static readonly WorkbookStylePatch Version = new(
        FontName: FormalWorkbookTypography.CanonicalFontName,
        FontSize: VersionFontSize,
        Bold: true,
        FontColorArgb: "FFFF0000",
        FillForegroundArgb: "FF0066FF");

    internal static readonly WorkbookStylePatch TbHeader = new(
        FontName: FormalWorkbookTypography.CanonicalFontName,
        FontSize: HeaderFontSize,
        Bold: true,
        FillForegroundArgb: "FFF0F000");

    internal static readonly WorkbookStylePatch GlHeader = new(
        FontName: FormalWorkbookTypography.CanonicalFontName,
        FontSize: HeaderFontSize,
        Bold: true,
        FillForegroundArgb: "FFE6E600");

    /// <summary>欄位名稱超過上限時要換成幾行；不超過時是 1。</summary>
    internal static int NameLineCount(string? name, double nameColumnWidth) =>
        ExcelDisplayWidth.WrappedLineCount(name, nameColumnWidth);
}

/// <summary>Validation 欄位資訊頁只需的 borderless regular／bold／換行基底，字級和報告其他頁同為 10 點。</summary>
internal static class FormalFieldInfoStyles
{
    internal const uint Default = 0;
    internal const uint Bold = 1;
    internal const uint Wrapped = 2;
    internal const double BaseFontSize = 10D;

    internal static Stylesheet Build()
    {
        var fonts = new Fonts(
            new SpreadsheetFont(
                new FontSize { Val = BaseFontSize },
                new FontName { Val = FormalWorkbookTypography.CanonicalFontName }),
            new SpreadsheetFont(
                new Bold(),
                new FontSize { Val = BaseFontSize },
                new FontName { Val = FormalWorkbookTypography.CanonicalFontName }))
        {
            Count = 2
        };
        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }))
        {
            Count = 2
        };
        var borders = new Borders(
            new Border(
                new LeftBorder(),
                new RightBorder(),
                new TopBorder(),
                new BottomBorder(),
                new DiagonalBorder()))
        {
            Count = 1
        };
        var styleFormats = new CellStyleFormats(
            new CellFormat
            {
                NumberFormatId = 0,
                FontId = 0,
                FillId = 0,
                BorderId = 0
            })
        {
            Count = 1
        };
        var formats = new CellFormats(
            Borderless(fontId: 0, horizontalLeft: false),
            Borderless(fontId: 1, horizontalLeft: true),
            Borderless(fontId: 0, horizontalLeft: true, wrap: true))
        {
            Count = 3
        };

        return new Stylesheet(fonts, fills, borders, styleFormats, formats);
    }

    private static CellFormat Borderless(uint fontId, bool horizontalLeft, bool wrap = false)
    {
        var alignment = new Alignment
        {
            Vertical = VerticalAlignmentValues.Top,
            Indent = 0U,
            ShrinkToFit = false,
            WrapText = wrap
        };
        if (horizontalLeft)
        {
            alignment.Horizontal = HorizontalAlignmentValues.Left;
        }

        return new CellFormat
        {
            NumberFormatId = 0,
            FontId = fontId,
            FillId = 0,
            BorderId = 0,
            FormatId = 0,
            ApplyFont = true,
            ApplyFill = false,
            ApplyAlignment = true,
            Alignment = alignment
        };
    }
}
