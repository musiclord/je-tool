using DocumentFormat.OpenXml.Spreadsheet;
using SpreadsheetFont = DocumentFormat.OpenXml.Spreadsheet.Font;
using SpreadsheetColor = DocumentFormat.OpenXml.Spreadsheet.Color;

namespace JET.Infrastructure;

/// <summary>六份 legacy 報告共用的乾淨樣式表；不複製參考檔的 metadata、圖片或外部關聯。</summary>
internal static class LegacyReportStyles
{
    // cellXfs 0-14 are the long-standing JET report styles. New legacy parity
    // styles are append-only so every existing writer keeps its stable index.
    public const uint Default = 0;
    public const uint Title = 1;
    public const uint Section = 2;
    public const uint Header = 3;
    public const uint Body = 4;
    public const uint Amount = 5;
    public const uint Integer = 6;
    public const uint Editable = 7;
    public const uint Note = 8;
    public const uint WarningInteger = 9;
    public const uint UnlockedDefault = 10;
    public const uint UnlockedBold = 11;
    public const uint UnlockedHeader = 12;
    public const uint RawHeader = 13;
    public const uint RawBody = 14;

    /// <summary>
    /// ValidationReport D20:D25 conditional yellow marker. Legacy Step1 BAS
    /// 3593/3625/3658/3691/3728/3799; ISM 5549/5582/5616/5650/5688/5762.
    /// </summary>
    public const uint ValidationWarning = 15;

    /// <summary>
    /// Validation field-info TB header: Calibri 12 bold + FFF0F000. Legacy
    /// Step1 BAS 3834-3837; ISM 5798-5801. Kept distinct from the GL header.
    /// </summary>
    public const uint ValidationTbHeader = 16;

    /// <summary>
    /// Validation field-info GL header: Calibri 12 bold + FFE6E600. Legacy
    /// Step1 BAS 3894-3897; ISM 5878-5881. Kept distinct from the TB header.
    /// </summary>
    public const uint ValidationGlHeader = 17;

    /// <summary>
    /// Calibri 12 bold red conditional message. Legacy Step5 BAS 9033-9036;
    /// ISM 10412-10415. This named palette entry prevents magic style indices.
    /// </summary>
    public const uint LegacyConditionalMessage = 18;

    /// <summary>
    /// Calibri 12 bold white on FF0066FF table header. Legacy Step5 BAS
    /// 9099-9104; ISM 10478-10483.
    /// </summary>
    public const uint LegacyBlueTableHeader = 19;

    /// <summary>
    /// Calibri 16 bold red on FF0066FF version banner. Legacy Step5 BAS
    /// 9449-9453; ISM 10831-10835.
    /// </summary>
    public const uint LegacyVersionBanner = 20;

    // Stage 9 physical-oracle family for IDEA ExportDatabase pages. Keep this
    // append-only: the long-standing 0-20 indices are consumed by template
    // overlays and the Stage 8 script-owned appearance oracle.
    public const uint ExportDatabaseGeneral = 21;
    public const uint ExportDatabaseText = 22;
    public const uint ExportDatabaseDate = 23;
    public const uint ExportDatabaseTime = 24;

    private const uint AmountFormatId = 164;
    private const int MaximumDecimalPlaces = 28;
    private const uint ExportDatabaseNumberFormatFirstId = 165;
    private const uint ExportDatabaseNumberStyleFirst = 25;
    private const uint ExportDatabaseAmountStyle =
        ExportDatabaseNumberStyleFirst + MaximumDecimalPlaces + 1;

    // Font indices; source ranges are documented on the cellXf constants above.
    private const uint FontRegular = 0;
    private const uint FontTitle = 1;
    private const uint FontWhiteBold = 2;
    private const uint FontDarkBold = 3;
    private const uint FontCalibri12Bold = 4;
    private const uint FontCalibri12BoldRed = 5;
    private const uint FontCalibri12BoldWhite = 6;
    private const uint FontCalibri16BoldRed = 7;
    private const uint FontArial10 = 8;

    // Fill indices 0(None) and 1(Gray125) are reserved by Excel. The named
    // legacy colors retain their distinct source values rather than normalizing.
    private const uint FillNone = 0;
    private const uint FillNavy = 2;
    private const uint FillPaleBlue = 3;
    private const uint FillPaleYellow = 4;
    private const uint FillLegacyBlue = 5;
    private const uint FillValidationYellow = 6;
    private const uint FillTbYellow = 7;
    private const uint FillGlYellow = 8;

