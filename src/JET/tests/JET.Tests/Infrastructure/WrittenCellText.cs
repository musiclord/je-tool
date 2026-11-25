using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 讀出文字格寫進檔案的文字，不還原 _xHHHH_：inline string 讀格子裡的文字，共用字串讀表裡那一筆項目的文字。
/// 正式輸出的文字寫進共用字串表，工作底稿與 JET 讀回用的紀錄寫成 inline string，測試用同一種讀法核對兩種寫法。
/// </summary>
internal static class WrittenCellText
{
    /// <summary>共用字串表每一筆項目的文字，依編號排列；活頁簿沒有共用字串表時是空的。</summary>
    internal static IReadOnlyList<string> SharedItems(WorkbookPart workbookPart) =>
        workbookPart.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>()
            .Select(item => item.InnerText)
            .ToArray()
        ?? [];

    /// <summary>文字格寫進檔案的文字；不是文字格時回傳 null。共用字串的編號不在表裡時丟出例外，不當成空白。</summary>
    internal static string? Of(Cell cell, IReadOnlyList<string> sharedItems)
    {
        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText;
        }

        if (cell.DataType?.Value != CellValues.SharedString)
        {
            return null;
        }

        var index = int.Parse(cell.CellValue!.Text, NumberStyles.None, CultureInfo.InvariantCulture);
        return index < sharedItems.Count
            ? sharedItems[index]
            : throw new InvalidOperationException($"{cell.CellReference?.Value} 的共用字串編號 {index} 不在表裡。");
    }
}
