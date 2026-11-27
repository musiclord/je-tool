using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 疊寫範本工作表時，產生端的列有兩種交法：交 XML 物件，或直接交產生端建立的列。
/// 後者遇到不必和範本合併的列會直接寫出，不先轉成文字再解析。這裡確認兩種交法寫出的工作表逐位元組相同，
/// 樣式合併函式被呼叫的順序與參數也相同。
/// </summary>
public sealed class DirectTemplateOverlayGeneratedRowTests
{
    public static TheoryData<string> PlanNames => new()
    {
        "EveryCellFromSource",
        "MergeStyles",
        "SomeCellsBelongToTemplate"
    };

    [Theory]
    [MemberData(nameof(PlanNames))]
    public void GeneratedRows_WrittenDirectly_AreByteIdenticalToRowsGivenAsXml(string planName)
    {
        var xmlCalls = new List<string>();
        var viaXml = Write(
            planName,
            xmlCalls,
            (overlay, row) => overlay.WriteRow(
                XElement.Parse(row.OuterXml, LoadOptions.PreserveWhitespace)));
        var directCalls = new List<string>();
        var direct = Write(
            planName,
            directCalls,
            (overlay, row) => overlay.WriteRow(row));

        Assert.Equal(Encoding.UTF8.GetString(viaXml), Encoding.UTF8.GetString(direct));
        Assert.Equal(viaXml, direct);
        Assert.Equal(xmlCalls, directCalls);
    }

    [Fact]
    public void GeneratedRows_WrittenDirectly_KeepTextNumbersAndStyles()
    {
        var bytes = Write(
            "EveryCellFromSource",
            [],
            (overlay, row) => overlay.WriteRow(row));

        using var stream = new MemoryStream(bytes);
        XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var cells = XDocument.Load(stream, LoadOptions.PreserveWhitespace)
            .Descendants(spreadsheet + "c")
            .ToDictionary(
                cell => (string)cell.Attribute("r")!,
                cell => (
                    Text: cell.Value,
                    Style: (uint?)cell.Attribute("s")),
                StringComparer.Ordinal);

        // 第 1 列不歸產生端，照範本；第 3 列和範本合併，這個情境的樣式跟產生端；第 4 列起整列由產生端決定。
        Assert.Equal(("template-1", 1U), cells["A1"]);
        Assert.Equal(("row-3", 6U), cells["A3"]);
        Assert.Equal(("  leading and trailing  ", 7U), cells["A4"]);
        Assert.Equal(("a & b < c > d \" e ' f", 8U), cells["B4"]);
        Assert.Equal(("1234.5600", 9U), cells["C4"]);
        Assert.Equal((string.Empty, 10U), cells["D4"]);
        Assert.Equal(("𠀀字與表情😀", 7U), cells["A6"]);
        Assert.Equal(("row-9", 7U), cells["A9"]);
    }

    [Fact]
    public void GeneratedRow_WithACharacterXmlDoesNotAllow_FailsTheSameWayAsARowGivenAsXml()
    {
        // 正式的寫出程式會先把這種字元編碼掉；這裡直接放進格子，確認兩種交法失敗時是同一種例外：
        // 先轉成文字的做法在轉文字時就被擋下，直接寫出時在寫進工作表時被擋下。
        static Row Invalid() => new(Text("A6", "bad\u000Bcharacter", 7)) { RowIndex = 6 };

        Assert.Throws<ArgumentException>(() => Write(
            "EveryCellFromSource",
            [],
            (overlay, row) => overlay.WriteRow(
                XElement.Parse(row.OuterXml, LoadOptions.PreserveWhitespace)),
            [Invalid()]));
        Assert.Throws<ArgumentException>(() => Write(
            "EveryCellFromSource",
            [],
            (overlay, row) => overlay.WriteRow(row),
            [Invalid()]));
    }

