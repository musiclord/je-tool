using System.Text;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 正式輸出新寫的共用字串：編號接在範本原有的項目後面，同樣的短文字只存一次，記住的文字有字數與記憶體上限。
/// 項目先寫進暫存檔，收尾時照位元組接在範本的項目後面，所以暫存的內容本身就要是合法的 XML。資料都是程式產生的合成文字。
/// </summary>
public sealed class WorkbookSharedStringsTests
{
    private static readonly XNamespace Spreadsheet = SpreadsheetXmlWriter.MainNamespace;

    [Fact]
    public void DuplicateShortTexts_AreStoredOnce()
    {
        using var sharedStrings = new WorkbookSharedStrings(templateItemCount: 3);

        var indexes = new[] { "合成甲", "合成乙", "合成甲" }.Select(sharedStrings.Add).ToArray();

        Assert.Equal([3U, 4U, 3U], indexes);
        Assert.Equal(5L, sharedStrings.ItemCount);
        Assert.Equal(["合成甲", "合成乙"], ItemTexts(sharedStrings));
    }

    /// <summary>
    /// 只記四個字以內的文字，記憶體上限只夠記一筆。第二筆短文字記不下，之後每次都另存一份；已經記住的照樣命中；
    /// 超過字數的文字每次都另存一份。每一份的內容都照原樣。
    /// </summary>
    [Fact]
    public void TextsBeyondTheDeduplicationLimit_AreStillWrittenCorrectly()
    {
        using var sharedStrings = new WorkbookSharedStrings(
            templateItemCount: 0,
            deduplicatedTextLength: 4,
            deduplicationMemoryBudget: 100);

        var indexes = new[] { "合成甲", "合成乙", "合成甲", "合成乙", "合成長文字一", "合成長文字一" }
            .Select(sharedStrings.Add)
            .ToArray();

        Assert.Equal([0U, 1U, 0U, 2U, 3U, 4U], indexes);
        Assert.Equal(5L, sharedStrings.ItemCount);
        Assert.Equal(["合成甲", "合成乙", "合成乙", "合成長文字一", "合成長文字一"], ItemTexts(sharedStrings));
    }

