using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace JET.Infrastructure;

/// <summary>
/// OpenXML SDK 把 <see cref="WorkbookPart.Workbook"/> 與 <see cref="WorksheetPart.Worksheet"/> 標成可能是 null：
/// part 的內容是空的時候，讀取根節點會得到 null。JET 的範本與報告一定有這兩個根節點，缺少時代表檔案損壞。
/// 寫出報告的程式一律從這裡取根節點，缺少時丟出指出 part 位置的錯誤，不在各處加 <c>!</c>。
/// 讀使用者上傳檔案的程式把缺少的根節點當成沒有內容，用 <c>?.</c> 處理，不經過這裡。
/// </summary>
internal static class OpenXmlPartRoots
{
    internal static Workbook RequiredWorkbook(this WorkbookPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return part.Workbook
            ?? throw new InvalidDataException($"Workbook part '{part.Uri}' has no workbook element.");
    }

    internal static Worksheet RequiredWorksheet(this WorksheetPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return part.Worksheet
            ?? throw new InvalidDataException($"Worksheet part '{part.Uri}' has no worksheet element.");
    }
}
