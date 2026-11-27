using DocumentFormat.OpenXml.Spreadsheet;
using SpreadsheetFont = DocumentFormat.OpenXml.Spreadsheet.Font;

namespace JET.Infrastructure;

/// <summary>
/// 匯出底稿用的樣式表，也就是 WorkbookStylesPart 的 Stylesheet，以及各樣式在 cellXfs 裡的索引常數。
///
/// 儲存格用 StyleIndex 指向 cellXfs 裡從 0 起算的位置。這裡替每個位置取名，例如 <see cref="Default"/>、<see cref="Bold"/>，
/// 寫出底稿的程式用名稱取用，就不會指錯位置。
///
/// 子元素的順序不能變：ISO/IEC 29500 的 CT_Stylesheet 規定 styleSheet 的子元素依序是
/// numFmts、fonts、fills、borders、cellStyleXfs、cellXfs，OpenXML SDK 的 Stylesheet 類別會檢查這個順序。
/// fills 另有 Excel 的慣例：索引 0 必須是 PatternType.None，索引 1 必須是 Gray125，自訂的底色從索引 2 開始，
/// 否則 Excel 會判定檔案損壞並嘗試修復。這份樣式表只放兩張封面、step5 與各資料工作表會用到的樣式，
/// 不預先加入用不到的；需要新樣式時在這裡增加，並更新索引常數。
/// </summary>
internal static class WorkingPaperStyles
{
    // ---- cellXfs 索引(對應下方 BuildCellFormats 的順序)----

    /// <summary>預設:微軟正黑體、靠上對齊。資料表內文用。</summary>
    public const uint Default = 0;

    /// <summary>粗體、靠左靠上。封面 A1/A2、各表「說明：」標籤。</summary>
    public const uint Bold = 1;

    /// <summary>粗體、自動換行、靠上。含換行的整段固定說明文字用，例如「JE WorkingPaper說明」的 B1。</summary>
    public const uint BoldWrap = 2;

    /// <summary>一般、靠左靠上。封面固定說明段(A5/A6)。</summary>
    public const uint Plain = 3;

    /// <summary>黃底橫幅:12 級、黃色填滿、自動換行、靠左靠上。step5 A1。</summary>
    public const uint YellowBanner = 4;

    /// <summary>
    /// 審計員填寫的空白格：未鎖定、不加底色。受保護的工作表只鎖程式寫入的資料區，
    /// 這種格子在資料區裡也維持可填。
    /// </summary>
    public const uint Editable = 5;

    /// <summary>金額：千分位且固定四位小數，對齊 JET 全域顯示精度。</summary>
    public const uint Amount = 6;

    /// <summary>舊版 JET 的日期型態欄位：存成 Excel 的日期序號，固定用 yyyy-mm-dd 顯示。</summary>
    public const uint Date = 7;

    /// <summary>舊版 JET 的時間型態欄位：存成一天的小數，固定用 hh:mm:ss 顯示。</summary>
    public const uint Time = 8;

    /// <summary>舊版 JET 的數值型態欄位可以保存的小數位數上限，和 System.Decimal 相同。</summary>
    public const int MaximumDecimalPlaces = 28;

    // ---- fonts 索引 ----
    private const uint FontRegular = 0;
    private const uint FontBold = 1;
    private const uint FontBanner = 2;

    // ---- fills 索引(0/1 為 Excel 保留;黃色自 2 起)----
    private const uint FillYellow = 2;

    private const uint AmountNumberFormatId = 164;
    private const uint DateNumberFormatId = 165;
    private const uint TimeNumberFormatId = 166;
    private const uint NumberFormatFirstId = 167;
    private const uint GroupedNumberFormatFirstId =
        NumberFormatFirstId + MaximumDecimalPlaces + 1;
    private const uint NumberStyleFirst = 9;
    private const uint GroupedNumberStyleFirst =
        NumberStyleFirst + MaximumDecimalPlaces + 1;

