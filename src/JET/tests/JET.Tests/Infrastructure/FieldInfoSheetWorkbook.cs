using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 用 OpenXml SDK 寫出只有「自動化工具-檔案欄位資訊」看得見對照表的合成活頁簿，沒有任何 JET 隱藏配對紀錄。
/// 儲存格輪流寫成共用字串與內嵌字串，兩種讀法都會用到。欄名都是合成的。
/// </summary>
internal static class FieldInfoSheetWorkbook
{
    internal const string SheetName = "自動化工具-檔案欄位資訊";
    internal const string TbTitle = "TB檔案配對前後欄位對照表";
    internal const string GlTitle = "GL檔案配對前後欄位對照表";

    internal static readonly string?[] Header = ["配對前欄位名稱", "欄位型態", "文字長度", "小數位數", "配對後欄位名稱"];

    /// <summary>
    /// 舊版 IDEA JET 的版面：A1 版本字樣、A2 TB 標題、第 3 列表頭、TB 列，
    /// 空一列後是 GL 標題、表頭與 GL 列。
    /// </summary>
    internal static IReadOnlyList<string?[]?> LegacyLayout(IEnumerable<string?[]> tb, IEnumerable<string?[]> gl) =>
        [["V.2019"], [TbTitle], Header, .. tb, null, [GlTitle], Header, .. gl];

    /// <summary>新版 JET 的版面，不含 F1:H1 的隱藏紀錄：第 1 列空白，TB 與 GL 之間空兩列。</summary>
    internal static IReadOnlyList<string?[]?> CurrentLayout(IEnumerable<string?[]> tb, IEnumerable<string?[]> gl) =>
        [null, [TbTitle], Header, .. tb, null, null, [GlTitle], Header, .. gl];

    /// <summary>第 N 個元素寫在第 N 列；null 的列不寫出列元素，null 的儲存格不寫出儲存格。</summary>
    internal static string Write(IReadOnlyList<string?[]?> rows, string sheetName = SheetName)
    {
        var path = Path.Combine(Path.GetTempPath(), $"jet-field-info-sheet-{Guid.NewGuid():N}.xlsx");
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var sharedStrings = workbookPart.AddNewPart<SharedStringTablePart>();
        sharedStrings.SharedStringTable = new SharedStringTable();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        var sharedIndex = 0;
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            if (rows[rowIndex] is not { } cells)
            {
                continue;
            }

            var rowNumber = (uint)(rowIndex + 1);
            var row = new Row { RowIndex = rowNumber };
            for (var column = 0; column < cells.Length; column++)
            {
                if (cells[column] is not { } text)
                {
                    continue;
                }

                var reference = $"{(char)('A' + column)}{rowNumber.ToString(CultureInfo.InvariantCulture)}";
                var value = new Text(text) { Space = SpaceProcessingModeValues.Preserve };
                if (column % 2 == 0)
                {
                    sharedStrings.SharedStringTable.AppendChild(new SharedStringItem(value));
                    row.AppendChild(new Cell
                    {
                        CellReference = reference,
                        DataType = CellValues.SharedString,
                        CellValue = new CellValue((sharedIndex++).ToString(CultureInfo.InvariantCulture))
                    });
                }
                else
                {
                    row.AppendChild(new Cell
                    {
                        CellReference = reference,
                        DataType = CellValues.InlineString,
                        InlineString = new InlineString(value)
                    });
                }
            }

            sheetData.AppendChild(row);
        }

        worksheetPart.Worksheet = new Worksheet(sheetData);
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.AppendChild(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = sheetName
        });
        workbookPart.Workbook.Save();
        return path;
    }
}
