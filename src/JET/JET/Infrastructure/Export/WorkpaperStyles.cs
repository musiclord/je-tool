using DocumentFormat.OpenXml.Spreadsheet;
using SpreadsheetFont = DocumentFormat.OpenXml.Spreadsheet.Font;

namespace JET.Infrastructure;

/// <summary>
/// 匯出底稿的最小樣式表(WorkbookStylesPart 的 Stylesheet)+ cellXfs 索引常數。
///
/// 為什麼用具名索引常數而非魔術數字:cellXfs 是 0-based 索引集合,cell 以 StyleIndex 指進來;
/// 用 <see cref="Default"/>/<see cref="Bold"/>… 命名讓 emitter 讀得懂、避免錯位(介面誤用困難)。
///
/// 子元素順序是硬約束:ISO/IEC 29500 的 CT_Stylesheet 規定 styleSheet 子序列為
/// numFmts → fonts → fills → borders → cellStyleXfs → cellXfs(strongly-typed Stylesheet 會驗證順序)。
/// fills 還有 Excel 慣例:索引 0 必為 PatternType.None、索引 1 必為 Gray125,自訂填色從索引 2 起,
/// 否則 Excel 會判檔損並嘗試修復。本表只放三表(封面 ×2 + step5)+ 後續資料表會用到的少量樣式,
/// 不提前堆砌(no over-engineering);Task 3-5 需要新樣式時在此擴充並更新索引常數。
/// </summary>
internal static class WorkpaperStyles
{
    // ---- cellXfs 索引(對應下方 BuildCellFormats 的順序)----

    /// <summary>預設:微軟正黑體、靠上對齊。資料表內文用。</summary>
    public const uint Default = 0;

    /// <summary>粗體、靠左靠上。封面 A1/A2、各表「說明：」標籤。</summary>
    public const uint Bold = 1;

    /// <summary>粗體 + 自動換行 + 靠上。含換行的整段 boilerplate(JE WorkingPaper說明 B1)。</summary>
    public const uint BoldWrap = 2;

    /// <summary>一般、靠左靠上。封面固定說明段(A5/A6)。</summary>
    public const uint Plain = 3;

    /// <summary>黃底橫幅:12 級、黃色填滿、自動換行、靠左靠上。step5 A1。</summary>
    public const uint YellowBanner = 4;

    /// <summary>人工覆核欄：淡黃底、未鎖定，配合受保護工作表延展至實際資料末列。</summary>
    public const uint Editable = 5;

    /// <summary>金額：千分位且固定四位小數，對齊 JET 全域顯示精度。</summary>
    public const uint Amount = 6;

    /// <summary>Legacy native date：以 Excel serial 儲存、固定 yyyy-mm-dd 顯示。</summary>
    public const uint Date = 7;

    /// <summary>Legacy native time：以一天的小數儲存、固定 hh:mm:ss 顯示。</summary>
    public const uint Time = 8;

    /// <summary>Legacy Number 支援的最大 persisted decimal scale（對齊 System.Decimal）。</summary>
    public const int MaximumDecimalPlaces = 28;

    // ---- fonts 索引 ----
    private const uint FontRegular = 0;
    private const uint FontBold = 1;
    private const uint FontBanner = 2;

    // ---- fills 索引(0/1 為 Excel 保留;黃色自 2 起)----
    private const uint FillYellow = 2;
    private const uint FillPaleYellow = 3;

    private const uint AmountNumberFormatId = 164;
    private const uint DateNumberFormatId = 165;
    private const uint TimeNumberFormatId = 166;
    private const uint NumberFormatFirstId = 167;
    private const uint GroupedNumberFormatFirstId =
        NumberFormatFirstId + MaximumDecimalPlaces + 1;
    private const uint NumberStyleFirst = 9;
    private const uint GroupedNumberStyleFirst =
        NumberStyleFirst + MaximumDecimalPlaces + 1;

    // step4-1 的實物 ExportDatabase 基底外觀。既有 0..66 樣式仍由其他
    // Working Paper 頁面共用，因此只能在尾端追加，不能改寫既有索引。
    public const uint Step41Text =
        GroupedNumberStyleFirst + MaximumDecimalPlaces + 1;
    public const uint Step41Date = Step41Text + 1;
    public const uint Step41Time = Step41Date + 1;
    private const uint Step41NumberStyleFirst = Step41Time + 1;
    private const uint Step41GroupedNumberStyleFirst =
        Step41NumberStyleFirst + MaximumDecimalPlaces + 1;

