using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;
using static JET.Tests.Infrastructure.WorkingPaperWriterTestSupport;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 報告與工作底稿的工作表寫入器在取消或失敗時放掉檔案與暫存資源：釋放時不補寫結尾、不丟出新的例外，
/// 建立到一半失敗時，已經開啟的壓縮檔項目與暫存檔也一併放掉。資料都由程式合成。
/// 範本副本只看測試自己留住的那一份；壓縮檔項目有沒有放掉，看同一個壓縮檔能不能馬上開始寫下一張工作表。
/// </summary>
public sealed class ExportWriterResourceReleaseTests
{
    [Fact]
    public void WorkingPaperSheet_CancelledBetweenRows_DisposeReleasesTheEntryWithoutThrowing()
    {
        using var output = new MemoryStream();
        using var workbook = new SyntheticWorkbook(output);
        var template = workbook.AddSheet(TemplateWithRows(5));
        var next = workbook.AddSheet(new Worksheet(new SheetData()));
        using var cancellation = new CancellationTokenSource();
        var sheet = WorkingPaperWriter.SheetWriter.OverlayTemplate(
            "合成一", template, AllCellsFromSource(), workbook.Styles, mapCellStyle: null, cancellation.Token);
        sheet.WriteRow(2, [sheet.TextCell(2, 1, "合成")]);
        // 這張工作表還開著的時候，壓縮檔不讓下一張開始寫。
        Assert.IsType<InvalidOperationException>(TryStartSheet(next));

        // 第 2 列寫完、第 3 列還沒寫時取消；範本後面還有三列沒有處理。
        cancellation.Cancel();
        var error = Record.Exception(sheet.Dispose);

        Assert.Null(error);
        Assert.Null(TryStartSheet(next));
    }

    [Fact]
    public void OverlayWriter_CancelledBetweenRows_DisposeReleasesItsTemplateCopyAndTheEntry()
    {
        using var output = new MemoryStream();
        using var workbook = new SyntheticWorkbook(output);
        var template = workbook.AddSheet(TemplateWithRows(5));
        var next = workbook.AddSheet(new Worksheet(new SheetData()));
        using var cancellation = new CancellationTokenSource();
        FileStream? copy = null;
        var overlay = new DirectTemplateWorksheetOverlayWriter(
            "合成一",
            template,
            AllCellsFromSource(),
            cancellation.Token,
            () => copy = DirectTemplateWorksheetOverlayWriter.CreateTemporaryWorksheetStream());
        overlay.WriteRow(new Row(new Cell { CellReference = "A2" }) { RowIndex = 2U });
        Assert.NotNull(copy);
        Assert.True(copy.CanRead && File.Exists(copy.Name), "寫到一半時，範本工作表副本應該還開著。");

        // 第 2 列寫完、第 3 列還沒寫時取消；範本後面還有三列沒有處理。
        cancellation.Cancel();
        var error = Record.Exception(overlay.Dispose);

        Assert.Null(error);
        AssertClosedAndDeleted(copy);
        Assert.Null(TryStartSheet(next));
    }

