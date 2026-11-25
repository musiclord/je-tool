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

            using var writer = new DirectTemplateStreamingSheetWriter(
                "合成", part, firstDynamicRow: 2, SheetUsedRange.Of(2, 1, 2, 1), ensureProtection: true);
            writer.WriteDataRow(2, [new Cell { CellReference = "A2" }]);
            writer.CloseAndSummarize(CancellationToken.None);
        }

        stream.Position = 0;
        using var read = SpreadsheetDocument.Open(stream, false);
        var worksheet = read.WorkbookPart!.WorksheetParts.Single().RequiredWorksheet();
        Assert.Equal(
            ["dimension", "sheetData", "sheetCalcPr", "sheetProtection", "pageMargins"],
            worksheet.ChildElements.Select(element => element.LocalName));
        Assert.Empty(new OpenXmlValidator().Validate(read));
    }

    /// <summary>
    /// 範本固定列的格子加上開頁時宣告的資料列範圍，就是這張工作表的 dimension。範本原有的 dimension 換成新的範圍，
    /// 位置照範本，在 sheetPr 之後、sheetViews 之前；範本留下的舊範圍不沿用。
    /// </summary>
    [Fact]
    public void TemplateRowsAndDeclaredDataRows_AreWrittenAsOneDimension()
    {
        var package = WriteTemplateSheet(
            SheetUsedRange.Of(17, 1, 18, 8),
            [Row(17, "A", "B", "C", "D", "E", "F", "G", "H"), Row(18, "A", "B", "C", "D", "E", "F", "G", "H")]);

        using var read = SpreadsheetDocument.Open(new MemoryStream(package, writable: false), false);
        var worksheet = read.WorkbookPart!.WorksheetParts.Single().RequiredWorksheet();
        Assert.Equal(
            ["sheetPr", "dimension", "sheetViews", "sheetData", "pageMargins"],
            worksheet.ChildElements.Select(element => element.LocalName));
        Assert.Equal("A1:H18", Assert.Single(worksheet.Elements<SheetDimension>()).Reference?.Value);
        WorksheetDimensionAssert.EveryWorksheetMatchesWrittenCells(package);
        Assert.Empty(new OpenXmlValidator().Validate(read));
    }

    /// <summary>沒有資料列時，範圍只有範本的固定列。</summary>
    [Fact]
    public void NoDataRows_DimensionCoversOnlyTheTemplateRows()
    {
        var package = WriteTemplateSheet(SheetUsedRange.Empty, []);

        using var read = SpreadsheetDocument.Open(new MemoryStream(package, writable: false), false);
        var worksheet = read.WorkbookPart!.WorksheetParts.Single().RequiredWorksheet();
        Assert.Equal("A1:G16", Assert.Single(worksheet.Elements<SheetDimension>()).Reference?.Value);
        WorksheetDimensionAssert.EveryWorksheetMatchesWrittenCells(package);
    }

    /// <summary>
    /// 宣告的資料列範圍和實際寫出的格子不同，是程式算錯了範圍，丟出程式錯誤。
    /// 格子落在範圍外時寫那一列就失敗；宣告的列沒有寫完時，關閉工作表時失敗。
    /// </summary>
    [Theory]
    [InlineData(18U, 8U, 17U, "A B C D E F G H I", true)]
    [InlineData(18U, 8U, 19U, "A", true)]
    [InlineData(18U, 8U, 17U, "A B C D E F G H", false)]
    public void DeclaredRangeDifferentFromWrittenCells_FailsWithAProgrammingError(
        uint declaredLastRow,
        uint declaredLastColumn,
        uint writtenRow,
        string writtenColumns,
        bool failsWhileWriting)
    {
        using var stream = new MemoryStream();
        using var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook);
        var part = AddTemplateSheet(document);
        var closing = false;

        var error = Record.Exception(() =>
        {
            using var writer = new DirectTemplateStreamingSheetWriter(
                "合成", part, firstDynamicRow: 17, SheetUsedRange.Of(17, 1, declaredLastRow, declaredLastColumn));
            var (row, cells) = Row(writtenRow, writtenColumns.Split(' '));
            writer.WriteDataRow(row, cells);
            closing = true;
            writer.CloseAndSummarize(CancellationToken.None);
        });

        var failure = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("合成", failure.Message, StringComparison.Ordinal);
        Assert.Equal(failsWhileWriting, !closing);
    }

    /// <summary>資料列的範圍只能從第一筆資料列起往下，蓋到範本固定列的範圍是呼叫端算錯了。</summary>
    [Fact]
    public void DeclaredDataRangeAboveTheFirstDataRow_Throws()
    {
        using var stream = new MemoryStream();
        using var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook);
        var part = AddTemplateSheet(document);

        Assert.Throws<ArgumentOutOfRangeException>(() => new DirectTemplateStreamingSheetWriter(
            "合成", part, firstDynamicRow: 17, SheetUsedRange.Of(16, 1, 17, 8)));
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

        var writer = new DirectTemplateStreamingSheetWriter("合成", part, firstDynamicRow: 1, SheetUsedRange.Of(1, 1, 1, 1));
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

    /// <summary>用合成範本從第 17 列起照宣告的範圍寫資料列，回傳輸出的活頁簿。</summary>
    private static byte[] WriteTemplateSheet(SheetUsedRange dataRange, IReadOnlyList<(uint Row, Cell[] Cells)> rows)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var part = AddTemplateSheet(document);
            using var writer = new DirectTemplateStreamingSheetWriter("合成", part, firstDynamicRow: 17, dataRange);
            foreach (var (row, cells) in rows)
            {
                writer.WriteDataRow(row, cells);
            }

            writer.CloseAndSummarize(CancellationToken.None);
            document.WorkbookPart!.Workbook!.Save();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// 合成範本：第 1 列有 A1 與 G1、第 16 列有 C16，所以固定列的格子範圍是 A1:G16；範本留著不對的舊 dimension A1:B2。
    /// </summary>
    private static WorksheetPart AddTemplateSheet(SpreadsheetDocument document)
    {
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var part = workbookPart.AddNewPart<WorksheetPart>();
        part.Worksheet = new Worksheet(
            new SheetProperties(),
            new SheetDimension { Reference = "A1:B2" },
            new SheetViews(new SheetView { WorkbookViewId = 0U }),
            new SheetData(
                new Row(new Cell { CellReference = "A1" }, new Cell { CellReference = "G1" }) { RowIndex = 1 },
                new Row(new Cell { CellReference = "C16" }) { RowIndex = 16 }),
            new PageMargins { Left = 0.7, Right = 0.7, Top = 0.75, Bottom = 0.75, Header = 0.3, Footer = 0.3 });
        part.Worksheet.Save();
        workbookPart.Workbook.AppendChild(new Sheets()).Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(part),
            SheetId = 1,
            Name = "合成"
        });
        return part;
    }

    private static (uint Row, Cell[] Cells) Row(uint row, params string[] columns) =>
        (row, columns.Select(column => new Cell { CellReference = column + row }).ToArray());

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
