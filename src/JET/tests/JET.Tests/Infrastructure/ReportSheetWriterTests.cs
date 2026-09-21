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
            using var writer = new ReportSheetWriter("Default", part);
            writer.WriteFixedRow(1, [new Cell { CellReference = "A1" }], 18);
            writer.CloseAndSummarize(default);
        }
        stream.Position = 0;
        using var read = SpreadsheetDocument.Open(stream, false);
        var sheet = read.WorkbookPart!.WorksheetParts.Single().Worksheet;
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
        var worksheet = Assert.IsType<WorksheetPart>(read.WorkbookPart!.WorksheetParts.Single()).Worksheet;
        Assert.Equal(
            new[] { "sheetViews", "sheetFormatPr", "cols", "sheetData", "pageMargins", "pageSetup" },
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
}