    /// <summary>
    /// 暫存檔直接寫位元組，沒有 XML 寫出程式幫忙擋字元：控制字元、CR 與 XML 的特殊符號都要寫成讀得回原文的 XML，
    /// 文字加 xml:space="preserve"，檔案開頭沒有 BOM。
    /// </summary>
    [Fact]
    public void ControlCharactersAndMarkup_AreWrittenAsValidXml()
    {
        const string source = " 合成\u0001摘要<&>]]>\"'\r\n第二行\t𠀀_x0041_ ";
        var encoded = ExcelText.Encode(source);
        using var sharedStrings = new WorkbookSharedStrings(templateItemCount: 0);

        Assert.Equal(0U, sharedStrings.Add(encoded));

        var bytes = AddedItemBytes(sharedStrings);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), "暫存的共用字串不應該帶 BOM。");
        var item = Assert.Single(Items(bytes));
        var text = Assert.Single(item.Elements());
        Assert.Equal(Spreadsheet + "t", text.Name);
        Assert.Equal("preserve", (string?)text.Attribute(XNamespace.Xml + "space"));
        Assert.Equal(encoded, text.Value);
        Assert.Equal(source, ReportTextEncodingTests.Decode(text.Value));
    }

    /// <summary>
    /// 超過每格上限的文字寫進共用字串表時，截斷格數照樣記在寫進的那張工作表；長文字不去重複，每格各存一份截斷後的文字。
    /// </summary>
    [Fact]
    public void TruncatedTextInSharedStrings_IsCountedPerWorksheet()
    {
        var longText = new string('合', ExcelWorksheetConstraints.MaxCellTextLength + 10);
        using var workbook = new FormalWorkbook();

        var cells = new[] { (workbook.First, new Cell()), (workbook.First, new Cell()), (workbook.Second, new Cell()) };
        foreach (var (part, cell) in cells)
        {
            ExcelText.SetText(cell, longText, part);
        }

        Assert.Equal(2, ExcelText.TruncatedCellCount(workbook.First));
        Assert.Equal(1, ExcelText.TruncatedCellCount(workbook.Second));
        Assert.All(cells, item => Assert.Equal(CellValues.SharedString, item.Item2.DataType?.Value));
        Assert.All(cells, item => Assert.Null(item.Item2.InlineString));
        Assert.Equal(["0", "1", "2"], cells.Select(item => item.Item2.CellValue?.Text));
        var texts = ItemTexts(workbook.SharedStrings);
        Assert.Equal(3, texts.Count);
        Assert.All(texts, text =>
        {
            Assert.True(text.Length <= ExcelWorksheetConstraints.MaxCellTextLength, $"寫出 {text.Length} 字");
            Assert.EndsWith(ReportMessages.TruncatedCellNotice, text, StringComparison.Ordinal);
            Assert.StartsWith(text[..^ReportMessages.TruncatedCellNotice.Length], longText, StringComparison.Ordinal);
        });
    }

    /// <summary>不屬於正式輸出的活頁簿沒有共用字串的收集器，文字照舊寫成 inline string。</summary>
    [Fact]
    public void TextOutsideAFormalWorkbook_IsWrittenAsInlineString()
    {
        using var package = new MemoryStream();
        using var document = SpreadsheetDocument.Create(package, SpreadsheetDocumentType.Workbook, autoSave: false);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook(new Sheets());
        var part = workbookPart.AddNewPart<WorksheetPart>();
        var cell = new Cell();

        ExcelText.SetText(cell, " 合成\n摘要 ", part);

        Assert.Null(FormalWorkbookArchive.SharedStringsOf(document));
        Assert.Equal(CellValues.InlineString, cell.DataType?.Value);
        var text = Assert.Single(cell.InlineString!.Elements<Text>());
        Assert.Equal(" 合成\n摘要 ", text.Text);
        Assert.Equal(SpaceProcessingModeValues.Preserve, text.Space?.Value);
    }

    /// <summary>JET 讀回用的紀錄與工作底稿的文字，在寫進共用字串表的正式輸出裡也照樣寫成 inline string，不加進共用字串表。</summary>
    [Fact]
    public void InlineTextInAFormalWorkbook_StaysInlineAndAddsNoSharedString()
    {
        using var workbook = new FormalWorkbook();
        var verbatim = new Cell();
        var truncated = new Cell();

        ExcelText.SetVerbatimInlineText(verbatim, "合成紀錄");
        ExcelText.SetInlineText(truncated, "合成底稿", workbook.First);

        Assert.All([verbatim, truncated], cell => Assert.Equal(CellValues.InlineString, cell.DataType?.Value));
        Assert.Equal(["合成紀錄", "合成底稿"], new[] { verbatim, truncated }.Select(cell => cell.InlineString?.InnerText));
        Assert.Equal(0L, workbook.SharedStrings.ItemCount);
    }

    /// <summary>照原樣寫出的文字也寫進共用字串表，而且不截斷、不記截斷格數。</summary>
    [Fact]
    public void VerbatimTextInAFormalWorkbook_IsASharedStringWithoutTruncation()
    {
        var longText = new string('合', ExcelWorksheetConstraints.MaxCellTextLength + 10);
        using var workbook = new FormalWorkbook();
        var cell = new Cell();

        ExcelText.SetVerbatimText(cell, longText, workbook.First);

        Assert.Equal(CellValues.SharedString, cell.DataType?.Value);
        Assert.Equal("0", cell.CellValue?.Text);
        Assert.Equal(0, ExcelText.TruncatedCellCount(workbook.First));
        Assert.Equal([longText], ItemTexts(workbook.SharedStrings));
    }

    private static IReadOnlyList<string> ItemTexts(WorkbookSharedStrings sharedStrings) =>
        Items(AddedItemBytes(sharedStrings))
            .Select(item => string.Concat(item.Elements(Spreadsheet + "t").Select(text => text.Value)))
            .ToArray();

    private static byte[] AddedItemBytes(WorkbookSharedStrings sharedStrings)
    {
        using var output = new MemoryStream();
        sharedStrings.CopyAddedItemsTo(output, CancellationToken.None);
        return output.ToArray();
    }

    /// <summary>把暫存的項目包進 sst 根元素，用一般的 XML 解析器讀，不合法的字元或符號會讀不進來。</summary>
    private static IReadOnlyList<XElement> Items(byte[] addedItems)
    {
        var xml = Encoding.UTF8.GetBytes($"<sst xmlns=\"{SpreadsheetXmlWriter.MainNamespace}\">")
            .Concat(addedItems)
            .Concat(Encoding.UTF8.GetBytes("</sst>"))
            .ToArray();
        using var reader = XmlReader.Create(
            new MemoryStream(xml, writable: false),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var root = XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root!;
        Assert.All(root.Nodes(), node => Assert.IsType<XElement>(node));
        var items = root.Elements().ToArray();
        Assert.All(items, item => Assert.Equal(Spreadsheet + "si", item.Name));
        return items;
    }

    /// <summary>接在正式輸出壓縮檔上的合成活頁簿，有兩張工作表；壓縮檔選寫進共用字串表的寫法。</summary>
    private sealed class FormalWorkbook : IDisposable
    {
        private readonly MemoryStream _package = new();
        private readonly MemoryStream _output = new();
        private readonly SpreadsheetDocument _document;
        private readonly FormalWorkbookArchive _archive;

        internal FormalWorkbook()
        {
            _document = SpreadsheetDocument.Create(_package, SpreadsheetDocumentType.Workbook, autoSave: false);
            var workbookPart = _document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook(new Sheets());
            First = workbookPart.AddNewPart<WorksheetPart>();
            Second = workbookPart.AddNewPart<WorksheetPart>();
            _archive = new FormalWorkbookArchive(_output, FormalWorkbookLayout.SameAsExcel);
            _archive.Attach(_document);
        }

        internal WorksheetPart First { get; }

        internal WorksheetPart Second { get; }

        internal WorkbookSharedStrings SharedStrings =>
            FormalWorkbookArchive.SharedStringsOf(_document)
            ?? throw new InvalidOperationException("正式輸出的活頁簿沒有共用字串的收集器。");

        public void Dispose()
        {
            _archive.Dispose();
            _document.Dispose();
            _package.Dispose();
            _output.Dispose();
        }
    }
}