    private const uint BorderNone = 0;
    private const uint BorderGrid = 1;
    private const string JetFontName = "微軟正黑體";
    private const string LegacyFontName = "Calibri";

    public static Stylesheet Build()
    {
        var numberFormats = new NumberingFormats(
            new NumberingFormat { NumberFormatId = AmountFormatId, FormatCode = "#,##0.0000;[Red]-#,##0.0000" });
        for (var decimalPlaces = 0;
             decimalPlaces <= MaximumDecimalPlaces;
             decimalPlaces++)
        {
            numberFormats.Append(new NumberingFormat
            {
                NumberFormatId = ExportDatabaseNumberFormatFirstId + checked((uint)decimalPlaces),
                FormatCode = "0" + (decimalPlaces == 0
                    ? string.Empty
                    : "." + new string('0', decimalPlaces))
            });
        }
        numberFormats.Count = checked((uint)numberFormats.ChildElements.Count);

        var fonts = new Fonts(
            Font(JetFontName, 11, bold: false, color: null),
            Font(JetFontName, 14, bold: true, color: "FF1F4E78"),
            Font(JetFontName, 11, bold: true, color: "FFFFFFFF"),
            Font(JetFontName, 11, bold: true, color: "FF1F1F1F"),
            Font(LegacyFontName, 12, bold: true, color: null),
            Font(LegacyFontName, 12, bold: true, color: "FFFF0000"),
            Font(LegacyFontName, 12, bold: true, color: "FFFFFFFF"),
            Font(LegacyFontName, 16, bold: true, color: "FFFF0000"),
            Font("Arial", 10, bold: false, color: null))
        { Count = 9 };

        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            SolidFill("FF1F4E78"),
            SolidFill("FFD9EAF7"),
            SolidFill("FFFFF2CC"),
            SolidFill("FF0066FF"),
            SolidFill("FFFFFF00"),
            SolidFill("FFF0F000"),
            SolidFill("FFE6E600"))
        { Count = 9 };

        var emptyBorder = new Border(
            new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder());
        var gridBorder = new Border(
            Thin<LeftBorder>(), Thin<RightBorder>(), Thin<TopBorder>(), Thin<BottomBorder>(), new DiagonalBorder());
        var borders = new Borders(emptyBorder, gridBorder) { Count = 2 };

