using System.IO.Compression;
using System.Text;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 以 ZipArchive 手刻最小 .xlsx 的測試工具：產出 ClosedXML 寫不出的形狀
/// （phoneticPr/rPh 注音、row/cell 無 r 屬性、無 dimension、inlineStr/str/e 型別、
/// styled-empty cell、自訂 numFmt、chartsheet、零工作表）。
/// 只供 OpenXmlSaxTableReader 測試使用；正常 fixture 仍走 TestWorkbookBuilder。
/// </summary>
internal sealed class RawXlsxBuilder
{
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private readonly List<(string Name, string Xml, bool IsChartsheet)> _sheets = [];
    private string? _sharedStringsXml;
    private string? _stylesXml;

    /// <summary>worksheetXml = worksheet 元素的「內容」（通常含 dimension? + sheetData）。</summary>
    public RawXlsxBuilder AddSheet(string name, string worksheetInnerXml)
    {
        _sheets.Add((name, worksheetInnerXml, false));
        return this;
    }

    public RawXlsxBuilder AddChartsheet(string name)
    {
        _sheets.Add((name, string.Empty, true));
        return this;
    }

    /// <summary>sstInnerXml = 一串原始 &lt;si&gt; 項目。</summary>
    public RawXlsxBuilder WithSharedStrings(string sstInnerXml)
    {
        _sharedStringsXml =
            $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><sst xmlns="{MainNs}">{sstInnerXml}</sst>""";
        return this;
    }

    /// <summary>stylesInnerXml = styleSheet 元素的「內容」（numFmts? + cellXfs 等）。</summary>
    public RawXlsxBuilder WithStyles(string stylesInnerXml)
    {
        _stylesXml =
            $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="{MainNs}">{stylesInnerXml}</styleSheet>""";
        return this;
    }

    public string Save()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jet-rawxlsx-{Guid.NewGuid():N}.xlsx");

        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        var contentTypes = new StringBuilder(
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>""");
        var workbookSheets = new StringBuilder();
        var workbookRels = new StringBuilder();

        for (var i = 0; i < _sheets.Count; i++)
        {
            var (name, xml, isChartsheet) = _sheets[i];
            var sheetNo = i + 1;
            var partName = isChartsheet ? $"chartsheets/sheet{sheetNo}.xml" : $"worksheets/sheet{sheetNo}.xml";
            var contentType = isChartsheet
                ? "application/vnd.openxmlformats-officedocument.spreadsheetml.chartsheet+xml"
                : "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";

            contentTypes.Append($"""<Override PartName="/xl/{partName}" ContentType="{contentType}"/>""");
            workbookSheets.Append($"""<sheet name="{name}" sheetId="{sheetNo}" r:id="rId{sheetNo}"/>""");
            workbookRels.Append(
                $"""<Relationship Id="rId{sheetNo}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/{(isChartsheet ? "chartsheet" : "worksheet")}" Target="{partName}"/>""");

            var partXml = isChartsheet
                ? $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><chartsheet xmlns="{MainNs}"><sheetViews><sheetView workbookViewId="0"/></sheetViews></chartsheet>"""
                : $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="{MainNs}">{xml}</worksheet>""";
            AddEntry(zip, $"xl/{partName}", partXml);
        }

        var nextRelId = _sheets.Count + 1;
        if (_sharedStringsXml is not null)
        {
            contentTypes.Append(
                """<Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>""");
            workbookRels.Append(
                $"""<Relationship Id="rId{nextRelId}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/>""");
            AddEntry(zip, "xl/sharedStrings.xml", _sharedStringsXml);
            nextRelId++;
        }

        if (_stylesXml is not null)
        {
            contentTypes.Append(
                """<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""");
            workbookRels.Append(
                $"""<Relationship Id="rId{nextRelId}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>""");
            AddEntry(zip, "xl/styles.xml", _stylesXml);
        }

        contentTypes.Append("</Types>");

        AddEntry(zip, "[Content_Types].xml", contentTypes.ToString());
        AddEntry(zip, "_rels/.rels",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
        AddEntry(zip, "xl/workbook.xml",
            $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="{MainNs}" xmlns:r="{RelNs}"><sheets>{workbookSheets}</sheets></workbook>""");
        AddEntry(zip, "xl/_rels/workbook.xml.rels",
            $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{workbookRels}</Relationships>""");

        return path;
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
