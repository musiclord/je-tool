using DocumentFormat.OpenXml.Spreadsheet;
using SpreadsheetFont = DocumentFormat.OpenXml.Spreadsheet.Font;

namespace JET.Infrastructure;

/// <summary>
/// Validation Report 與 Working Paper 的欄位資訊頁共用外觀。
/// 基底 cellXf 由 <see cref="WorkpaperStyles"/> 提供，因此可見區維持無框線。
/// </summary>
internal static class FormalFieldInfoAppearance
{
    internal static readonly WorkbookStylePatch Version = new(
        FontName: FormalWorkbookTypography.CanonicalFontName,
        FontSize: 16D,
        Bold: true,
        FontColorArgb: "FFFF0000",
        FillForegroundArgb: "FF0066FF");

    internal static readonly WorkbookStylePatch TbHeader = new(
        FontName: FormalWorkbookTypography.CanonicalFontName,
        FontSize: 12D,
        Bold: true,
        FillForegroundArgb: "FFF0F000");

    internal static readonly WorkbookStylePatch GlHeader = new(
        FontName: FormalWorkbookTypography.CanonicalFontName,
        FontSize: 12D,
        Bold: true,
        FillForegroundArgb: "FFE6E600");
}

/// <summary>Validation 欄位資訊頁只需的 borderless regular／bold 基底。</summary>
internal static class FormalFieldInfoStyles
{
    internal const uint Default = 0;
    internal const uint Bold = 1;

    internal static Stylesheet Build()
    {
        var fonts = new Fonts(
            new SpreadsheetFont(
                new FontSize { Val = 11D },
                new FontName { Val = FormalWorkbookTypography.CanonicalFontName }),
            new SpreadsheetFont(
                new Bold(),
                new FontSize { Val = 11D },
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
            Borderless(fontId: 1, horizontalLeft: true))
        {
            Count = 2
        };

        return new Stylesheet(fonts, fills, borders, styleFormats, formats);
    }

    private static CellFormat Borderless(uint fontId, bool horizontalLeft)
    {
        var alignment = new Alignment
        {
            Vertical = VerticalAlignmentValues.Top,
            Indent = 0U,
            ShrinkToFit = false,
            WrapText = false
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