    // step4-1 用的樣式，外觀照舊版 JET 用 ExportDatabase 匯出的明細頁。0 到 66 號樣式還有底稿其他頁面在用，
    // 所以這些樣式只能加在最後面，不能改既有的索引。
    public const uint HighRiskVoucherDetailText =
        GroupedNumberStyleFirst + MaximumDecimalPlaces + 1;
    public const uint HighRiskVoucherDetailDate = HighRiskVoucherDetailText + 1;
    public const uint HighRiskVoucherDetailTime = HighRiskVoucherDetailDate + 1;
    private const uint HighRiskVoucherDetailNumberStyleFirst = HighRiskVoucherDetailTime + 1;
    private const uint HighRiskVoucherDetailGroupedNumberStyleFirst =
        HighRiskVoucherDetailNumberStyleFirst + MaximumDecimalPlaces + 1;

    /// <summary>一般字、靠左靠上並自動換行。欄寬有上限的長文字用，例如欄位資訊頁的欄位名稱與 step3 的條件。</summary>
    public const uint Wrap =
        HighRiskVoucherDetailGroupedNumberStyleFirst + MaximumDecimalPlaces + 1;

    private const uint FontHighRiskVoucherDetail = 3;

    private const string FontName = "微軟正黑體";
    private const double BannerFontSize = 12d;

    /// <summary>底稿一般內文的字級；step4-1 用 <see cref="HighRiskVoucherDetailFontSize"/>。</summary>
    internal const double RegularFontSize = 11d;
    internal const double HighRiskVoucherDetailFontSize = 12d;
    private const string YellowHex = "FFFFFF00"; // ARGB:不透明黃(#FFFF00)

    /// <summary>組出整份 Stylesheet。styles.xml 很小，直接在記憶體裡建好整份，不需要逐段寫出。</summary>
    public static Stylesheet Build()
    {
        // 子元素順序固定:numFmts、fonts、fills、borders、cellStyleXfs、cellXfs(見類別註解)
        return new Stylesheet(
            BuildNumberingFormats(),
            BuildFonts(),
            BuildFills(),
            BuildBorders(),
            BuildCellStyleFormats(),
            BuildCellFormats());
    }

    /// <summary>舊版 JET 一般的數值型態欄位：依欄位保存的小數位數固定顯示，不加千分位。</summary>
    public static uint Number(int decimalPlaces)
    {
        ValidateDecimalPlaces(decimalPlaces);
        return NumberStyleFirst + checked((uint)decimalPlaces);
    }

    /// <summary>舊版 JET 帶正負號的金額：依欄位保存的小數位數固定顯示，並加千分位。</summary>
    public static uint GroupedNumber(int decimalPlaces)
    {
        ValidateDecimalPlaces(decimalPlaces);
        return GroupedNumberStyleFirst + checked((uint)decimalPlaces);
    }

    /// <summary>step4-1 一般的數值型態欄位：小數位數照已確定的欄位定義，字型與對齊用 step4-1 明細頁的樣式。</summary>
    public static uint HighRiskVoucherDetailNumber(int decimalPlaces)
    {
        ValidateDecimalPlaces(decimalPlaces);
        return HighRiskVoucherDetailNumberStyleFirst + checked((uint)decimalPlaces);
    }

    /// <summary>step4-1 帶正負號的金額：小數位數照已確定的欄位定義，並加千分位。</summary>
    public static uint HighRiskVoucherDetailGroupedNumber(int decimalPlaces)
    {
        ValidateDecimalPlaces(decimalPlaces);
        return HighRiskVoucherDetailGroupedNumberStyleFirst + checked((uint)decimalPlaces);
    }