    [Fact]
    public async Task WorkingPaperExport_CancelledBetweenRows_ReleasesTheSheetBeingWritten()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationProjectAsync(host, voucherCount: 3);
        await ImportContinuationReferencesAsync(host, voucherCount: 3);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());
        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context);
        using var template = await OpenedWorkingPaperTemplate.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var cancelled = false;
        Exception? startedWhileStep4Open = null;

        void OnProgress(WorkingPaperProgress update)
        {
            if (update.SheetName == Step4Sheet && update.RowsWritten == 1)
            {
                cancelled = true;
                startedWhileStep4Open = template.TryStartNextSheet();
                cancellation.Cancel();
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BuildWriter(host, new WorkingPaperWriterOptions(), progressRowInterval: 1).FillTemplateAsync(
                template.Editor, context, plan, cancellation.Token, OnProgress));

        // step4 寫完第一列時還開著，壓縮檔不讓下一張開始寫；取消之後馬上就可以。
        Assert.True(cancelled, "step4 沒有寫到第一列，測不到這個情形。");
        Assert.IsType<InvalidOperationException>(startedWhileStep4Open);
        Assert.Null(template.TryStartNextSheet());
    }

    [Fact]
    public async Task WorkingPaperExport_FirstPageFailsWhileWritingFixedRows_ReleasesTheOpenedSheet()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationProjectAsync(host, voucherCount: 3);
        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context);
        // step1 的固定列要用計畫裡的說明文字。拿掉之後，工作表已經開啟、前幾列固定列也寫了，才在建立分頁的途中失敗。
        var planWithoutStep1Texts = plan with
        {
            Sheets = plan.Sheets
                .Select(sheet => sheet.SheetName == Step1Sheet ? sheet with { Methodology = [] } : sheet)
                .ToArray()
        };
        using var template = await OpenedWorkingPaperTemplate.OpenAsync();
        var finishedSheets = new List<string>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuildWriter(host).FillTemplateAsync(
                template.Editor,
                context,
                planWithoutStep1Texts,
                CancellationToken.None,
                update => finishedSheets.Add(update.SheetName)));

        Assert.Contains("methodology", error.Message, StringComparison.Ordinal);
        // 匯出一結束，同一個壓縮檔就可以開始寫下一張工作表。
        Assert.Null(template.TryStartNextSheet());
        // 失敗發生時 step1 開著：它前面的封面寫完了，step1 沒有寫完，但表頭的固定列已經寫進它在輸出裡的項目。
        Assert.Contains(CoverSheet, finishedSheets);
        Assert.DoesNotContain(Step1Sheet, finishedSheets);
        Assert.Contains(context.CompanyName, template.CloseAndReadSheet(Step1Sheet));
    }

    [Fact]
    public void OverlayWriter_TemplateCannotBeRead_ReleasesTheTemplateCopy()
    {
        using var output = new MemoryStream();
        using var workbook = new SyntheticWorkbook(output);
        // 範本工作表沒有 sheetData：副本建立之後，讀範本開頭時才失敗。
        var template = workbook.AddSheet(new Worksheet());
        var next = workbook.AddSheet(new Worksheet(new SheetData()));
        FileStream? copy = null;

        Assert.Throws<InvalidDataException>(() => new DirectTemplateWorksheetOverlayWriter(
            "合成一",
            template,
            AllCellsFromSource(),
            CancellationToken.None,
            () => copy = DirectTemplateWorksheetOverlayWriter.CreateTemporaryWorksheetStream()));

        Assert.NotNull(copy);
        AssertClosedAndDeleted(copy);
        // 建構失敗沒有留下開著的項目。
        Assert.Null(TryStartSheet(next));
    }

    [Fact]
    public void ReportSheetWriter_ConstructorFailsAfterOpeningTheEntry_ReleasesIt()
    {
        using var output = new MemoryStream();
        using var workbook = new SyntheticWorkbook(output);
        var first = workbook.AddSheet(new Worksheet(new SheetData()));
        var next = workbook.AddSheet(new Worksheet(new SheetData()));

        Assert.Throws<ArgumentOutOfRangeException>(() => new ReportSheetWriter(
            "合成一", first, options: new ReportSheetOptions(DefaultRowHeight: -1)));

        using var following = new ReportSheetWriter("合成二", next);
        following.CloseAndSummarize(CancellationToken.None);
    }

    [Fact]
    public void DirectTemplateStreamingSheetWriter_ConstructorFailsWhileWritingFixedRows_ReleasesTheEntry()
    {
        using var output = new MemoryStream();
        using var workbook = new SyntheticWorkbook(output);
        // 固定列裡有 XML 不允許的字元，建構式把固定列寫出去時才失敗。
        var first = workbook.AddSheet(
            new Worksheet(new SheetData(new Row(RawTextCell("A1", "固定\u0001列")) { RowIndex = 1 })),
            save: false);
        var next = workbook.AddSheet(new Worksheet(new SheetData()));

        Assert.Throws<ArgumentException>(() => new DirectTemplateStreamingSheetWriter("合成一", first, firstDynamicRow: 2));

        using var following = new ReportSheetWriter("合成二", next);
        following.CloseAndSummarize(CancellationToken.None);
    }

    [Fact]
    public void Archive_DisposedBeforeCompletion_ClosesAndRejectsFurtherSheets()
    {
        using var output = new MemoryStream();
        using var workbook = new SyntheticWorkbook(output);
        var first = workbook.AddSheet(new Worksheet(new SheetData()));
        var next = workbook.AddSheet(new Worksheet(new SheetData()));
        // 開了一張工作表卻沒有人釋放，例如寫表頭時失敗。
        var orphan = new ReportSheetWriter("合成一", first);
        orphan.WriteDataRow(1, [orphan.TextCell(1, 1, "合成", style: 0)]);

        workbook.Archive.Dispose();

        Assert.Null(Record.Exception(workbook.Archive.Dispose));
        Assert.Throws<ObjectDisposedException>(() => new ReportSheetWriter("合成二", next));
        using var copy = new MemoryStream();
        Assert.Throws<ObjectDisposedException>(() => workbook.Archive.Complete(copy, CancellationToken.None));
        GC.KeepAlive(orphan);
    }

    [Fact]
    public void Archive_OutputKeepsFailing_DisposeDoesNotThrow()
    {
        using var output = new FailingOutputStream();
        using var workbook = new SyntheticWorkbook(output);
        var first = workbook.AddSheet(new Worksheet(new SheetData()));
        var orphan = new ReportSheetWriter("合成一", first);
        orphan.WriteDataRow(1, [orphan.TextCell(1, 1, "合成", style: 0)]);
        output.FailFromNowOn();

        var error = Record.Exception(workbook.Archive.Dispose);

        Assert.Null(error);
        Assert.True(output.Failures > 0, "關閉壓縮檔時沒有寫到輸出檔，測不到這個情形。");
        GC.KeepAlive(orphan);
    }

    [Theory]
    [InlineData("report")]
    [InlineData("streamingTemplate")]
    [InlineData("workingPaperContinuation")]
    [InlineData("workingPaperTemplate")]
    public void FailureBetweenRows_WhileTheOutputAlsoFails_KeepsTheOriginalError(string writerKind)
    {
        using var output = new FailingOutputStream();
        using var workbook = new SyntheticWorkbook(output);
        var part = workbook.AddSheet(TemplateWithRows(5));

        void FailBetweenRows()
        {
            using var sheet = OpenWithOneRowWritten(writerKind, workbook, part);
            output.FailFromNowOn();
            throw new InvalidOperationException("合成的讀取失敗");
        }

        var error = Record.Exception(FailBetweenRows);

        Assert.Equal("合成的讀取失敗", Assert.IsType<InvalidOperationException>(error).Message);
    }

    [Fact]
    public void RewrittenTemplateSheet_FailureWhileTheOutputAlsoFails_KeepsTheOriginalError()
    {
        using var output = new FailingOutputStream();
        using var workbook = new SyntheticWorkbook(output);
        var part = workbook.AddSheet(new Worksheet(
            new Columns(new Column { Min = 1, Max = 1, Style = 0 }),
            new SheetData(new Row { RowIndex = 1 }, new Row { RowIndex = 2 })));
        var plan = new DirectTemplateWorksheetRewritePlan(
            FirstDynamicRow: 2,
            DimensionReference: "A1:A2",
            PrototypeColumns: [1],
            MapColumnStyle: null,
            CreateDynamicRows: (_, _) =>
            {
                output.FailFromNowOn();
                throw new InvalidOperationException("合成的讀取失敗");
            });

        var error = Record.Exception(() => DirectTemplateWorksheetRewriter.Rewrite(part, plan, CancellationToken.None));

        Assert.Equal("合成的讀取失敗", Assert.IsType<InvalidOperationException>(error).Message);
    }

    private static IDisposable OpenWithOneRowWritten(string writerKind, SyntheticWorkbook workbook, WorksheetPart part)
    {
        switch (writerKind)
        {
            case "report":
                var report = new ReportSheetWriter("合成", part);
                report.WriteDataRow(6, [report.TextCell(6, 1, "合成", style: 0)]);
                return report;
            case "streamingTemplate":
                var streaming = new DirectTemplateStreamingSheetWriter("合成", part, firstDynamicRow: 6);
                streaming.WriteDataRow(6, [new Cell { CellReference = "A6" }]);
                return streaming;
            case "workingPaperContinuation":
                var continuation = WorkingPaperWriter.SheetWriter.CreateContinuation(
                    "合成", part, workbook.Styles, mapCellStyle: null, CancellationToken.None, pageSetupRelationshipId: null);
                continuation.WriteRow(6, [continuation.TextCell(6, 1, "合成")]);
                return continuation;
            case "workingPaperTemplate":
                var overlay = WorkingPaperWriter.SheetWriter.OverlayTemplate(
                    "合成", part, AllCellsFromSource(), workbook.Styles, mapCellStyle: null, CancellationToken.None);
                overlay.WriteRow(2, [overlay.TextCell(2, 1, "合成")]);
                return overlay;
            default:
                throw new ArgumentOutOfRangeException(nameof(writerKind));
        }
    }

    private static Worksheet TemplateWithRows(int rows) => new(new SheetData(
        Enumerable.Range(1, rows).Select(row => new Row(new Cell { CellReference = $"A{row}" }) { RowIndex = (uint)row })));

    private static Cell RawTextCell(string reference, string text) => new()
    {
        CellReference = reference,
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(text)),
    };

    private static DirectTemplateWorksheetOverlayPlan AllCellsFromSource() => new(
        IsDynamicRow: _ => true,
        IsDynamicCell: (_, _) => true,
        UseSourcePostSheetDataElements: null,
        UseSourceDimension: false,
        PreserveTemplateExtent: false,
        UseSourceColumnsWhenTemplateMissing: false,
        UseSourceColumns: false,
        MergeSourceColumnWidths: false,
        UseSourceCellStyle: (_, _) => true);

    /// <summary>
    /// 試著在工作表所屬的壓縮檔開始寫這張工作表，開了就馬上關閉；回傳丟出的例外。
    /// 壓縮檔同時只能寫一張工作表，所以回傳 null 表示這時沒有別的工作表開著。
    /// </summary>
    private static Exception? TryStartSheet(WorksheetPart part) =>
        Record.Exception(() => FormalWorkbookArchive.OpenWorksheetContent(part).Dispose());

    /// <summary>
    /// 確認寫入器已經關閉這一份範本副本，檔案也跟著刪除。副本開著的時候檔案存在，關閉就自動刪除；
    /// 測試留著這一份副本，沒有人釋放時它會一直開著。訊息不寫路徑。
    /// </summary>
    private static void AssertClosedAndDeleted(FileStream templateCopy)
    {
        Assert.False(templateCopy.CanRead, "寫入器釋放之後，範本工作表副本還開著。");
        Assert.False(File.Exists(templateCopy.Name), "寫入器釋放之後，範本工作表副本還留在暫存資料夾。");
    }

    /// <summary>
    /// 照匯出底稿的做法開好的範本：範本複製到記憶體裡的小活頁簿，工作表內容寫進接在輸出上的壓縮檔。
    /// 活頁簿與壓縮檔留在測試手上，寫底稿失敗或取消之後壓縮檔還沒關閉，可以直接看這時有沒有工作表還開著。
    /// </summary>
    private sealed class OpenedWorkingPaperTemplate : IDisposable
    {
        private readonly MemoryStream _output = new();
        private readonly MemoryStream _workbook;
        private readonly SpreadsheetDocument _document;
        private readonly FormalWorkbookArchive _archive;

        private OpenedWorkingPaperTemplate(MemoryStream workbook)
        {
            _workbook = workbook;
            _archive = new FormalWorkbookArchive(_output);
            _document = SpreadsheetDocument.Open(workbook, true, new OpenSettings { AutoSave = false });
            _archive.Attach(_document);
            // 範本裡的工作表都可以改寫。
            Editor = new DirectTemplateWorkbookEditor(
                _document,
                Sheets().Select(sheet => sheet.Name!.Value!).ToArray());
        }

        internal DirectTemplateWorkbookEditor Editor { get; }

        internal static async Task<OpenedWorkingPaperTemplate> OpenAsync()
        {
            var workbook = new MemoryStream();
            await ReportTemplateCatalog.Default.CopyToAsync(
                ReportTemplateCatalog.WorkingPaper, workbook, CancellationToken.None);
            return new OpenedWorkingPaperTemplate(workbook);
        }

        /// <summary>在同一個壓縮檔開始寫一張新加的工作表；回傳的意思同 <see cref="TryStartSheet"/>。</summary>
        internal Exception? TryStartNextSheet() =>
            TryStartSheet(_document.WorkbookPart!.AddNewPart<WorksheetPart>());

        /// <summary>關閉壓縮檔，讀出這張工作表已經寫進輸出的內容；它沒有開始寫過時回傳 null。</summary>
        internal string? CloseAndReadSheet(string sheetName)
        {
            var sheet = Sheets().Single(item => item.Name?.Value == sheetName);
            var entryName = _document.WorkbookPart!.GetPartById(sheet.Id!.Value!).Uri.OriginalString.TrimStart('/');
            _archive.Dispose();
            using var written = new ZipArchive(_output, ZipArchiveMode.Read, leaveOpen: true);
            if (written.GetEntry(entryName) is not { } entry)
            {
                return null;
            }

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }

        public void Dispose()
        {
            _archive.Dispose();
            _document.Dispose();
            _workbook.Dispose();
            _output.Dispose();
        }

        private IEnumerable<Sheet> Sheets() =>
            _document.WorkbookPart!.RequiredWorkbook().Sheets!.Elements<Sheet>();
    }

    /// <summary>合成的活頁簿：工作表內容寫進接在 <c>output</c> 上的正式輸出壓縮檔。</summary>
    private sealed class SyntheticWorkbook : IDisposable
    {
        private readonly MemoryStream _package = new();
        private readonly SpreadsheetDocument _document;
        private readonly WorkbookPart _workbookPart;

        internal SyntheticWorkbook(Stream output)
        {
            _document = SpreadsheetDocument.Create(_package, SpreadsheetDocumentType.Workbook, autoSave: false);
            _workbookPart = _document.AddWorkbookPart();
            _workbookPart.Workbook = new Workbook(new Sheets());
            var stylesPart = _workbookPart.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = new Stylesheet();
            Styles = WorkbookStyleMap.Append(stylesPart, WorkingPaperStyles.Build());
            Archive = new FormalWorkbookArchive(output);
            Archive.Attach(_document);
        }

        internal WorkbookStyleMap Styles { get; }

        internal FormalWorkbookArchive Archive { get; }

        internal WorksheetPart AddSheet(Worksheet worksheet, bool save = true)
        {
            var part = _workbookPart.AddNewPart<WorksheetPart>();
            part.Worksheet = worksheet;
            if (save)
            {
                worksheet.Save();
            }

            var sheets = _workbookPart.RequiredWorkbook().Sheets!;
            var number = (uint)sheets.Elements<Sheet>().Count() + 1U;
            sheets.Append(new Sheet { Id = _workbookPart.GetIdOfPart(part), SheetId = number, Name = $"合成{number}" });
            return part;
        }

        public void Dispose()
        {
            Archive.Dispose();
            _document.Dispose();
            _package.Dispose();
        }
    }

    /// <summary>可以定位的輸出；要求失敗之後，每一次寫入都丟出同一個讀寫錯誤。</summary>
    private sealed class FailingOutputStream : MemoryStream
    {
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
                throw new IOException("合成的輸出檔寫入失敗");
            }
        }
    }
}