    /// <summary>人工填寫但不提示底色：未鎖定、無填色。</summary>
    public const uint EditableNoFill =
        Step41GroupedNumberStyleFirst + MaximumDecimalPlaces + 1;

    private const uint FontStep41 = 3;

    private const string FontName = "微軟正黑體";
    private const double BannerFontSize = 12d;
    private const string YellowHex = "FFFFFF00"; // ARGB:不透明黃(對齊樣本 bg#FFFF00)

    /// <summary>組出整份 Stylesheet。一次性建構(styles.xml 恆小,DOM 即可,不需串流)。</summary>
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

    /// <summary>Legacy 一般 Number：依 persisted scale 固定顯示小數位，不套千分位。</summary>
    public static uint Number(int decimalPlaces)
    {
        ValidateDecimalPlaces(decimalPlaces);
        return NumberStyleFirst + checked((uint)decimalPlaces);
    }

    /// <summary>Legacy signed amount：依 persisted scale 固定顯示小數位，並套千分位。</summary>
    public static uint GroupedNumber(int decimalPlaces)
    {
        ValidateDecimalPlaces(decimalPlaces);
        return GroupedNumberStyleFirst + checked((uint)decimalPlaces);
    }

    /// <summary>step4-1 一般 Number：保留 finalized scale，套用實物基底字型與對齊。</summary>
    public static uint Step41Number(int decimalPlaces)
    {
        ValidateDecimalPlaces(decimalPlaces);
        return Step41NumberStyleFirst + checked((uint)decimalPlaces);
    }

    /// <summary>step4-1 signed amount：保留 finalized grouping/scale 語意。</summary>
    public static uint Step41GroupedNumber(int decimalPlaces)
    {
        ValidateDecimalPlaces(decimalPlaces);
        return Step41GroupedNumberStyleFirst + checked((uint)decimalPlaces);
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
        var regular = new SpreadsheetFont(new FontSize { Val = 11d }, new FontName { Val = FontName });
        var bold = new SpreadsheetFont(new Bold(), new FontSize { Val = 11d }, new FontName { Val = FontName });
        var banner = new SpreadsheetFont(new FontSize { Val = BannerFontSize }, new FontName { Val = FontName });
        var step41 = new SpreadsheetFont(
            new FontSize { Val = 12d },
            new FontName { Val = FontName });
        return new Fonts(regular, bold, banner, step41) { Count = 4 };
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
        var paleYellow = new Fill(new PatternFill(new ForegroundColor { Rgb = "FFFFFFCC" })
        {
            PatternType = PatternValues.Solid
        });
        return new Fills(none, gray, yellow, paleYellow) { Count = 4 };
    }

    private static Borders BuildBorders()
    {
        // 單一空白邊框(索引 0):cellXfs 的 BorderId 都指 0。本 task 三表無框線。
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
            FontRegular, fillId: FillPaleYellow, horizontalLeft: true, wrap: false, unlocked: true);
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

        formats.Append(Step41Aligned(49));
        formats.Append(Step41Aligned(DateNumberFormatId));
        formats.Append(Step41Aligned(TimeNumberFormatId));

        for (var decimalPlaces = 0; decimalPlaces <= MaximumDecimalPlaces; decimalPlaces++)
        {
            formats.Append(Step41Aligned(
                NumberFormatFirstId + checked((uint)decimalPlaces)));
        }

        for (var decimalPlaces = 0; decimalPlaces <= MaximumDecimalPlaces; decimalPlaces++)
        {
            formats.Append(Step41Aligned(
                GroupedNumberFormatFirstId + checked((uint)decimalPlaces)));
        }

        formats.Append(TopAligned(
            FontRegular,
            fillId: 0,
            horizontalLeft: true,
            wrap: false,
            unlocked: true));

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

    /// <summary>共用 cellXf 工廠:靠上對齊(樣本全表 vertical=top),可選靠左、自動換行、指定填色。</summary>
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

    private static CellFormat Step41Aligned(uint numberFormatId)
    {
        return new CellFormat
        {
            NumberFormatId = numberFormatId,
            FontId = FontStep41,
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
