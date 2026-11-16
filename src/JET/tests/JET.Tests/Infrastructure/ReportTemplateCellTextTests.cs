using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 報告寫出程式讀範本儲存格的文字。範本缺了要讀的儲存格或共用字串時，丟出的是「範本內容有問題」的例外，
/// 匯出才會回報無法填入內附的範本，不會變成沒有說明的程式錯誤。
/// </summary>
public sealed class ReportTemplateCellTextTests
{
    [Fact]
    public void ReadsInlineStringsSharedStringsAndPlainValues()
    {
        WithWorksheet(
            (data, shared) =>
            {
                shared.AppendChild(new SharedStringItem(new Text("條件的內容")));
                data.AppendChild(new Row(
                    new Cell { CellReference = "A4", DataType = CellValues.InlineString, InlineString = new InlineString(new Text("編號")) },
                    new Cell { CellReference = "B4", DataType = CellValues.SharedString, CellValue = new CellValue("0") },
                    new Cell { CellReference = "C4", CellValue = new CellValue("42") }));
            },
            part =>
            {
                Assert.Equal("編號", LegacyReportWriter.ReadTemplateCellText(part, "A4"));
                Assert.Equal("條件的內容", LegacyReportWriter.ReadTemplateCellText(part, "B4"));
                Assert.Equal("42", LegacyReportWriter.ReadTemplateCellText(part, "C4"));
            });
    }

    [Fact]
    public void MissingCell_ThrowsInvalidDataNamingTheCell()
    {
        WithWorksheet(
            (data, _) => data.AppendChild(new Row(
                new Cell { CellReference = "A4", DataType = CellValues.InlineString, InlineString = new InlineString(new Text("編號")) })),
            part =>
            {
                var exception = Assert.Throws<InvalidDataException>(() => LegacyReportWriter.ReadTemplateCellText(part, "B4"));
                Assert.Contains("B4", exception.Message);
            });
    }

    [Fact]
    public void SharedStringIndexOutsideTheTable_ThrowsInvalidDataNamingTheCell()
    {
        WithWorksheet(
            (data, shared) =>
            {
                shared.AppendChild(new SharedStringItem(new Text("條件的內容")));
                data.AppendChild(new Row(
                    new Cell { CellReference = "B4", DataType = CellValues.SharedString, CellValue = new CellValue("7") }));
            },
            part =>
            {
                var exception = Assert.Throws<InvalidDataException>(() => LegacyReportWriter.ReadTemplateCellText(part, "B4"));
                Assert.Contains("B4", exception.Message);
            });
    }

    [Fact]
    public void SharedStringCellInWorkbookWithoutSharedStringTable_ThrowsInvalidDataNamingTheCell()
    {
        using var stream = new MemoryStream();
        using var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook(new Sheets());
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(new SheetData(new Row(
            new Cell { CellReference = "B4", DataType = CellValues.SharedString, CellValue = new CellValue("0") })));

        var exception = Assert.Throws<InvalidDataException>(() => LegacyReportWriter.ReadTemplateCellText(worksheetPart, "B4"));
        Assert.Contains("B4", exception.Message);
    }

    [Fact]
    public void SharedStringCellWhoseValueIsNotAnIndex_ThrowsInvalidDataNamingTheCell()
    {
        WithWorksheet(
            (data, shared) =>
            {
                shared.AppendChild(new SharedStringItem(new Text("條件的內容")));
                data.AppendChild(new Row(
                    new Cell { CellReference = "B4", DataType = CellValues.SharedString, CellValue = new CellValue("條件的內容") }));
            },
            part =>
            {
                var exception = Assert.Throws<InvalidDataException>(() => LegacyReportWriter.ReadTemplateCellText(part, "B4"));
                Assert.Contains("B4", exception.Message);
            });
    }

    private static void WithWorksheet(Action<SheetData, SharedStringTable> build, Action<WorksheetPart> check)
    {
        using var stream = new MemoryStream();
        using var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook(new Sheets());
        var sharedStrings = workbookPart.AddNewPart<SharedStringTablePart>();
        sharedStrings.SharedStringTable = new SharedStringTable();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        worksheetPart.Worksheet = new Worksheet(data);

        build(data, sharedStrings.SharedStringTable);
        check(worksheetPart);
    }
}