        var styleXfs = new CellStyleFormats(
            new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 })
        { Count = 1 };

        var cellXfs = new CellFormats(
            Format(FontRegular, FillNone, BorderNone, 0, wrap: false, unlocked: false),
            Format(FontTitle, FillNone, BorderNone, 0, wrap: true, unlocked: false),
            Format(FontWhiteBold, FillNavy, BorderGrid, 0, wrap: true, unlocked: false),
            Format(FontDarkBold, FillPaleBlue, BorderGrid, 0, wrap: true, unlocked: false),
            Format(FontRegular, FillNone, BorderGrid, 0, wrap: true, unlocked: false),
            Format(FontRegular, FillNone, BorderGrid, AmountFormatId, wrap: false, unlocked: false),
            Format(FontRegular, FillNone, BorderGrid, 0, wrap: false, unlocked: false),
            Format(FontRegular, FillPaleYellow, BorderGrid, 0, wrap: true, unlocked: true),
            Format(FontRegular, FillNone, BorderNone, 0, wrap: true, unlocked: false),
            Format(FontRegular, FillPaleYellow, BorderGrid, 0, wrap: false, unlocked: false),
            Format(FontRegular, FillNone, BorderNone, 0, wrap: false, unlocked: true),
            Format(FontDarkBold, FillNone, BorderNone, 0, wrap: true, unlocked: true),
            Format(FontDarkBold, FillPaleBlue, BorderGrid, 0, wrap: true, unlocked: true),
            Format(FontDarkBold, FillPaleBlue, BorderGrid, 0, wrap: false, unlocked: false),
            Format(FontRegular, FillNone, BorderGrid, 0, wrap: false, unlocked: false),
            Format(FontRegular, FillValidationYellow, BorderGrid, 0, wrap: true, unlocked: false),
            Format(FontCalibri12Bold, FillTbYellow, BorderGrid, 0, wrap: true, unlocked: false),
            Format(FontCalibri12Bold, FillGlYellow, BorderGrid, 0, wrap: true, unlocked: false),
            Format(FontCalibri12BoldRed, FillNone, BorderNone, 0, wrap: true, unlocked: false),
            Format(FontCalibri12BoldWhite, FillLegacyBlue, BorderGrid, 0, wrap: true, unlocked: false),
            Format(FontCalibri16BoldRed, FillLegacyBlue, BorderNone, 0, wrap: true, unlocked: false),
            ExportDatabaseFormat(0),
            ExportDatabaseFormat(49),
            ExportDatabaseFormat(14),
            ExportDatabaseFormat(21));
        for (var decimalPlaces = 0;
             decimalPlaces <= MaximumDecimalPlaces;
             decimalPlaces++)
        {
            cellXfs.Append(ExportDatabaseFormat(
                ExportDatabaseNumberFormatFirstId + checked((uint)decimalPlaces)));
        }
        cellXfs.Append(ExportDatabaseFormat(AmountFormatId));
        cellXfs.Count = checked((uint)cellXfs.ChildElements.Count);

        return new Stylesheet(numberFormats, fonts, fills, borders, styleXfs, cellXfs);
    }

    internal static uint ExportDatabaseNumber(int decimalPlaces)
    {
        if (decimalPlaces is < 0 or > MaximumDecimalPlaces)
        {
            throw new ArgumentOutOfRangeException(
                nameof(decimalPlaces),
                decimalPlaces,
                $"ExportDatabase decimal scale must be in 0..{MaximumDecimalPlaces}.");
        }

        return ExportDatabaseNumberStyleFirst + checked((uint)decimalPlaces);
    }

    internal static uint ExportDatabaseAmount => ExportDatabaseAmountStyle;

    private static SpreadsheetFont Font(string name, double size, bool bold, string? color)
    {
        var font = new SpreadsheetFont();
        if (bold)
        {
            font.Append(new Bold());
        }

        font.Append(new FontSize { Val = size });
        if (color is not null)
        {
            font.Append(new SpreadsheetColor { Rgb = color });
        }

        // CT_Font schema order places color before name. Appending color after FontName
        // makes Excel-readable files that nevertheless fail the OpenXML schema validator.
        font.Append(new FontName { Val = name });

        return font;
    }

    private static Fill SolidFill(string argb) => new(
        new PatternFill(new ForegroundColor { Rgb = argb }) { PatternType = PatternValues.Solid });

    private static T Thin<T>() where T : BorderPropertiesType, new() => new()
    {
        Style = BorderStyleValues.Thin,
        Color = new SpreadsheetColor { Rgb = "FFB7B7B7" }
    };

    private static CellFormat Format(
        uint font,
        uint fill,
        uint border,
        uint numberFormat,
        bool wrap,
        bool unlocked)
    {
        return new CellFormat
        {
            FontId = font,
            FillId = fill,
            BorderId = border,
            NumberFormatId = numberFormat,
            FormatId = 0,
            ApplyFont = true,
            ApplyFill = fill != 0,
            ApplyBorder = border != 0,
            ApplyNumberFormat = numberFormat != 0,
            ApplyAlignment = true,
            Alignment = new Alignment
            {
                Vertical = VerticalAlignmentValues.Top,
                WrapText = wrap,
                Indent = 0U,
                ShrinkToFit = false
            },
            ApplyProtection = unlocked,
            Protection = unlocked ? new Protection { Locked = false } : null
        };
    }

    private static CellFormat ExportDatabaseFormat(uint numberFormatId) => new()
    {
        FontId = FontArial10,
        FillId = FillNone,
        BorderId = BorderNone,
        NumberFormatId = numberFormatId,
        FormatId = 0,
        ApplyFont = true,
        ApplyFill = false,
        ApplyBorder = false,
        ApplyNumberFormat = numberFormatId != 0,
        ApplyAlignment = true,
        Alignment = new Alignment
        {
            Vertical = VerticalAlignmentValues.Bottom,
            WrapText = false,
            Indent = 0U,
            ShrinkToFit = false
        },
        ApplyProtection = false
    };
}
