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

    /// <summary>
    /// 寫完資料列、關閉工作表時，輸出檔才寫不進去：外層照例在 finally 再 Dispose 一次，
    /// 往外丟的仍是寫不進去的那個錯誤，不換成「寫入器已經在錯誤狀態」之類的新例外。
    /// 合成範本在 sheetData 之後有大量合併儲存格，關閉時要寫出的內容超過壓縮與 XML 的緩衝，寫出一定會碰到輸出檔。
    /// </summary>
    [Fact]
    public void OutputFailsWhileClosing_DisposingAgainKeepsTheWriteError()
    {
        using var output = new FailingOutputStream();
        using var stream = new MemoryStream();
        using var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var part = workbookPart.AddNewPart<WorksheetPart>();
        var merges = new MergeCells(Enumerable.Range(1, 20_000)
            .Select(row => new MergeCell { Reference = $"A{row}:C{row}" }));
        part.Worksheet = new Worksheet(new SheetData(), merges);
        part.Worksheet.Save();
        new FormalWorkbookArchive(output).Attach(document);

        var writer = new DirectTemplateStreamingSheetWriter("合成", part, firstDynamicRow: 1);
        writer.WriteDataRow(1, [new Cell { CellReference = "A1" }]);
        output.FailFromNowOn();

        var escaped = Record.Exception(() =>
        {
            try
            {
                writer.CloseAndSummarize(CancellationToken.None);
            }
            finally
            {
                writer.Dispose();
            }
        });

        Assert.True(output.Failures > 0, "關閉工作表時沒有寫到輸出檔，測不到這個情形。");
        Assert.IsType<IOException>(escaped);
        Assert.Equal(FailingOutputStream.Message, escaped.Message);
    }

    /// <summary>可以定位的輸出；要求失敗之後，每一次寫入都丟出同一個讀寫錯誤。</summary>
    private sealed class FailingOutputStream : MemoryStream
    {
        internal const string Message = "合成的輸出檔寫入失敗";

        private bool _failing;

        public int Failures { get; private set; }

        public void FailFromNowOn() => _failing = true;

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfFailing();
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ThrowIfFailing();
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            ThrowIfFailing();
            base.WriteByte(value);
        }

        private void ThrowIfFailing()
        {
            if (_failing)
            {
                Failures++;
                throw new IOException(Message);
            }
        }
    }
}