    private static byte[] Write(
        string planName,
        List<string> mergeCalls,
        Action<DirectTemplateWorksheetOverlayWriter, Row> writeRow,
        IReadOnlyList<Row>? rows = null)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = new Worksheet(
                new SheetData(
                    new Row(Text("A1", "template-1", 1)) { RowIndex = 1 },
                    new Row(Text("A3", "template-3", 2), Text("C3", "template-3c", 2)) { RowIndex = 3 },
                    new Row(Text("B5", "template-5", 3)) { RowIndex = 5 }));
            workbookPart.Workbook.AppendChild(new Sheets()).Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Synthetic"
            });
            workbookPart.Workbook.Save();
            worksheetPart.Worksheet.Save();
        }

        stream.Position = 0;
        using (var document = SpreadsheetDocument.Open(stream, isEditable: true))
        {
            var part = document.WorkbookPart!.WorksheetParts.Single();
            using var overlay = new DirectTemplateWorksheetOverlayWriter(
                "Synthetic",
                part,
                Plan(planName, mergeCalls),
                CancellationToken.None);
            overlay.Start(generatedDimension: null, generatedColumns: null);
            foreach (var row in rows ?? GeneratedRows())
            {
                writeRow(overlay, row);
            }
            overlay.Complete([]);
        }

        stream.Position = 0;
        using var read = SpreadsheetDocument.Open(stream, isEditable: false);
        using var content = read.WorkbookPart!.WorksheetParts.Single().GetStream(FileMode.Open, FileAccess.Read);
        using var copy = new MemoryStream();
        content.CopyTo(copy);
        return copy.ToArray();
    }

    private static DirectTemplateWorksheetOverlayPlan Plan(string name, List<string> mergeCalls)
    {
        Func<uint, uint, uint?, uint, uint> merge = (row, column, templateStyle, sourceStyle) =>
        {
            mergeCalls.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{row}:{column}:{templateStyle?.ToString(CultureInfo.InvariantCulture) ?? "none"}:{sourceStyle}"));
            // 回傳值跟著呼叫次數變，順序一旦不同，寫出的樣式也會不同。
            return checked(100U + (uint)mergeCalls.Count);
        };
        return new DirectTemplateWorksheetOverlayPlan(
            IsDynamicRow: row => row >= 3,
            IsDynamicCell: name == "SomeCellsBelongToTemplate"
                ? (row, column) => row >= 3 && column <= 3
                : (row, _) => row >= 3,
            UseSourcePostSheetDataElements: new HashSet<string>(StringComparer.Ordinal),
            UseSourceDimension: false,
            PreserveTemplateExtent: false,
            UseSourceColumnsWhenTemplateMissing: false,
            UseSourceColumns: false,
            MergeSourceColumnWidths: false,
            UseSourceCellStyle: name == "MergeStyles"
                ? (_, column) => column != 2
                : (_, _) => true,
            MergeCellStyle: name == "MergeStyles" ? merge : null);
    }

    /// <summary>
    /// 每次呼叫都重新建立，因為直接寫出時會改列裡格子的樣式。內容涵蓋不歸產生端的列、和範本同一列的列、
    /// 落在兩個範本列之間與範本列之後的列，以及需要跳脫、保留空白或換行的文字、數值、沒有值與沒有樣式的格子。
    /// </summary>
    private static IReadOnlyList<Row> GeneratedRows() =>
    [
        new Row(Text("A1", "ignored", 5)) { RowIndex = 1 },
        new Row(Text("A3", "row-3", 6), Text("B3", "row-3b", 6)) { RowIndex = 3 },
        new Row(
            Text("A4", "  leading and trailing  ", 7),
            Text("B4", "a & b < c > d \" e ' f", 8),
            new Cell
            {
                CellReference = "C4",
                StyleIndex = 9,
                DataType = CellValues.Number,
                CellValue = new CellValue("1234.5600")
            },
            new Cell { CellReference = "D4", StyleIndex = 10 })
        {
            RowIndex = 4,
            Height = 42,
            CustomHeight = true
        },
        new Row(Text("A5", "row-5", 7), Text("B5", "row-5b", 7)) { RowIndex = 5 },
        new Row(
            Text("A6", "𠀀字與表情😀", 7),
            Text("B6", "line1\nline2", 7),
            Text("C6", "line1\r\nline2", 7),
            Text("D6", "line1\rline2", 7),
            Text("E6", "tab\tinside", 7))
        {
            RowIndex = 6
        },
        new Row(
            Text("A7", "no style on the next cell", 7),
            new Cell
            {
                CellReference = "C7",
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text("unstyled"))
            })
        {
            RowIndex = 7
        },
        new Row(
            new Cell { CellReference = "A8", StyleIndex = 7, DataType = CellValues.InlineString, InlineString = new InlineString() },
            new Cell { CellReference = "B8", StyleIndex = 7, CellFormula = new CellFormula("SUM(1,2)") })
        {
            RowIndex = 8
        },
        new Row(Text("A9", "row-9", 7), Text("AB9", "far column", 7)) { RowIndex = 9 },
        new Row { RowIndex = 10 }
    ];

    private static Cell Text(string reference, string value, uint style) => new()
    {
        CellReference = reference,
        StyleIndex = style,
        DataType = CellValues.InlineString,
        InlineString = new InlineString(
            new Text(value) { Space = SpaceProcessingModeValues.Preserve })
    };
}
