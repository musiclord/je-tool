using System.Text;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 工作表 XML 的共用寫出程式：Open XML SDK 的元素經過它寫出時，主要命名空間不加前綴，內容讀回來和原本相同。
/// 資料都是程式產生的合成文字。
/// </summary>
public sealed class SpreadsheetXmlWriterTests
{
    private static readonly XNamespace Spreadsheet = SpreadsheetXmlWriter.MainNamespace;

    [Fact]
    public void ElementBuiltFromOuterXml_IsWrittenInTheDefaultNamespace()
    {
        // 用 XML 文字建立、還沒解析的元素，SDK 會把整段文字原樣交給寫出程式。
        var row = new Row(
            $"<x:row xmlns:x=\"{SpreadsheetXmlWriter.MainNamespace}\" r=\"1\">"
            + "<x:c r=\"A1\" t=\"inlineStr\"><x:is><x:t xml:space=\"preserve\">合成\n文字</x:t></x:is></x:c></x:row>");

        var xml = WriteWorksheet(writer => row.WriteTo(writer));

        Assert.DoesNotContain("x:", xml, StringComparison.Ordinal);
        Assert.Contains("<row r=\"1\"><c r=\"A1\" t=\"inlineStr\">", xml, StringComparison.Ordinal);
        Assert.Equal("合成\n文字", Parse(xml).Descendants(Spreadsheet + "t").Single().Value);
    }

    [Fact]
    public void LineBreaksInAttributes_SurviveReadingBack()
    {
        // 屬性值裡直接寫出的換行，讀回時會被換成空白，所以要寫成字元參照。
        var validation = new DataValidation { Prompt = "合成提示第一行\n第二行\r\n第三行\t結尾" };

        var xml = WriteWorksheet(writer => validation.WriteTo(writer));

        Assert.Equal(
            "合成提示第一行\n第二行\r\n第三行\t結尾",
            (string?)Parse(xml).Descendants(Spreadsheet + "dataValidation").Single().Attribute("prompt"));
    }

    [Fact]
    public void OtherNamespacePrefixes_AreKept()
    {
        var row = new Row { RowIndex = 1, DyDescent = 0.25 };

        var xml = WriteWorksheet(writer => row.WriteTo(writer));

        Assert.Contains("x14ac:dyDescent=\"0.25\"", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<x:", xml, StringComparison.Ordinal);
        Assert.Equal(
            "0.25",
            (string?)Parse(xml).Descendants(Spreadsheet + "row").Single()
                .Attribute(XName.Get("dyDescent", "http://schemas.microsoft.com/office/spreadsheetml/2009/9/ac")));
    }

    /// <summary>
    /// 共用字串表的 count 要等於所有工作表參照它的格數，所以寫出程式數主要命名空間裡 t="s" 的 c 元素：
    /// SDK 的元素、還沒解析的元素與從 XmlReader 複製的節點都算；inline string、數字、樣式屬性 s 與其他命名空間的 c 不算。
    /// </summary>
    [Fact]
    public void CellsReferringToTheSharedStringTable_AreCounted()
    {
        var typed = new Row(
            new Cell { CellReference = "A1", DataType = CellValues.SharedString, CellValue = new CellValue("0") },
            new Cell { CellReference = "B1", DataType = CellValues.SharedString, StyleIndex = 1U, CellValue = new CellValue("1") },
            new Cell { CellReference = "C1", DataType = CellValues.Number, StyleIndex = 2U, CellValue = new CellValue("5") },
            new Cell { CellReference = "D1", StyleIndex = 3U })
        { RowIndex = 1 };
        var unparsed = new Row(
            $"<x:row xmlns:x=\"{SpreadsheetXmlWriter.MainNamespace}\" r=\"2\">"
            + "<x:c r=\"A2\" t=\"s\"><x:v>2</x:v></x:c>"
            + "<x:c r=\"B2\" t=\"inlineStr\"><x:is><x:t>合成</x:t></x:is></x:c></x:row>");
        const string copied =
            $"<row xmlns=\"{SpreadsheetXmlWriter.MainNamespace}\" xmlns:o=\"urn:synthetic\" r=\"3\">"
            + "<c r=\"A3\" s=\"1\" t=\"s\"><v>3</v></c><o:c t=\"s\" /><c r=\"B3\" s=\"4\"><v>1</v></c></row>";

        long references = -1;
        WriteWorksheet(
            writer =>
            {
                typed.WriteTo(writer);
                unparsed.WriteTo(writer);
                using var reader = XmlReader.Create(new StringReader(copied));
                reader.MoveToContent();
                writer.WriteNode(reader, defattr: true);
                references = ((SpreadsheetXmlWriter)writer).SharedStringReferences;
            });

        Assert.Equal(4L, references);
    }

    /// <summary>
    /// 已經是合法 XML 的位元組照原樣接在開始標記後面，例如收尾時從暫存檔複製的共用字串項目；開始標記先寫完整，
    /// 後面再寫的元素與結束標記接在這些位元組之後。
    /// </summary>
    [Fact]
    public void PreparedContent_IsWrittenRightAfterTheStartTag()
    {
        var prepared = Encoding.UTF8.GetBytes("<si><t xml:space=\"preserve\">合成\n項目</t></si>");
        using var output = new MemoryStream();
        using (var writer = SpreadsheetXmlWriter.Create(output))
        {
            writer.WriteStartDocument(standalone: true);
            writer.WriteStartElement(string.Empty, "sst", SpreadsheetXmlWriter.MainNamespace);
            writer.WriteAttributeString("count", "2");
            writer.WritePreparedContent(new MemoryStream(prepared, writable: false), CancellationToken.None);
            writer.WriteStartElement(string.Empty, "si", SpreadsheetXmlWriter.MainNamespace);
            writer.WriteElementString("t", SpreadsheetXmlWriter.MainNamespace, "合成結尾");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        var xml = Encoding.UTF8.GetString(output.ToArray());
        Assert.EndsWith("\"><si><t xml:space=\"preserve\">合成\n項目</t></si><si><t>合成結尾</t></si></sst>", xml, StringComparison.Ordinal);
        Assert.Equal(
            ["合成\n項目", "合成結尾"],
            Parse(xml).Root!.Elements(Spreadsheet + "si").Select(item => item.Value));
    }

    private static string WriteWorksheet(Action<XmlWriter> writeContent)
    {
        using var output = new MemoryStream();
        using (var writer = SpreadsheetXmlWriter.Create(output))
        {
            writer.WriteStartDocument(standalone: true);
            writer.WriteStartElement(string.Empty, "worksheet", SpreadsheetXmlWriter.MainNamespace);
            writer.WriteStartElement(string.Empty, "sheetData", SpreadsheetXmlWriter.MainNamespace);
            writeContent(writer);
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        var bytes = output.ToArray();
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), "寫出的 XML 不應該帶 BOM。");
        return Encoding.UTF8.GetString(bytes);
    }

    private static XDocument Parse(string xml) => XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
}