    private static NumberingFormats BuildNumberingFormats()
    {
        var formats = new NumberingFormats(
            new NumberingFormat
            {
                NumberFormatId = AmountNumberFormatId,
                FormatCode = "#,##0.0000"
            },
            new NumberingFormat
            {
                NumberFormatId = DateNumberFormatId,
                FormatCode = "yyyy-mm-dd"
            },
            new NumberingFormat
            {
                NumberFormatId = TimeNumberFormatId,
                FormatCode = "hh:mm:ss"
            });

        for (var decimalPlaces = 0; decimalPlaces <= MaximumDecimalPlaces; decimalPlaces++)
        {
            formats.Append(new NumberingFormat
            {
                NumberFormatId = NumberFormatFirstId + checked((uint)decimalPlaces),
                FormatCode = NumberFormatCode(decimalPlaces, grouped: false)
            });
        }

        for (var decimalPlaces = 0; decimalPlaces <= MaximumDecimalPlaces; decimalPlaces++)
        {
            formats.Append(new NumberingFormat
            {
                NumberFormatId = GroupedNumberFormatFirstId + checked((uint)decimalPlaces),
                FormatCode = NumberFormatCode(decimalPlaces, grouped: true)
            });
        }

        formats.Count = checked((uint)formats.ChildElements.Count);
        return formats;
    }

    private static Fonts BuildFonts()
    {
        // 索引必須與 FontRegular/FontBold/FontBanner 一致
        var regular = new SpreadsheetFont(new FontSize { Val = RegularFontSize }, new FontName { Val = FontName });
        var bold = new SpreadsheetFont(new Bold(), new FontSize { Val = RegularFontSize }, new FontName { Val = FontName });
        var banner = new SpreadsheetFont(new FontSize { Val = BannerFontSize }, new FontName { Val = FontName });
        var highRiskVoucherDetail = new SpreadsheetFont(
            new FontSize { Val = HighRiskVoucherDetailFontSize },
            new FontName { Val = FontName });
        return new Fonts(regular, bold, banner, highRiskVoucherDetail) { Count = 4 };
    }

    private static Fills BuildFills()
    {
        // 索引 0 = None、索引 1 = Gray125(Excel 保留,不可省);黃色為索引 2
        var none = new Fill(new PatternFill { PatternType = PatternValues.None });
        var gray = new Fill(new PatternFill { PatternType = PatternValues.Gray125 });
        var yellow = new Fill(new PatternFill(new ForegroundColor { Rgb = YellowHex })
        {
            PatternType = PatternValues.Solid
        });
        return new Fills(none, gray, yellow) { Count = 3 };
    }

    private static Borders BuildBorders()
    {
        // 單一空白邊框(索引 0):cellXfs 的 BorderId 都指 0,這份樣式表的樣式都沒有框線。
        var border = new Border(
            new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder());
        return new Borders(border) { Count = 1 };
    }

