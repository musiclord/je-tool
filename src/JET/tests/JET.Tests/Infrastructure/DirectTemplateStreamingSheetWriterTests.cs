using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class DirectTemplateStreamingSheetWriterTests
{
    /// <summary>
    /// 工作表的元素順序規定 sheetCalcPr 在 sheetProtection 之前。範本在 sheetData 之後有 sheetCalcPr 時，
    /// 補上的保護要排在 sheetCalcPr 後面，範本其餘元素照原來的順序接在保護後面。
    /// </summary>
    [Fact]
    public void EnsureProtection_TemplateWithSheetCalcPr_WritesProtectionAfterIt()
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var part = workbookPart.AddNewPart<WorksheetPart>();
            part.Worksheet = new Worksheet(
                new SheetData(new Row(new Cell { CellReference = "A1" }) { RowIndex = 1 }),
                new SheetCalculationProperties { FullCalculationOnLoad = true },
                new PageMargins { Left = 0.7, Right = 0.7, Top = 0.75, Bottom = 0.75, Header = 0.3, Footer = 0.3 });
            part.Worksheet.Save();
            workbookPart.Workbook.AppendChild(new Sheets()).Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(part),
                SheetId = 1,
                Name = "合成"
            });

            using var writer = new DirectTemplateStreamingSheetWriter("合成", part, firstDynamicRow: 2, ensureProtection: true);
            writer.WriteDataRow(2, [new Cell { CellReference = "A2" }]);
            writer.CloseAndSummarize(CancellationToken.None);
        }

        stream.Position = 0;
        using var read = SpreadsheetDocument.Open(stream, false);
        var worksheet = read.WorkbookPart!.WorksheetParts.Single().RequiredWorksheet();
        Assert.Equal(
            ["sheetData", "sheetCalcPr", "sheetProtection", "pageMargins"],
            worksheet.ChildElements.Select(element => element.LocalName));
        Assert.Empty(new OpenXmlValidator().Validate(read));
    }
}
