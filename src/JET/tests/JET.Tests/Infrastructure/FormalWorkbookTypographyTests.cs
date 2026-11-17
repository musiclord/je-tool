using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class FormalWorkbookTypographyTests
{
    /// <summary>
    /// Excel 一列最高 409 點。11 點字每行 16.5 點：24 行是 396 點，照行數加高；25 行算起來 412.5 點，超過上限，寫成 409 點。
    /// </summary>
    [Theory]
    [InlineData(11D, 24, 396D)]
    [InlineData(11D, 25, 409D)]
    [InlineData(10D, 41, 409D)]
    public void RowHeight_ManyWrappedLines_StopsAtExcelRowHeightLimit(double fontSize, int lines, double expected)
    {
        Assert.Equal(expected, FormalWorkbookTypography.RowHeight(fontSize, lines));
    }

    /// <summary>
    /// 統一字型改的是樣式表與共用字串表裡的字型，明細工作表多大都不用載入記憶體：
    /// 明細列數多十倍時，統一字型配置的記憶體不跟著變多。合成活頁簿的樣式表用 Arial 與佈景主題字型，
    /// 共用字串表有一段指定新細明體的格式文字，明細工作表是一般文字。
    /// </summary>
    [Fact]
    public void Normalize_UnifiesStyleAndSharedStringFonts_WithoutLoadingDetailSheets()
    {
        using var root = new TempProjectRoot();
        Directory.CreateDirectory(root.Path);
        var small = NormalizeAndMeasure(Path.Combine(root.Path, "small.xlsx"), rows: 2_000);
        var large = NormalizeAndMeasure(Path.Combine(root.Path, "large.xlsx"), rows: 20_000);

        Assert.True(
            large <= small * 2,
            $"明細列數多十倍時，統一字型配置的記憶體不應超過兩倍；實際是 {small:N0} 與 {large:N0} 位元組。");
    }

    private static long NormalizeAndMeasure(string path, int rows)
    {
        CreateWorkbook(path, rows);

        long allocated;
        using (var document = SpreadsheetDocument.Open(path, true, new OpenSettings { AutoSave = false }))
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            FormalWorkbookTypography.Normalize(document);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        using (var document = SpreadsheetDocument.Open(path, false))
        {
            var workbookPart = document.WorkbookPart!;
            var stylesheet = workbookPart.WorkbookStylesPart!.Stylesheet!;
            Assert.All(
                stylesheet.Descendants<Font>(),
                font => Assert.Equal(FormalWorkbookTypography.CanonicalFontName, font.FontName?.Val?.Value));
            Assert.Empty(stylesheet.Descendants<FontScheme>());
            var runFont = Assert.Single(workbookPart.SharedStringTablePart!.SharedStringTable!.Descendants<RunFont>());
            Assert.Equal(FormalWorkbookTypography.CanonicalFontName, runFont.Val?.Value);
        }

        return allocated;
    }

    private static void CreateWorkbook(string path, int rows)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook(new Sheets());

        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = new Stylesheet(
            new Fonts(new Font(
                new FontSize { Val = 10D },
                new FontName { Val = "Arial" },
                new FontScheme { Val = FontSchemeValues.Minor }))
            { Count = 1U },
            new Fills(new Fill(new PatternFill { PatternType = PatternValues.None })) { Count = 1U },
            new Borders(new Border()) { Count = 1U },
            new CellFormats(new CellFormat()) { Count = 1U });
        stylesPart.Stylesheet.Save();

        var sharedStringsPart = workbookPart.AddNewPart<SharedStringTablePart>();
        sharedStringsPart.SharedStringTable = new SharedStringTable(
            new SharedStringItem(new Run(
                new RunProperties(new RunFont { Val = "新細明體" }),
                new Text("範本說明"))))
        { Count = 1U, UniqueCount = 1U };
        sharedStringsPart.SharedStringTable.Save();

        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        using (var writer = OpenXmlWriter.Create(worksheetPart))
        {
            writer.WriteStartElement(new Worksheet());
            writer.WriteStartElement(new SheetData());
            for (var row = 1; row <= rows; row++)
            {
                writer.WriteStartElement(new Row { RowIndex = (uint)row });
                for (var column = 1; column <= 10; column++)
                {
                    writer.WriteElement(new Cell
                    {
                        DataType = CellValues.InlineString,
                        InlineString = new InlineString(new Text($"合成明細 {row}-{column}"))
                    });
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        workbookPart.Workbook.Sheets!.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = "明細"
        });
        workbookPart.Workbook.Save();
    }
}