    private static CellStyleFormats BuildCellStyleFormats()
    {
        // 至少一筆主格式(索引 0),cellXfs 的 xfId 指它
        var master = new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 };
        return new CellStyleFormats(master) { Count = 1 };
    }

    private static CellFormats BuildCellFormats()
    {
        // 順序對應頂部索引常數;ApplyFont/ApplyFill/ApplyAlignment 明示讓 Excel 套用對應記錄
        var defaultXf = TopAligned(FontRegular, fillId: 0, horizontalLeft: false, wrap: false);
        var boldXf = TopAligned(FontBold, fillId: 0, horizontalLeft: true, wrap: false);
        var boldWrapXf = TopAligned(FontBold, fillId: 0, horizontalLeft: false, wrap: true);
        var plainXf = TopAligned(FontRegular, fillId: 0, horizontalLeft: true, wrap: false);
        var bannerXf = TopAligned(FontBanner, fillId: FillYellow, horizontalLeft: true, wrap: true);
        var editableXf = TopAligned(
            FontRegular, fillId: 0, horizontalLeft: true, wrap: false, unlocked: true);
        var amountXf = TopAligned(
            FontRegular, fillId: 0, horizontalLeft: false, wrap: false, numberFormatId: AmountNumberFormatId);
        var dateXf = TopAligned(
            FontRegular, fillId: 0, horizontalLeft: false, wrap: false, numberFormatId: DateNumberFormatId);
        var timeXf = TopAligned(
            FontRegular, fillId: 0, horizontalLeft: false, wrap: false, numberFormatId: TimeNumberFormatId);

        var formats = new CellFormats(
            defaultXf,
            boldXf,
            boldWrapXf,
            plainXf,
            bannerXf,
            editableXf,
            amountXf,
            dateXf,
            timeXf);

        for (var decimalPlaces = 0; decimalPlaces <= MaximumDecimalPlaces; decimalPlaces++)
        {
            formats.Append(TopAligned(
                FontRegular,
                fillId: 0,
                horizontalLeft: false,
                wrap: false,
                numberFormatId: NumberFormatFirstId + checked((uint)decimalPlaces)));
        }

        for (var decimalPlaces = 0; decimalPlaces <= MaximumDecimalPlaces; decimalPlaces++)
        {
            formats.Append(TopAligned(
                FontRegular,
                fillId: 0,
                horizontalLeft: false,
                wrap: false,
                numberFormatId: GroupedNumberFormatFirstId + checked((uint)decimalPlaces)));
        }

        formats.Append(HighRiskVoucherDetailAligned(49));
        formats.Append(HighRiskVoucherDetailAligned(DateNumberFormatId));
        formats.Append(HighRiskVoucherDetailAligned(TimeNumberFormatId));

        for (var decimalPlaces = 0; decimalPlaces <= MaximumDecimalPlaces; decimalPlaces++)
        {
            formats.Append(HighRiskVoucherDetailAligned(
                NumberFormatFirstId + checked((uint)decimalPlaces)));
        }

        for (var decimalPlaces = 0; decimalPlaces <= MaximumDecimalPlaces; decimalPlaces++)
        {
            formats.Append(HighRiskVoucherDetailAligned(
                GroupedNumberFormatFirstId + checked((uint)decimalPlaces)));
        }

        formats.Append(TopAligned(FontRegular, fillId: 0, horizontalLeft: true, wrap: true));

        formats.Count = checked((uint)formats.ChildElements.Count);
        return formats;
    }

    private static string NumberFormatCode(int decimalPlaces, bool grouped) =>
        (grouped ? "#,##0" : "0")
        + (decimalPlaces == 0 ? string.Empty : "." + new string('0', decimalPlaces));

    private static void ValidateDecimalPlaces(int decimalPlaces)
    {
        if (decimalPlaces is < 0 or > MaximumDecimalPlaces)
        {
            throw new ArgumentOutOfRangeException(
                nameof(decimalPlaces),
                decimalPlaces,
                $"Legacy Number decimal scale 必須介於 0 與 {MaximumDecimalPlaces}。");
        }
    }

    /// <summary>共用 cellXf 工廠:靠上對齊,可選靠左、自動換行、指定填色。</summary>
    private static CellFormat TopAligned(
        uint fontId,
        uint fillId,
        bool horizontalLeft,
        bool wrap,
        bool unlocked = false,
        uint numberFormatId = 0)
    {
        var alignment = new Alignment
        {
            Vertical = VerticalAlignmentValues.Top,
            WrapText = wrap,
            Indent = 0U,
            ShrinkToFit = false
        };
        if (horizontalLeft)
        {
            alignment.Horizontal = HorizontalAlignmentValues.Left;
        }

        return new CellFormat
        {
            NumberFormatId = numberFormatId,
            FontId = fontId,
            FillId = fillId,
            BorderId = 0,
            FormatId = 0,
            ApplyFont = true,
            ApplyFill = fillId != 0, // index 0 = 無填色,不需套用
            ApplyNumberFormat = numberFormatId != 0,
            ApplyAlignment = true,
            Alignment = alignment,
            ApplyProtection = unlocked,
            Protection = unlocked ? new Protection { Locked = false } : null
        };
    }

    private static CellFormat HighRiskVoucherDetailAligned(uint numberFormatId)
    {
        return new CellFormat
        {
            NumberFormatId = numberFormatId,
            FontId = FontHighRiskVoucherDetail,
            FillId = 0,
            BorderId = 0,
            FormatId = 0,
            ApplyFont = true,
            ApplyFill = false,
            ApplyNumberFormat = numberFormatId != 0,
            ApplyAlignment = true,
            Alignment = new Alignment
            {
                Vertical = VerticalAlignmentValues.Center,
                WrapText = false,
                Indent = 0U,
                ShrinkToFit = false
            }
        };
    }
}
