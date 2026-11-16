using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SpreadsheetFont = DocumentFormat.OpenXml.Spreadsheet.Font;

namespace JET.Infrastructure;

/// <summary>
/// 六份正式輸出寫完後共用的字型整理：把活頁簿裡每一個字型的名稱改成微軟正黑體，並拿掉佈景主題字型的設定。
/// 固定範本與照舊版 JET 做出來的樣式，字級、粗體與顏色都照原樣，只有字型名稱統一。
/// </summary>
internal static class FormalWorkbookTypography
{
    internal const string CanonicalFontName = "微軟正黑體";

    private const double RowHeightPerFontPoint = 1.5D;

    /// <summary>
    /// JET 自己寫的工作表列高一律是字級的 1.5 倍：10 點 15、11 點 16.5、12 點 18；其他字級也照它算。
    /// 微軟正黑體比舊流程的 Arial 高，沿用 12.5 點會切到字；Excel 的標準列高又隨螢幕縮放改變，
    /// 1.5 倍在一般縮放與高縮放下都不低於字的自然高度。多行文字依行數加高。
    /// </summary>
    internal static double RowHeight(double fontSize, int lines = 1)
    {
        if (!double.IsFinite(fontSize) || fontSize <= 0D)
        {
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(lines, 1);

        return fontSize * RowHeightPerFontPoint * lines;
    }

    internal static void Normalize(SpreadsheetDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("Formal workbook has no workbook part.");
        var stylesPart = workbookPart.WorkbookStylesPart
            ?? throw new InvalidDataException("Formal workbook has no styles part.");
        var stylesheet = stylesPart.Stylesheet
            ?? throw new InvalidDataException("Formal workbook has no stylesheet.");

        foreach (var font in stylesheet.Descendants<SpreadsheetFont>())
        {
            font.FontName = new FontName { Val = CanonicalFontName };
            font.FontScheme = null;
        }
        stylesheet.Save();

        if (workbookPart.SharedStringTablePart?.SharedStringTable is { } sharedStrings)
        {
            var runFonts = sharedStrings.Descendants<RunFont>().ToArray();
            foreach (var runFont in runFonts)
            {
                runFont.Val = CanonicalFontName;
            }
            if (runFonts.Length > 0)
            {
                sharedStrings.Save();
            }
        }

        foreach (var worksheetPart in workbookPart.WorksheetParts)
        {
            var runFonts = worksheetPart.RequiredWorksheet().Descendants<RunFont>().ToArray();
            if (runFonts.Length == 0)
            {
                continue;
            }

            foreach (var runFont in runFonts)
            {
                runFont.Val = CanonicalFontName;
            }
            ExcelText.SaveWorksheet(worksheetPart);
        }
    }
}
