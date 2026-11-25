using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class ReportSheetWriterTests
{
    [Fact]
    public void DefaultSheet_HasWorkbookViewReference_ForNativeExcelDpiCompatibility()
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var book = document.AddWorkbookPart();
            book.Workbook = new Workbook();
            var part = book.AddNewPart<WorksheetPart>();
            using var writer = new ReportSheetWriter("Default", part, SheetUsedRange.Of(1, 1, 1, 1));
            writer.WriteFixedRow(1, [new Cell { CellReference = "A1" }], 18);
            writer.CloseAndSummarize(default);
        }
        stream.Position = 0;
        using var read = SpreadsheetDocument.Open(stream, false);
        var sheet = read.WorkbookPart!.WorksheetParts.Single().RequiredWorksheet();
        var view = Assert.Single(sheet.Descendants<SheetView>());
        Assert.Equal(0U, view.WorkbookViewId?.Value);
        Assert.Null(view.ZoomScale);
        Assert.Null(view.ShowGridLines);
    }
    [Fact]
    public void LegacyContinuationNames_KeepFirstNameAndUseNumberedSuffixWithinExcelLimit()
    {
        Assert.Equal("V_Report 5", LegacyReportWriter.SeriesName("V_Report 5", 1));
        Assert.Equal("V_Report 5 (2)", LegacyReportWriter.SeriesName("V_Report 5", 2));
        Assert.InRange(
            LegacyReportWriter.SeriesName("1234567890123456789012345678901", 12).Length,
            1,
            ExcelWorksheetConstraints.MaxSheetNameLength);
    }

    [Fact]
    public void LayoutOptions_WriteSchemaOrderedViewFreezeColumnsRowsAndPrintSettings()
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Layout"
            });

            using var writer = new ReportSheetWriter(
                "Layout",
                worksheetPart,
                SheetUsedRange.Of(1, 1, 1, 1),
                [new ReportColumn(1, 2, 18, BestFit: true)],
                new ReportSheetOptions(
                    ShowGridLines: false,
                    ZoomScale: 85,
                    ZoomScaleNormal: 85,
                    FreezeRows: 2,
                    FreezeColumns: 1,
                    DefaultRowHeight: 16,
                    PageMargins: new ReportPageMargins(0.7, 0.7, 0.75, 0.75, 0.3, 0.3),
                    PageSetup: new ReportPageSetup(
                        OrientationValues.Landscape,
                        PaperSize: 9,
                        Scale: 61,
                        FitToHeight: 0)));
            writer.WriteFixedRow(1, [new Cell { CellReference = "A1" }], 24);
            writer.CloseAndSummarize(CancellationToken.None);
            workbookPart.Workbook.Save();
        }

        stream.Position = 0;
        using var read = SpreadsheetDocument.Open(stream, false);
        var worksheet = Assert.IsType<WorksheetPart>(read.WorkbookPart!.WorksheetParts.Single()).RequiredWorksheet();
        Assert.Equal(
            new[] { "dimension", "sheetViews", "sheetFormatPr", "cols", "sheetData", "pageMargins", "pageSetup" },
            worksheet.ChildElements.Select(element => element.LocalName).ToArray());

        var view = worksheet.Descendants<SheetView>().Single();
        Assert.False(view.ShowGridLines?.Value ?? true);
        Assert.Equal(85U, view.ZoomScale?.Value);
        var pane = Assert.Single(view.Elements<Pane>());
        Assert.Equal(1D, pane.HorizontalSplit?.Value);
        Assert.Equal(2D, pane.VerticalSplit?.Value);
        Assert.Equal("B3", pane.TopLeftCell?.Value);
        Assert.Equal(PaneValues.BottomRight, pane.ActivePane?.Value);
        Assert.Equal(PaneStateValues.Frozen, pane.State?.Value);

        Assert.Equal(24D, worksheet.GetFirstChild<SheetData>()!.Elements<Row>().Single().Height?.Value);
        Assert.True(worksheet.GetFirstChild<Columns>()!.Elements<Column>().Single().BestFit?.Value ?? false);
        var setup = Assert.Single(worksheet.Elements<PageSetup>());
        Assert.Equal(OrientationValues.Landscape, setup.Orientation?.Value);
        Assert.Equal(9U, setup.PaperSize?.Value);
        Assert.Equal(61U, setup.Scale?.Value);
        Assert.Equal(0U, setup.FitToHeight?.Value);
        Assert.Empty(new OpenXmlValidator().Validate(read));
    }

    /// <summary>
    /// 開頁時宣告的範圍寫成 dimension，排在 sheetViews 前面，和寫出的格子範圍相同。
    /// 一格也沒有寫的工作表寫 A1，和 Excel 存檔時相同。
    /// </summary>
    [Theory]
    [InlineData("", "", "A1")]
    [InlineData("A1", "A1", "A1")]
    [InlineData("A1:I3", "A1 B1 C1 D1 E1 F1 G1 H1 I1 A2 B2 C2 D2 E2 F2 G2 H2 I2 A3 B3 C3 D3 E3 F3 G3 H3 I3", "A1:I3")]
    [InlineData("B5:C6", "B5 C5 B6 C6", "B5:C6")]
    public void DeclaredRange_IsWrittenAsDimensionBeforeSheetViews(string declared, string written, string expected)
    {
        var package = WriteSheet(Range(declared), written);

        using var read = SpreadsheetDocument.Open(new MemoryStream(package, writable: false), false);
        var worksheet = read.WorkbookPart!.WorksheetParts.Single().RequiredWorksheet();
        var dimension = Assert.IsType<SheetDimension>(worksheet.ChildElements[0]);
        Assert.Equal(expected, dimension.Reference?.Value);
        Assert.Equal("sheetViews", worksheet.ChildElements[1].LocalName);
        WorksheetDimensionAssert.EveryWorksheetMatchesWrittenCells(package);
        Assert.Empty(new OpenXmlValidator().Validate(read));
    }

    /// <summary>
    /// 開頁時宣告的範圍和實際寫出的格子不同，是程式算錯了範圍，不是使用者能處理的情形，所以丟出程式錯誤，不寫出 dimension 不對的檔案。
    /// 格子落在宣告的範圍外時，寫那一列就失敗；宣告的範圍比寫出的大時，關閉工作表時才知道，在關閉時失敗。
    /// </summary>
    [Theory]
    [InlineData("A1:B2", "A1 B1 A2 B2 C3", true)]
    [InlineData("", "A1", true)]
    [InlineData("B2:C3", "A1", true)]
    [InlineData("A1:C3", "A1 B2", false)]
    [InlineData("A1", "", false)]
    [InlineData("A1:E1", "A1 B1 C1 D1", false)]
    public void DeclaredRangeDifferentFromWrittenCells_FailsWithAProgrammingError(
        string declared,
        string written,
        bool failsWhileWriting)
    {
        using var stream = new MemoryStream();
        using var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var part = workbookPart.AddNewPart<WorksheetPart>();
        var closing = false;

        var error = Record.Exception(() =>
        {
            using var writer = new ReportSheetWriter("合成", part, Range(declared));
            foreach (var (row, cells) in Rows(written))
            {
                writer.WriteFixedRow(row, cells);
            }

            closing = true;
            writer.CloseAndSummarize(CancellationToken.None);
        });

        var failure = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("合成", failure.Message, StringComparison.Ordinal);
        Assert.Equal(failsWhileWriting, !closing);
    }

    /// <summary>
    /// 表頭在第 1 列、資料從第 2 列起的明細，每一頁的範圍是 A1 到這一頁最後一列的最後一欄。
    /// 資料寫到列數上限就換頁，所以每頁最多「上限減 1」列資料，沒有資料時只有表頭。
    /// V_Report 5 用 Excel 的列數上限換頁，一頁最多 1,048,575 列資料。
    /// </summary>
    [Theory]
    [InlineData(0L, 1, 5U, 17U, "A1:E1")]
    [InlineData(1L, 1, 5U, 17U, "A1:E2")]
    [InlineData(16L, 1, 5U, 17U, "A1:E17")]
    [InlineData(17L, 1, 5U, 17U, "A1:E17")]
    [InlineData(17L, 2, 5U, 17U, "A1:E2")]
    [InlineData(40L, 2, 5U, 17U, "A1:E17")]
    [InlineData(40L, 3, 5U, 17U, "A1:E9")]
    [InlineData(0L, 1, 1U, 17U, "A1")]
    [InlineData(17L, 1, 5U, ExcelWorksheetConstraints.MaxRows, "A1:E18")]
    [InlineData(1_048_575L, 1, 5U, ExcelWorksheetConstraints.MaxRows, "A1:E1048576")]
    [InlineData(1_048_576L, 1, 5U, ExcelWorksheetConstraints.MaxRows, "A1:E1048576")]
    [InlineData(1_048_576L, 2, 5U, ExcelWorksheetConstraints.MaxRows, "A1:E2")]
    public void PagedSheetUsedRange_CoversTheHeaderAndThisPagesRows(
        long totalRows,
        int part,
        uint columnCount,
        uint rowLimit,
        string expected) =>
        Assert.Equal(expected, LegacyReportWriter.PagedSheetUsedRange(totalRows, part, columnCount, rowLimit).Reference);

    /// <summary>換頁只在還有下一列資料時發生，所以資料剛好寫滿一頁時沒有下一頁；問沒有的頁是呼叫端算錯了。</summary>
    [Theory]
    [InlineData(0L, 2, 17U)]
    [InlineData(16L, 2, 17U)]
    [InlineData(17L, 3, 17U)]
    [InlineData(1_048_575L, 2, ExcelWorksheetConstraints.MaxRows)]
    [InlineData(5L, 0, 17U)]
    [InlineData(-1L, 1, 17U)]
    public void PagedSheetUsedRange_PageWithoutRows_Throws(long totalRows, int part, uint rowLimit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LegacyReportWriter.PagedSheetUsedRange(totalRows, part, 5, rowLimit));

    /// <summary>空字串表示沒有任何格子；其他寫法和 dimension 相同，例如 A1 或 A1:B2。</summary>
    internal static SheetUsedRange Range(string reference)
    {
        if (reference.Length == 0)
        {
            return SheetUsedRange.Empty;
        }

        var corners = reference.Split(':');
        var (firstRow, firstColumn) = SheetUsedRange.ParseCellReference(corners[0]);
        var (lastRow, lastColumn) = SheetUsedRange.ParseCellReference(corners[^1]);
        return SheetUsedRange.Of(firstRow, firstColumn, lastRow, lastColumn);
    }

    /// <summary>以空白分隔的格子位置，依列分組、由上往下排；每一格都是只有位置的空白格。</summary>
    private static IEnumerable<(uint Row, Cell[] Cells)> Rows(string cells) =>
        cells.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(reference => (Reference: reference, Position: SheetUsedRange.ParseCellReference(reference)))
            .GroupBy(cell => cell.Position.Row)
            .OrderBy(group => group.Key)
            .Select(group => (group.Key, group
                .OrderBy(cell => cell.Position.Column)
                .Select(cell => new Cell { CellReference = cell.Reference })
                .ToArray()));

    private static byte[] WriteSheet(SheetUsedRange declared, string written)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var part = workbookPart.AddNewPart<WorksheetPart>();
            workbookPart.Workbook.AppendChild(new Sheets()).Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(part),
                SheetId = 1,
                Name = "合成"
            });

            using var writer = new ReportSheetWriter("合成", part, declared);
            foreach (var (row, cells) in Rows(written))
            {
                writer.WriteFixedRow(row, cells);
            }

            writer.CloseAndSummarize(CancellationToken.None);
            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }
}
