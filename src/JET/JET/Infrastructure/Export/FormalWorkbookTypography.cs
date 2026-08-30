using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SpreadsheetFont = DocumentFormat.OpenXml.Spreadsheet.Font;

namespace JET.Infrastructure;

/// <summary>
/// 六份正式輸出共用的字型收尾。固定範本與 legacy script 仍可保留字級、粗體、
/// 顏色等個別語意，但最終活頁簿不得再混用不同字族或 theme font。
/// </summary>
internal static class FormalWorkbookTypography
{
    internal const string CanonicalFontName = "微軟正黑體";

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
            var runFonts = worksheetPart.Worksheet.Descendants<RunFont>().ToArray();
            if (runFonts.Length == 0)
            {
                continue;
            }

            foreach (var runFont in runFonts)
            {
                runFont.Val = CanonicalFontName;
            }
            worksheetPart.Worksheet.Save();
        }
    }
}
