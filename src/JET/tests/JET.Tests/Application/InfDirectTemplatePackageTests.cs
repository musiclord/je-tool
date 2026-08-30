using System.IO.Compression;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class InfDirectTemplatePackageTests(ReportArtifactExportFixture fixture)
    : IClassFixture<ReportArtifactExportFixture>
{
    private const string MainSheet = "INF Testing 可靠性測試";
    private const string AllFieldsSheet = "可靠性樣本_所有欄位";

    [Fact]
    public async Task InfReport_FillsOnlyTheTwoTemplateWorksheetsAndPreservesStaticReliabilityCells()
    {
        var response = await fixture.ExportValidationArtifactsAsync();
        var artifact = response.GetProperty("artifacts").EnumerateArray()
            .Single(item => item.GetProperty("kind").GetString() == "infReport");
        var outputPath = fixture.ArtifactPath(artifact);
        var templatePath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "JET",
            "JET",
            "Templates",
            "INFReport.xlsx");

        Assert.Equal(
            new[]
            {
                "[Content_Types].xml",
                "xl/_rels/workbook.xml.rels",
                "xl/sharedStrings.xml",
                "xl/styles.xml",
                "xl/workbook.xml",
                "xl/worksheets/sheet1.xml",
                "xl/worksheets/sheet2.xml",
                "xl/worksheets/sheet3.xml"
            },
            ChangedPackageParts(templatePath, outputPath));
        AssertInfStylesPreserveSemanticsAndNormalizeFontFamily(templatePath, outputPath);

        using (var workbook = new XLWorkbook(outputPath))
        {
            Assert.Equal(
                new[]
                {
                    MainSheet,
                    AllFieldsSheet,
                    ReportWorkbookMetadataFormat.WorksheetName
                },
                workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
            Assert.Equal(
                XLWorksheetVisibility.VeryHidden,
                workbook.Worksheet(ReportWorkbookMetadataFormat.WorksheetName).Visibility);
            var main = workbook.Worksheet(MainSheet);
            Assert.StartsWith("公司名稱 [", main.Cell("A1").GetString(), StringComparison.Ordinal);
            Assert.StartsWith("財務報表期間 [", main.Cell("A2").GetString(), StringComparison.Ordinal);
            Assert.Equal("證實測試 - 會計分錄及其他調整", main.Cell("A3").GetString());
            Assert.Equal("傳票核准人員", main.Cell("L51").GetString());
            Assert.Equal(
                Enumerable.Range(1, 59).Select(value => $"{value}.").ToArray(),
                main.Range("A53:A111").Cells().Select(cell => cell.GetString()).ToArray());
            Assert.True(main.Cell("B55").IsEmpty());
            Assert.True(main.Cell("L111").IsEmpty());
            Assert.All(
                main.Range("M53:T111").Cells(),
                cell => Assert.True(cell.IsEmpty()));
            Assert.DoesNotContain(
                workbook.Worksheets.SelectMany(sheet => sheet.CellsUsed())
                    .Select(cell => cell.GetString()),
                value => value.Contains("Run ID", StringComparison.OrdinalIgnoreCase));

            var allFields = workbook.Worksheet(AllFieldsSheet);
            Assert.Equal(
                new[]
                {
                    "傳票號碼", "傳票日期", "核准日期", "科目代號",
                    "科目名稱", "摘要", "金額", "借方旗標"
                },
                allFields.Row(1).Cells(1, 8).Select(cell => cell.GetString()).ToArray());
            Assert.Equal(8, allFields.LastColumnUsed()!.ColumnNumber());
            Assert.Equal(3, allFields.LastRowUsed()!.RowNumber());
        }

        using var template = SpreadsheetDocument.Open(templatePath, false);
        using var output = SpreadsheetDocument.Open(outputPath, false);
        AssertCanonicalMetadataWorksheet(output);
        var templateMain = WorksheetFor(template, MainSheet);
        var outputMain = WorksheetFor(output, MainSheet);
        Assert.Equal(
            templateMain.Elements<MergeCells>().Single().Elements<MergeCell>()
                .Select(merge => merge.Reference?.Value).ToArray(),
            outputMain.Elements<MergeCells>().Single().Elements<MergeCell>()
                .Select(merge => merge.Reference?.Value).ToArray());
        Assert.Equal(
            templateMain.Elements<DataValidations>().Single().OuterXml,
            outputMain.Elements<DataValidations>().Single().OuterXml);
        Assert.Equal(
            templateMain.GetFirstChild<SheetProtection>()?.OuterXml,
            outputMain.GetFirstChild<SheetProtection>()?.OuterXml);
        Assert.Equal(10.6328125D, ColumnFor(templateMain, 6).Width!.Value, 6);
        Assert.True(ColumnFor(outputMain, 6).Width!.Value >= 15D);
        Assert.Equal(7.6328125D, ColumnFor(templateMain, 20).Width!.Value, 6);
        Assert.Equal(22.453125D, ColumnFor(outputMain, 20).Width!.Value, 6);
        Assert.Equal(55.5D, RowFor(templateMain, 52).Height!.Value, 6);
        Assert.Equal(55.5D, RowFor(outputMain, 52).Height!.Value, 6);
        foreach (var columnIndex in Enumerable.Range(2, 11).Select(value => (uint)value))
        {
            Assert.True(
                ColumnFor(outputMain, columnIndex).Width!.Value
                >= ColumnFor(templateMain, columnIndex).Width!.Value);
        }
        foreach (var columnIndex in Enumerable.Range(1, 22).Select(value => (uint)value)
                     .Except(Enumerable.Range(2, 11).Select(value => (uint)value))
                     .Except([20U]))
        {
            Assert.Equal(
                ColumnLayout(ColumnFor(templateMain, columnIndex)),
                ColumnLayout(ColumnFor(outputMain, columnIndex)));
        }
        foreach (var reference in new[] { "F51", "K51" })
        {
            var generatedHeader = CellFormatFor(output, CellFor(outputMain, reference));
            Assert.False(generatedHeader.Alignment?.WrapText?.Value ?? false);
            Assert.Equal(0U, generatedHeader.Alignment?.Indent?.Value ?? 0U);
            Assert.False(generatedHeader.Alignment?.ShrinkToFit?.Value ?? false);
        }
        foreach (var reference in HeaderCellReferences()
                     .Except(["F51", "K51"], StringComparer.Ordinal))
        {
            Assert.Equal(
                CellFor(templateMain, reference).StyleIndex?.Value,
                CellFor(outputMain, reference).StyleIndex?.Value);
        }
        foreach (var reference in StaticCellReferences())
        {
            Assert.Equal(
                CellFor(templateMain, reference).StyleIndex?.Value,
                CellFor(outputMain, reference).StyleIndex?.Value);
        }
    }

    private static void AssertCanonicalMetadataWorksheet(SpreadsheetDocument document)
    {
        Assert.Empty(new OpenXmlValidator().Validate(document));
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("INF output has no workbook part.");
        var metadataSheet = workbookPart.Workbook.Sheets!.Elements<Sheet>().Single(sheet =>
            string.Equals(
                sheet.Name?.Value,
                ReportWorkbookMetadataFormat.WorksheetName,
                StringComparison.Ordinal));
        Assert.Equal(SheetStateValues.VeryHidden, metadataSheet.State?.Value);

        var worksheet = Assert.IsType<WorksheetPart>(
            workbookPart.GetPartById(metadataSheet.Id!.Value!)).Worksheet;
        var rows = worksheet.GetFirstChild<SheetData>()!.Elements<Row>().ToArray();
        Assert.NotEmpty(rows);
        Assert.Equal(ReportWorkbookMetadataFormat.Marker, MetadataCellText(rows[0], "A1"));
        Assert.Equal(
            ReportWorkbookMetadataFormat.CurrentVersion.ToString(CultureInfo.InvariantCulture),
            MetadataCellText(rows[0], "B1"));

        var chunkCount = int.Parse(MetadataCellText(rows[0], "C1"), CultureInfo.InvariantCulture);
        Assert.True(chunkCount > 0);
        Assert.Equal(chunkCount + 1, rows.Length);
        var chunks = new string[chunkCount];
        for (var index = 0; index < chunkCount; index++)
        {
            var row = rows[index + 1];
            var rowIndex = checked((uint)index + 2U);
            Assert.Equal(rowIndex, row.RowIndex?.Value);
            Assert.True(row.Hidden?.Value ?? false);
            Assert.Equal(
                (index + 1).ToString(CultureInfo.InvariantCulture),
                MetadataCellText(row, $"A{rowIndex}"));
            chunks[index] = MetadataCellText(row, $"B{rowIndex}");
            Assert.InRange(
                chunks[index].Length,
                1,
                ReportWorkbookMetadataFormat.MaximumChunkLength);
            if (index < chunkCount - 1)
            {
                Assert.Equal(
                    ReportWorkbookMetadataFormat.MaximumChunkLength,
                    chunks[index].Length);
            }
        }

        using var payload = JsonDocument.Parse(string.Concat(chunks));
        Assert.Equal(
            ReportWorkbookMetadataFormat.CurrentVersion,
            payload.RootElement.GetProperty("formatVersion").GetInt32());
    }

    private static string MetadataCellText(Row row, string reference)
    {
        var cell = row.Elements<Cell>().Single(cell => string.Equals(
            cell.CellReference?.Value,
            reference,
            StringComparison.Ordinal));
        return cell.InlineString?.InnerText ?? cell.CellValue?.Text ?? string.Empty;
    }

    private static string[] ChangedPackageParts(string templatePath, string outputPath)
    {
        using var template = ZipFile.OpenRead(templatePath);
        using var output = ZipFile.OpenRead(outputPath);
        var names = template.Entries.Select(entry => entry.FullName)
            .Union(output.Entries.Select(entry => entry.FullName), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return names.Where(name =>
        {
            var expected = template.GetEntry(name);
            var actual = output.GetEntry(name);
            return expected is null
                || actual is null
                || !ReadBytes(expected).SequenceEqual(ReadBytes(actual));
        }).ToArray();
    }

    private static byte[] ReadBytes(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static void AssertInfStylesPreserveSemanticsAndNormalizeFontFamily(
        string templatePath,
        string outputPath)
    {
        using var template = ZipFile.OpenRead(templatePath);
        using var output = ZipFile.OpenRead(outputPath);
        var templateStyles = ReadXml(template.GetEntry("xl/styles.xml")
            ?? throw new InvalidDataException("INF template has no styles part."));
        var outputStyles = ReadXml(output.GetEntry("xl/styles.xml")
            ?? throw new InvalidDataException("INF output has no styles part."));
        XNamespace spreadsheet =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var templateFormats = templateStyles.Root!
            .Element(spreadsheet + "cellXfs")!
            .Elements(spreadsheet + "xf")
            .ToArray();
        var outputFormats = outputStyles.Root!
            .Element(spreadsheet + "cellXfs")!
            .Elements(spreadsheet + "xf")
            .ToArray();

        Assert.True(outputFormats.Length > templateFormats.Length);
        for (var index = 0; index < templateFormats.Length; index++)
        {
            Assert.True(XNode.DeepEquals(templateFormats[index], outputFormats[index]));
        }
        var templateFormatShapes = templateFormats
            .Select(NumberFormatAgnosticCellFormat)
            .ToHashSet(StringComparer.Ordinal);
        var outputNumberFormats = outputStyles.Root
            .Element(spreadsheet + "numFmts")?
            .Elements(spreadsheet + "numFmt")
            .ToDictionary(
                format => (string?)format.Attribute("numFmtId") ?? string.Empty,
                format => (string?)format.Attribute("formatCode") ?? string.Empty,
                StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        Assert.All(outputFormats.Skip(templateFormats.Length), format =>
        {
            Assert.Equal("1", (string?)format.Attribute("applyNumberFormat"));
            Assert.Contains(NumberFormatAgnosticCellFormat(format), templateFormatShapes);

            var numberFormatId = (string?)format.Attribute("numFmtId");
            Assert.False(string.IsNullOrWhiteSpace(numberFormatId));
            if (string.Equals(numberFormatId, "49", StringComparison.Ordinal))
            {
                return;
            }

            Assert.True(outputNumberFormats.TryGetValue(numberFormatId!, out var code));
            Assert.True(IsInfFieldNumberFormat(code));
        });

        var templateRemainder = templateStyles.Root.Elements()
            .Where(element => element.Name != spreadsheet + "cellXfs"
                              && element.Name != spreadsheet + "numFmts"
                              && element.Name != spreadsheet + "fonts")
            .ToArray();
        var outputRemainder = outputStyles.Root.Elements()
            .Where(element => element.Name != spreadsheet + "cellXfs"
                              && element.Name != spreadsheet + "numFmts"
                              && element.Name != spreadsheet + "fonts")
            .ToArray();
        Assert.Equal(
            templateRemainder.Select(element => element.Name),
            outputRemainder.Select(element => element.Name));
        for (var index = 0; index < templateRemainder.Length; index++)
        {
            Assert.Equal(
                Canonical(templateRemainder[index]),
                Canonical(outputRemainder[index]));
        }

        var templateFonts = templateStyles.Root.Element(spreadsheet + "fonts")!
            .Elements(spreadsheet + "font").ToArray();
        var outputFonts = outputStyles.Root.Element(spreadsheet + "fonts")!
            .Elements(spreadsheet + "font").ToArray();
        Assert.Equal(templateFonts.Length, outputFonts.Length);
        for (var index = 0; index < templateFonts.Length; index++)
        {
            Assert.Equal(
                CanonicalFontWithoutFamily(templateFonts[index], spreadsheet),
                CanonicalFontWithoutFamily(outputFonts[index], spreadsheet));
        }
        Assert.All(outputFonts, font =>
        {
            Assert.Equal(
                "微軟正黑體",
                (string?)font.Element(spreadsheet + "name")?.Attribute("val"));
            Assert.Null(font.Element(spreadsheet + "scheme"));
        });
    }

    private static string CanonicalFontWithoutFamily(XElement font, XNamespace spreadsheet)
    {
        var copy = new XElement(font);
        copy.Element(spreadsheet + "name")?.Remove();
        copy.Element(spreadsheet + "scheme")?.Remove();
        return Canonical(copy);
    }

    private static string NumberFormatAgnosticCellFormat(XElement source)
    {
        var copy = new XElement(source);
        copy.Attribute("numFmtId")?.Remove();
        copy.Attribute("applyNumberFormat")?.Remove();
        return Canonical(copy);
    }

    private static bool IsInfFieldNumberFormat(string? code)
    {
        if (code is "mm-dd-yy" or "h:mm:ss" or "0")
        {
            return true;
        }
        return code is not null
               && code.StartsWith("0.", StringComparison.Ordinal)
               && code.Length is >= 3 and <= 30
               && code.AsSpan(2).IndexOfAnyExcept('0') < 0;
    }

    private static XDocument ReadXml(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        return XDocument.Load(input);
    }

    private static string Canonical(XElement element) =>
        CanonicalElement(element).ToString(System.Xml.Linq.SaveOptions.DisableFormatting);

    private static XElement CanonicalElement(XElement element) =>
        new(
            element.Name,
            element.Attributes()
                .Where(attribute => !attribute.IsNamespaceDeclaration)
                .OrderBy(attribute => attribute.Name.NamespaceName, StringComparer.Ordinal)
                .ThenBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal)
                .Select(attribute => new XAttribute(attribute.Name, attribute.Value)),
            element.Nodes().Select<XNode, XNode>(node => node switch
            {
                XElement child => CanonicalElement(child),
                XCData data => new XCData(data.Value),
                XText text => new XText(text.Value),
                XComment comment => new XComment(comment.Value),
                XProcessingInstruction instruction =>
                    new XProcessingInstruction(instruction.Target, instruction.Data),
                _ => throw new InvalidDataException(
                    $"Unsupported XML node type '{node.NodeType}' in INF package oracle.")
            }));

    private static Worksheet WorksheetFor(SpreadsheetDocument document, string name)
    {
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("Workbook part is missing.");
        var sheet = workbookPart.Workbook.Sheets!.Elements<Sheet>()
            .Single(item => item.Name?.Value == name);
        return ((WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!)).Worksheet;
    }

    private static Cell CellFor(Worksheet worksheet, string reference) =>
        worksheet.Descendants<Cell>().Single(cell => cell.CellReference?.Value == reference);

    private static CellFormat CellFormatFor(SpreadsheetDocument document, Cell cell) =>
        document.WorkbookPart?.WorkbookStylesPart?.Stylesheet.CellFormats?
            .Elements<CellFormat>()
            .ElementAt(checked((int)(cell.StyleIndex?.Value ?? 0U)))
        ?? throw new InvalidDataException("Workbook cell format is missing.");

    private static Column ColumnFor(Worksheet worksheet, uint index) =>
        worksheet.GetFirstChild<Columns>()!.Elements<Column>().Single(column =>
            column.Min?.Value <= index && column.Max?.Value >= index);

    private static Row RowFor(Worksheet worksheet, uint index) =>
        worksheet.GetFirstChild<SheetData>()!.Elements<Row>().Single(row => row.RowIndex?.Value == index);

    private static (double? Width, uint? Style, bool? Hidden, bool? BestFit, bool? CustomWidth)
        ColumnLayout(Column column) =>
        (
            column.Width?.Value,
            column.Style?.Value,
            column.Hidden?.Value,
            column.BestFit?.Value,
            column.CustomWidth?.Value
        );

    private static IEnumerable<string> StaticCellReferences()
    {
        for (var row = 53; row <= 111; row++)
        {
            yield return $"A{row}";
            for (var column = 'M'; column <= 'T'; column++)
            {
                yield return $"{column}{row}";
            }
        }
    }

    private static IEnumerable<string> HeaderCellReferences()
    {
        for (var row = 49; row <= 52; row++)
        {
            for (var column = 'A'; column <= 'T'; column++)
            {
                yield return $"{column}{row}";
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "src",
                    "JET",
                    "JET",
                    "Templates",
                    "INFReport.xlsx")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Could not locate the JET repository root.");
    }
}
