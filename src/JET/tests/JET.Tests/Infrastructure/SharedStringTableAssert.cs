using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 檢查輸出檔的文字都寫進共用字串表，和 Excel 存檔時相同：
/// <list type="bullet">
/// <item>工作表裡沒有 inline string，只有 JET 讀回用的兩種隱藏紀錄例外：JET_Metadata 整張工作表，以及欄位資訊頁的 F1:H1。</item>
/// <item>這兩種紀錄有文字的格子一定是 inline string，讀回時不必載入共用字串表。</item>
/// <item>每個參照共用字串表的格子，編號都在表裡。</item>
/// <item>uniqueCount 等於表裡的項目數，count 等於所有工作表參照共用字串表的格數。</item>
/// </list>
/// 直接讀 XML，不經過會自己解析共用字串的讀取程式。
/// </summary>
internal static class SharedStringTableAssert
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Spreadsheet = SpreadsheetNamespace;
    private static readonly string[] FieldInfoRecordCells = ["F1", "G1", "H1"];

    internal static void TextCellsReferToTheSharedStringTable(byte[] package) => Assert.Empty(Problems(package));

    /// <summary>每一個不符合的地方一筆說明，寫工作表名稱與格子位置，不寫格子的文字。</summary>
    internal static IReadOnlyList<string> Problems(byte[] package)
    {
        using var document = SpreadsheetDocument.Open(new MemoryStream(package, writable: false), false);
        var workbookPart = document.WorkbookPart!;
        var problems = new List<string>();
        if (workbookPart.SharedStringTablePart is not { } sharedStringPart)
        {
            return ["活頁簿沒有共用字串表"];
        }

        var sharedStrings = Load(sharedStringPart);
        var itemCount = sharedStrings.Elements(Spreadsheet + "si").Count();
        var references = 0L;
        foreach (var sheet in workbookPart.Workbook!.Sheets!.Elements<Sheet>())
        {
            var name = sheet.Name?.Value ?? string.Empty;
            var part = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
            var isMetadata = string.Equals(name, ReportWorkbookMetadataFormat.WorksheetName, StringComparison.Ordinal);
            var isFieldInfo = string.Equals(name, MappingMetadataFormat.WorksheetName, StringComparison.Ordinal);
            foreach (var cell in Load(part).Descendants(Spreadsheet + "c"))
            {
                var reference = (string?)cell.Attribute("r");
                var type = (string?)cell.Attribute("t");
                var isRecord = isMetadata || (isFieldInfo && FieldInfoRecordCells.Contains(reference, StringComparer.Ordinal));
                if (isRecord)
                {
                    if (string.Equals(type, "s", StringComparison.Ordinal))
                    {
                        problems.Add($"{name}：{reference} 是讀回用的紀錄，卻參照共用字串表");
                    }

                    continue;
                }

                if (string.Equals(type, "inlineStr", StringComparison.Ordinal))
                {
                    problems.Add($"{name}：{reference} 是 inline string");
                    continue;
                }

                if (!string.Equals(type, "s", StringComparison.Ordinal))
                {
                    continue;
                }

                references++;
                var value = (string?)cell.Element(Spreadsheet + "v");
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index >= itemCount)
                {
                    problems.Add($"{name}：{reference} 的編號 {value} 不在共用字串表裡");
                }
            }
        }

        if ((string?)sharedStrings.Attribute("uniqueCount") != itemCount.ToString(CultureInfo.InvariantCulture))
        {
            problems.Add($"uniqueCount 是 {(string?)sharedStrings.Attribute("uniqueCount")}，實際有 {itemCount} 個項目");
        }

        if ((string?)sharedStrings.Attribute("count") != references.ToString(CultureInfo.InvariantCulture))
        {
            problems.Add($"count 是 {(string?)sharedStrings.Attribute("count")}，工作表實際有 {references} 格參照共用字串表");
        }

        return problems;
    }

    /// <summary>JET 讀回用的兩種隱藏紀錄裡有文字的格子；每一格都要是 inline string，回傳不是的格子。</summary>
    internal static IReadOnlyList<string> RecordCellsNotInline(byte[] package)
    {
        using var document = SpreadsheetDocument.Open(new MemoryStream(package, writable: false), false);
        var workbookPart = document.WorkbookPart!;
        var problems = new List<string>();
        foreach (var sheet in workbookPart.Workbook!.Sheets!.Elements<Sheet>())
        {
            var name = sheet.Name?.Value ?? string.Empty;
            var isMetadata = string.Equals(name, ReportWorkbookMetadataFormat.WorksheetName, StringComparison.Ordinal);
            var isFieldInfo = string.Equals(name, MappingMetadataFormat.WorksheetName, StringComparison.Ordinal);
            if (!isMetadata && !isFieldInfo)
            {
                continue;
            }

            var part = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
            var textCells = isMetadata
                ? MetadataTextCells(Load(part))
                : Load(part).Descendants(Spreadsheet + "c")
                    .Where(cell => FieldInfoRecordCells.Contains((string?)cell.Attribute("r"), StringComparer.Ordinal)
                                   && cell.Attribute("t") is not null
                                   && !string.Equals((string?)cell.Attribute("t"), "n", StringComparison.Ordinal));
            problems.AddRange(textCells
                .Where(cell => !string.Equals((string?)cell.Attribute("t"), "inlineStr", StringComparison.Ordinal))
                .Select(cell => $"{name}：{(string?)cell.Attribute("r")} 不是 inline string"));
        }

        return problems;
    }

    /// <summary>JET_Metadata 放文字的格子：A1 的標記，以及第 2 列起 B 欄的分段。</summary>
    private static IEnumerable<XElement> MetadataTextCells(XElement worksheet) =>
        worksheet.Descendants(Spreadsheet + "c").Where(cell =>
        {
            var reference = (string?)cell.Attribute("r") ?? string.Empty;
            return string.Equals(reference, "A1", StringComparison.Ordinal)
                   || (reference.StartsWith('B') && !string.Equals(reference, "B1", StringComparison.Ordinal));
        });

    private static XElement Load(OpenXmlPart part)
    {
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader).Root!;
    }
}
