using DocumentFormat.OpenXml.Spreadsheet;
using SpreadsheetFont = DocumentFormat.OpenXml.Spreadsheet.Font;
using SpreadsheetColor = DocumentFormat.OpenXml.Spreadsheet.Color;

namespace JET.Infrastructure;

/// <summary>照舊版 JET 版型寫出的六份報告共用的樣式表。樣式全部由這裡的程式建立，不從別的活頁簿複製中繼資料、圖片或外部關聯。</summary>
internal static class LegacyReportStyles
{
    // cellXfs 0 到 14 是 JET 報告的基本樣式，15 號以後是照舊版 JET 外觀的樣式；新的樣式只能加在最後，
    // 其他寫出程式用到的索引才不會變。
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
    /// 資料驗證報告 D20:D25 依條件標示的黃色底，外觀照舊版 JET。
    /// </summary>
    public const uint ValidationWarning = 15;

    /// <summary>
    /// 資料驗證報告欄位資訊頁的 TB 表頭，外觀照舊版 JET：Calibri 12 點粗體，底色 FFF0F000。
    /// 底色和 GL 表頭不同，兩者分開定義。
    /// </summary>
    public const uint ValidationTbHeader = 16;

    /// <summary>
    /// 資料驗證報告欄位資訊頁的 GL 表頭，外觀照舊版 JET：Calibri 12 點粗體，底色 FFE6E600。
    /// 底色和 TB 表頭不同，兩者分開定義。
    /// </summary>
    public const uint ValidationGlHeader = 17;

    /// <summary>
    /// 依條件顯示的訊息，外觀照舊版 JET：Calibri 12 點粗體紅字。
    /// 取了名稱，寫出程式就不必直接寫樣式的數字索引。
    /// </summary>
    public const uint LegacyConditionalMessage = 18;

    /// <summary>
    /// 表格表頭，外觀照舊版 JET：Calibri 12 點粗體白字，底色 FF0066FF。
    /// </summary>
    public const uint LegacyBlueTableHeader = 19;

    /// <summary>
    /// 版本字樣那一列，外觀照舊版 JET：Calibri 16 點粗體紅字，底色 FF0066FF。
    /// </summary>
    public const uint LegacyVersionBanner = 20;

    // ExportDatabase 外觀的明細頁使用的樣式。只能往後追加：0 到 20 號索引
    // 已被範本套版使用，改動會讓輸出外觀跑掉。
    public const uint ExportDatabaseGeneral = 21;
    public const uint ExportDatabaseText = 22;
    public const uint ExportDatabaseDate = 23;
    public const uint ExportDatabaseTime = 24;

    /// <summary>明細頁的字級；最後由 <see cref="FormalWorkbookTypography"/> 統一成微軟正黑體。</summary>
    internal const double ExportDatabaseFontSize = 10D;

    /// <summary>V_Report、R1 到 R7、#Criteria Select 與 Source_Quality 等明細頁共用的列高。</summary>
    internal static double ExportDatabaseRowHeight =>
        FormalWorkbookTypography.RowHeight(ExportDatabaseFontSize);

    private const uint AmountFormatId = 164;
    private const int MaximumDecimalPlaces = 28;
    private const uint ExportDatabaseNumberFormatFirstId = 165;
    private const uint ExportDatabaseNumberStyleFirst = 25;
    private const uint ExportDatabaseAmountStyle =
        ExportDatabaseNumberStyleFirst + MaximumDecimalPlaces + 1;

    // 字型的索引。
    private const uint FontRegular = 0;
    private const uint FontTitle = 1;
    private const uint FontWhiteBold = 2;
    private const uint FontDarkBold = 3;
    private const uint FontCalibri12Bold = 4;
    private const uint FontCalibri12BoldRed = 5;
    private const uint FontCalibri12BoldWhite = 6;
    private const uint FontCalibri16BoldRed = 7;
    private const uint FontArial10 = 8;

    // 底色索引 0（None）與 1（Gray125）是 Excel 保留的。取自舊版 JET 的幾個顏色
    // 各自保留原本的色碼，不合併成同一個顏色。
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
            Font("Arial", ExportDatabaseFontSize, bold: false, color: null))
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

        // OpenXML 的 CT_Font 規定 color 要排在 name 前面。color 接在 FontName 後面的檔案 Excel 打得開，
        // 但通不過 OpenXML 的結構驗證。
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
