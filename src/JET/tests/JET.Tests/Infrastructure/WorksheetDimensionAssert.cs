using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 檢查輸出檔裡每一張工作表的 dimension：一定要有，位置在 sheetPr 之後、其他元素之前，
/// ref 等於工作表裡所有格子涵蓋的範圍，和 Excel 存檔時相同。沒有格子的工作表，Excel 寫 A1。
/// 直接讀工作表的 XML，不經過會自己補算範圍的讀取程式。
/// </summary>
internal static class WorksheetDimensionAssert
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Spreadsheet = SpreadsheetNamespace;

    internal static void EveryWorksheetMatchesWrittenCells(byte[] package) => Assert.Empty(Problems(package));

    /// <summary>每一張不符合的工作表一筆說明，寫工作表名稱、宣告的範圍與實際寫出的範圍。</summary>
    internal static IReadOnlyList<string> Problems(byte[] package)
    {
        using var document = SpreadsheetDocument.Open(new MemoryStream(package, writable: false), false);
        var workbookPart = document.WorkbookPart!;
        var problems = new List<string>();
        foreach (var sheet in workbookPart.Workbook!.Sheets!.Elements<Sheet>())
        {
            var part = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
            XDocument xml;
            using (var stream = part.GetStream(FileMode.Open, FileAccess.Read))
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            {
                xml = XDocument.Load(reader);
            }

            var name = sheet.Name?.Value;
            var children = xml.Root!.Elements().ToArray();
            var dimensions = children.Where(element => element.Name == Spreadsheet + "dimension").ToArray();
            if (dimensions.Length != 1)
            {
                problems.Add($"{name}：有 {dimensions.Length} 個 dimension");
                continue;
            }

            var dimensionIndex = Array.IndexOf(children, dimensions[0]);
            if (children.Take(dimensionIndex).Any(element => element.Name != Spreadsheet + "sheetPr"))
            {
                problems.Add($"{name}：dimension 前面除了 sheetPr 還有其他元素");
            }

            var declared = (string?)dimensions[0].Attribute("ref");
            var written = WrittenRange(xml.Root!);
            if (!string.Equals(declared, written, StringComparison.Ordinal))
            {
                problems.Add($"{name}：dimension 是 {declared}，寫出的格子範圍是 {written}");
            }
        }

        return problems;
    }

    /// <summary>所有 c 的 r 涵蓋的範圍；只有一格時寫那一格，沒有格子時寫 A1。</summary>
    private static string WrittenRange(XElement worksheet)
    {
        var cells = worksheet
            .Descendants(Spreadsheet + "c")
            .Select(cell => ParseReference((string)cell.Attribute("r")!))
            .ToArray();
        if (cells.Length == 0)
        {
            return "A1";
        }

        var first = CellReference(cells.Min(cell => cell.Column), cells.Min(cell => cell.Row));
        var last = CellReference(cells.Max(cell => cell.Column), cells.Max(cell => cell.Row));
        return first == last ? first : first + ":" + last;
    }

    private static (uint Column, uint Row) ParseReference(string reference)
    {
        var column = 0U;
        var index = 0;
        while (index < reference.Length && char.IsAsciiLetterUpper(reference[index]))
        {
            column = column * 26U + (uint)(reference[index] - 'A' + 1);
            index++;
        }

        return (column, uint.Parse(reference.AsSpan(index), NumberStyles.None, CultureInfo.InvariantCulture));
    }

    private static string CellReference(uint column, uint row)
    {
        var letters = string.Empty;
        while (column > 0)
        {
            letters = (char)('A' + (int)((column - 1) % 26)) + letters;
            column = (column - 1) / 26;
        }

        return letters + row.ToString(CultureInfo.InvariantCulture);
    }
}
