using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 正式輸出的明細工作表一邊寫一邊壓縮進輸出檔，不先整張留在記憶體、等活頁簿關閉時才寫出。
/// 這裡用條件篩選報告的範本，在填範本時新增一張合成明細工作表，資料都由程式產生；
/// 範本原有的摘要頁照原樣保留，不是這裡要看的東西。
/// </summary>
public sealed class FormalWorkbookArchiveTests
{
    private const string SummarySheetName = "Summary Inforamtion";
    private const string DetailSheetName = "合成明細";
    private const int DetailRows = 20_000;
    private const int DetailColumns = 10;

    [Fact]
    public async Task DetailRows_ReachTheOutputFileBeforeTheWorkbookCloses()
    {
        await using var output = new MemoryStream();
        long lengthBeforeRows = 0;
        long lengthAfterRows = 0;

        await ReportTemplatePackage.FillDirectStreamingAsync(
            ReportTemplateCatalog.Default,
            ReportTemplateCatalog.CriteriaSelection,
            output,
            [SummarySheetName],
            (editor, cancellationToken) =>
            {
                KeepSummarySheet(editor, cancellationToken);
                var sheet = AddDetailSheet(editor.Document, DetailSheetName);
                lengthBeforeRows = output.Length;
                WriteDetailRows(sheet);
                lengthAfterRows = output.Length;
                var stat = sheet.CloseAndSummarize(cancellationToken);
                editor.Document.WorkbookPart!.RequiredWorkbook().Save();
                return Task.FromResult<IReadOnlyList<SheetStat>>([stat]);
            },
            CancellationToken.None);

        Assert.True(
            lengthAfterRows - lengthBeforeRows > 100_000,
            $"寫完 {DetailRows:N0} 列明細、活頁簿還沒關閉時，輸出檔應該已經包含這些明細；"
            + $"實際寫明細前後是 {lengthBeforeRows:N0} 與 {lengthAfterRows:N0} 位元組。");
        AssertDetailSheet(output);
    }

    [Fact]
    public async Task TwoDetailSheetsWrittenAtOnce_FailsWithAProgrammingError()
    {
        await using var output = new MemoryStream();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReportTemplatePackage.FillDirectStreamingAsync(
                ReportTemplateCatalog.Default,
                ReportTemplateCatalog.CriteriaSelection,
                output,
                [SummarySheetName],
                (editor, cancellationToken) =>
                {
                    KeepSummarySheet(editor, cancellationToken);
                    using var first = AddDetailSheet(editor.Document, "合成明細一");
                    using var second = AddDetailSheet(editor.Document, "合成明細二");
                    return Task.FromResult<IReadOnlyList<SheetStat>>([]);
                },
                CancellationToken.None));

        Assert.Contains("同時只能寫一張", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutputWithOldContent_IsReplacedByTheNewWorkbook()
    {
        // 舊內容比新的活頁簿長，沒有清掉的話，檔尾會留著舊的位元組。
        await using var output = new MemoryStream();
        output.Write(new byte[5_000_000]);
        output.Position = 100;

        var stats = await FillWithDetailSheetAsync(output);

        Assert.Equal(output.Length, stats.BytesWritten);
        AssertDetailSheet(output);
    }

    [Fact]
    public async Task OutputThatCannotSeek_IsRejectedBeforeAnythingIsWritten()
    {
        await using var output = new ForwardOnlyStream();

        var exception = await Assert.ThrowsAsync<JetActionException>(() => FillWithDetailSheetAsync(output));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
        Assert.Equal(0, output.BytesReceived);
    }

    private static Task<ExportStats> FillWithDetailSheetAsync(Stream output) =>
        ReportTemplatePackage.FillDirectStreamingAsync(
            ReportTemplateCatalog.Default,
            ReportTemplateCatalog.CriteriaSelection,
            output,
            [SummarySheetName],
            (editor, cancellationToken) =>
            {
                KeepSummarySheet(editor, cancellationToken);
                var sheet = AddDetailSheet(editor.Document, DetailSheetName);
                WriteDetailRows(sheet);
                var stat = sheet.CloseAndSummarize(cancellationToken);
                editor.Document.WorkbookPart!.RequiredWorkbook().Save();
                return Task.FromResult<IReadOnlyList<SheetStat>>([stat]);
            },
            CancellationToken.None);

    /// <summary>只能往前寫、不能定位的輸出，記下收到幾個位元組。</summary>
    private sealed class ForwardOnlyStream : MemoryStream
    {
        public long BytesReceived => base.Length;

        public override bool CanSeek => false;
    }

    private static void KeepSummarySheet(DirectTemplateWorkbookEditor editor, CancellationToken cancellationToken) =>
        editor.RewriteWorksheetPart(SummarySheetName, static (_, _) => { }, cancellationToken);

    private static ReportSheetWriter AddDetailSheet(SpreadsheetDocument document, string name)
    {
        var workbookPart = document.WorkbookPart!;
        var sheets = workbookPart.RequiredWorkbook().Sheets!;
        var part = workbookPart.AddNewPart<WorksheetPart>();
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(part),
            SheetId = sheets.Elements<Sheet>().Max(sheet => sheet.SheetId?.Value ?? 0U) + 1U,
            Name = name
        });
        return new ReportSheetWriter(name, part);
    }

    private static void WriteDetailRows(ReportSheetWriter sheet)
    {
        for (uint row = 1; row <= DetailRows; row++)
        {
            var cells = new Cell[DetailColumns];
            for (uint column = 1; column <= DetailColumns; column++)
            {
                cells[column - 1] = sheet.TextCell(row, column, CellText(row, column), style: 0);
            }

            sheet.WriteDataRow(row, cells);
        }
    }

    private static string CellText(uint row, uint column) => $"合成明細 {row}-{column}";

    private static void AssertDetailSheet(MemoryStream output)
    {
        using var document = SpreadsheetDocument.Open(new MemoryStream(output.ToArray(), writable: false), false);
        var workbookPart = document.WorkbookPart!;
        var sheet = workbookPart.RequiredWorkbook().Sheets!.Elements<Sheet>()
            .Single(item => item.Name?.Value == DetailSheetName);
        var part = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);

        var rows = 0;
        string? firstText = null;
        string? lastText = null;
        using var reader = OpenXmlReader.Create(part);
        while (reader.Read())
        {
            if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
            {
                continue;
            }

            var row = (Row)reader.LoadCurrentElement()!;
            rows++;
            var cells = row.Elements<Cell>().ToArray();
            Assert.Equal(DetailColumns, cells.Length);
            firstText ??= cells[0].InnerText;
            lastText = cells[^1].InnerText;
        }

        Assert.Equal(DetailRows, rows);
        Assert.Equal(CellText(1, 1), firstText);
        Assert.Equal(CellText(DetailRows, DetailColumns), lastText);
    }
}
