using System.Globalization;
using System.IO.Compression;
using System.IO.Packaging;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 條件篩選、預篩選與資料驗證報告的輸出檔，寫法要和 Excel 存檔時相同，其他讀 xlsx 的程式才讀得到明細。
/// 這裡用條件篩選報告的範本，摘要頁改一格、再新增一張合成明細工作表，資料都由程式產生，然後直接讀輸出的壓縮檔，
/// 逐一檢查關聯路徑、BOM、命名空間前綴、dimension、共用字串表與文字裡的換行。
/// </summary>
public sealed class FormalWorkbookExcelLayoutTests
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string PackageRelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string SummarySheetName = "Summary Inforamtion";
    private const string SummaryCell = "C1";
    private const string DetailSheetName = "合成明細";
    private const string LineBreakText = "合成\n摘要";

    private static readonly XNamespace Spreadsheet = SpreadsheetNamespace;
    private static readonly XNamespace PackageRelationships = PackageRelationshipsNamespace;

    [Fact]
    public async Task AddedWorksheetRelationships_AreRelativeToTheirSourcePart()
    {
        var entries = await ExportEntriesAsync();
        var problems = new List<string>();

        foreach (var (name, content) in entries.Where(entry => IsRelationships(entry.Key)))
        {
            var source = PackUriHelper.GetSourcePartUriFromRelationshipPartUri(new Uri("/" + name, UriKind.Relative));
            foreach (var relationship in Parse(content).Root!.Elements(PackageRelationships + "Relationship"))
            {
                if (string.Equals((string?)relationship.Attribute("TargetMode"), "External", StringComparison.Ordinal))
                {
                    continue;
                }

                var target = (string)relationship.Attribute("Target")!;
                if (target.StartsWith('/'))
                {
                    problems.Add($"{name}：{target} 從根目錄起算");
                    continue;
                }

                var resolved = PackUriHelper.ResolvePartUri(source, new Uri(target, UriKind.Relative))
                    .OriginalString
                    .TrimStart('/');
                if (!entries.ContainsKey(resolved))
                {
                    problems.Add($"{name}：{target} 解析成 {resolved}，壓縮檔裡沒有這個項目");
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public async Task EveryXmlEntry_StartsWithoutByteOrderMark()
    {
        var entries = await ExportEntriesAsync();

        var withByteOrderMark = entries
            .Where(entry => IsXml(entry.Key) || IsRelationships(entry.Key))
            .Where(entry => entry.Value.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            .Select(entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(withByteOrderMark);
    }

    [Fact]
    public async Task SpreadsheetMainNamespace_IsTheDefaultNamespaceInEveryPart()
    {
        var entries = await ExportEntriesAsync();
        var prefixed = new List<string>();

        foreach (var (name, content) in entries.Where(entry => IsXml(entry.Key)))
        {
            using var reader = XmlReader.Create(new MemoryStream(content, writable: false), ReaderSettings());
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element
                    && string.Equals(reader.NamespaceURI, SpreadsheetNamespace, StringComparison.Ordinal)
                    && reader.Prefix.Length > 0)
                {
                    prefixed.Add($"{name}：<{reader.Name}>");
                    break;
                }
            }
        }

        Assert.Empty(prefixed);
    }

    [Fact]
    public async Task EveryWorksheet_DeclaresDimensionEqualToWrittenCells()
    {
        var entries = await ExportEntriesAsync();
        var problems = new List<string>();

        foreach (var (name, content) in entries.Where(entry => IsWorksheet(entry.Key)))
        {
            var worksheet = Parse(content).Root!;
            var children = worksheet.Elements().ToArray();
            var dimensionIndex = Array.FindIndex(children, element => element.Name == Spreadsheet + "dimension");
            if (dimensionIndex < 0)
            {
                problems.Add($"{name}：沒有 dimension");
                continue;
            }

            if (children.Take(dimensionIndex).Any(element => element.Name != Spreadsheet + "sheetPr"))
            {
                problems.Add($"{name}：dimension 前面除了 sheetPr 還有其他元素");
            }

            var declared = (string?)children[dimensionIndex].Attribute("ref");
            var written = WrittenRange(worksheet);
            if (!string.Equals(declared, written, StringComparison.Ordinal))
            {
                problems.Add($"{name}：dimension 是 {declared}，寫出的格子範圍是 {written}");
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public async Task TextCells_ReferToTheSharedStringTable()
    {
        var entries = await ExportEntriesAsync();
        var template = TemplateEntries();
        var problems = new List<string>();

        var sharedStrings = Parse(entries["xl/sharedStrings.xml"]).Root!;
        var items = sharedStrings.Elements(Spreadsheet + "si").ToArray();
        var references = 0;
        foreach (var (name, content) in entries.Where(entry => IsWorksheet(entry.Key)))
        {
            foreach (var cell in Parse(content).Descendants(Spreadsheet + "c"))
            {
                var type = (string?)cell.Attribute("t");
                if (string.Equals(type, "inlineStr", StringComparison.Ordinal))
                {
                    problems.Add($"{name}：{(string?)cell.Attribute("r")} 是 inline string");
                }

                if (!string.Equals(type, "s", StringComparison.Ordinal))
                {
                    continue;
                }

                references++;
                var value = (string?)cell.Element(Spreadsheet + "v");
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    || index >= items.Length)
                {
                    problems.Add($"{name}：{(string?)cell.Attribute("r")} 的編號 {value} 不在共用字串表裡");
                }
            }
        }

        if ((string?)sharedStrings.Attribute("uniqueCount") != items.Length.ToString(CultureInfo.InvariantCulture))
        {
            problems.Add($"uniqueCount 是 {(string?)sharedStrings.Attribute("uniqueCount")}，實際有 {items.Length} 個項目");
        }

        if ((string?)sharedStrings.Attribute("count") != references.ToString(CultureInfo.InvariantCulture))
        {
            problems.Add($"count 是 {(string?)sharedStrings.Attribute("count")}，工作表實際有 {references} 格參照共用字串表");
        }

        // 統一字型會改範本項目裡的字型名稱，所以只比文字。
        var templateTexts = Parse(template["xl/sharedStrings.xml"]).Root!
            .Elements(Spreadsheet + "si")
            .Select(ItemText)
            .ToArray();
        var outputTexts = items.Take(templateTexts.Length).Select(ItemText).ToArray();
        if (!templateTexts.SequenceEqual(outputTexts, StringComparer.Ordinal))
        {
            problems.Add("共用字串表開頭的項目和範本原有的項目不同");
        }

        Assert.Empty(problems);
    }

    [Fact]
    public async Task LineBreaksInText_AreWrittenAsLineFeed()
    {
        var entries = await ExportEntriesAsync();
        var lineFeed = Encoding.UTF8.GetBytes(LineBreakText);
        var carriageReturnLineFeed = Encoding.UTF8.GetBytes(LineBreakText.Replace("\n", "\r\n", StringComparison.Ordinal));

        var writtenWithLineFeed = entries
            .Where(entry => entry.Value.AsSpan().IndexOf(lineFeed) >= 0)
            .Select(entry => entry.Key)
            .ToArray();
        var writtenWithCarriageReturn = entries
            .Where(entry => entry.Value.AsSpan().IndexOf(carriageReturnLineFeed) >= 0)
            .Select(entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(writtenWithCarriageReturn);
        Assert.NotEmpty(writtenWithLineFeed);
    }

    /// <summary>
    /// 同一份合成資料分別用和 Excel 相同的寫法與照 SDK 寫出的寫法產生：文字一份在共用字串表、一份是 inline string，
    /// 儲存格的內容與外觀都相同。內容的比對把共用字串與 inline string 當成同一種文字，外觀的比對不看儲存格型別。
    /// </summary>
    [Fact]
    public async Task ExcelLayout_KeepsCellContentAndAppearance()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jet-excel-layout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var sameAsExcel = Path.Combine(directory, "same-as-excel.xlsx");
            var asWritten = Path.Combine(directory, "as-written.xlsx");
            var sameAsExcelPackage = await ExportPackageAsync(FormalWorkbookLayout.SameAsExcel);
            var asWrittenPackage = await ExportPackageAsync(FormalWorkbookLayout.AsWritten);
            await File.WriteAllBytesAsync(sameAsExcel, sameAsExcelPackage);
            await File.WriteAllBytesAsync(asWritten, asWrittenPackage);
            // 前提：兩份的寫法真的不同，比對才有意義。
            Assert.Equal(["inlineStr", "inlineStr", "inlineStr"], DetailTextCellTypes(asWrittenPackage));
            Assert.Equal(["s", "s", "s"], DetailTextCellTypes(sameAsExcelPackage));

            var sameAsExcelContent = Path.Combine(directory, "same-as-excel.content.ndjson");
            var asWrittenContent = Path.Combine(directory, "as-written.content.ndjson");
            SpreadsheetContentFingerprint.Capture(sameAsExcel, sameAsExcelContent, "excel-layout", _ => SpreadsheetContentHeaderRule.None);
            SpreadsheetContentFingerprint.Capture(asWritten, asWrittenContent, "excel-layout", _ => SpreadsheetContentHeaderRule.None);

            Assert.Null(SpreadsheetContentFingerprint.DescribeFirstDifference(asWrittenContent, sameAsExcelContent));
            Assert.True(SpreadsheetContentFingerprint.FilesEqual(asWrittenContent, sameAsExcelContent));
            Assert.Equal(
                SpreadsheetAppearanceFingerprint.Capture(asWritten).Json,
                SpreadsheetAppearanceFingerprint.Capture(sameAsExcel).Json);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 新寫的共用字串編號從範本原有的項目數接下去。寫出期間範本的項目數變了，已經寫出的編號就會指錯文字，
    /// 所以收尾時核對，不符是程式錯誤，丟出 <see cref="InvalidOperationException"/>，不寫出這份檔案。
    /// </summary>
    [Fact]
    public async Task TemplateSharedStringsChangedWhileWriting_FailsWithAProgrammingError()
    {
        var error = await Record.ExceptionAsync(() => ExportPackageAsync(
            FormalWorkbookLayout.SameAsExcel,
            document =>
            {
                var table = document.WorkbookPart!.SharedStringTablePart!.SharedStringTable!;
                table.Append(new SharedStringItem(new Text("合成範本外的項目")));
                table.Save();
            }));

        var failure = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("共用字串表", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>明細頁 A1、A2、A3 三個文字格的 t 屬性。</summary>
    private static IReadOnlyList<string?> DetailTextCellTypes(byte[] package)
    {
        var detail = Parse(Entries(package)["xl/worksheets/sheet2.xml"]).Root!;
        return detail
            .Descendants(Spreadsheet + "c")
            .Where(cell => ((string?)cell.Attribute("r"))?.StartsWith('A') == true)
            .Select(cell => (string?)cell.Attribute("t"))
            .ToArray();
    }

    /// <summary>
    /// 摘要頁用 DOM 改一格、存回；明細頁用 <see cref="ReportSheetWriter"/> 寫三列，兩邊都有含換行的文字。
    /// 回傳輸出壓縮檔裡每個項目解壓後的內容。
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, byte[]>> ExportEntriesAsync() =>
        Entries(await ExportPackageAsync(FormalWorkbookLayout.SameAsExcel));

    /// <summary>
    /// 照 <see cref="ExportEntriesAsync"/> 的內容寫出整份輸出檔；<paramref name="beforeClose"/> 在寫完工作表、收尾之前執行。
    /// </summary>
    private static async Task<byte[]> ExportPackageAsync(
        FormalWorkbookLayout layout,
        Action<SpreadsheetDocument>? beforeClose = null)
    {
        await using var output = new MemoryStream();
        await ReportTemplatePackage.FillDirectStreamingAsync(
            ReportTemplateCatalog.Default,
            ReportTemplateCatalog.CriteriaSelection,
            output,
            [SummarySheetName],
            (editor, cancellationToken) =>
            {
                editor.RewriteWorksheetPart(SummarySheetName, EditSummary, cancellationToken);
                using var sheet = AddDetailSheet(editor.Document);
                WriteDetailRows(sheet);
                var stat = sheet.CloseAndSummarize(cancellationToken);
                editor.Document.WorkbookPart!.RequiredWorkbook().Save();
                beforeClose?.Invoke(editor.Document);
                return Task.FromResult<IReadOnlyList<SheetStat>>([stat]);
            },
            CancellationToken.None,
            layout);

        return output.ToArray();
    }

    private static void EditSummary(WorksheetPart part, CancellationToken cancellationToken)
    {
        var cells = new DirectTemplateCellEditor(part.RequiredWorksheet(), [SummaryCell]);
        cells.SetText(SummaryCell, LineBreakText);
        cells.EnsureAllAllowedCellsWereEdited();
        ExcelText.SaveWorksheet(part);
    }

    private static ReportSheetWriter AddDetailSheet(SpreadsheetDocument document)
    {
        var workbookPart = document.WorkbookPart!;
        var sheets = workbookPart.RequiredWorkbook().Sheets!;
        var part = workbookPart.AddNewPart<WorksheetPart>();
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(part),
            SheetId = sheets.Elements<Sheet>().Max(sheet => sheet.SheetId?.Value ?? 0U) + 1U,
            Name = DetailSheetName
        });
        return new ReportSheetWriter(DetailSheetName, part, SheetUsedRange.Table(3, 3));
    }

    private static void WriteDetailRows(ReportSheetWriter sheet)
    {
        sheet.WriteFixedRow(1, [
            sheet.TextCell(1, 1, "合成欄一", style: 0),
            sheet.TextCell(1, 2, "合成欄二", style: 0),
            sheet.TextCell(1, 3, "合成欄三", style: 0)
        ]);
        sheet.WriteDataRow(2, [
            sheet.TextCell(2, 1, LineBreakText, style: 0),
            sheet.NumberCell(2, 2, 12.5m, style: 0),
            sheet.BlankCell(2, 3, style: 0)
        ]);
        sheet.WriteDataRow(3, [
            sheet.TextCell(3, 1, "合成欄一", style: 0),
            sheet.TextCell(3, 2, "合成第三列", style: 0),
            sheet.NumberCell(3, 3, -7m, style: 0)
        ]);
    }

    private static IReadOnlyDictionary<string, byte[]> TemplateEntries() =>
        Entries(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Templates", ReportTemplateCatalog.CriteriaSelection)));

    private static IReadOnlyDictionary<string, byte[]> Entries(byte[] package)
    {
        using var archive = new ZipArchive(new MemoryStream(package, writable: false), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(entry => entry.FullName, ReadEntry, StringComparer.Ordinal);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var copy = new MemoryStream();
        input.CopyTo(copy);
        return copy.ToArray();
    }

    private static bool IsXml(string name) => name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    private static bool IsRelationships(string name) => name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);

    private static bool IsWorksheet(string name) =>
        name.StartsWith("xl/worksheets/", StringComparison.Ordinal)
        && name.IndexOf('/', "xl/worksheets/".Length) < 0
        && IsXml(name);

    private static XDocument Parse(byte[] content)
    {
        using var reader = XmlReader.Create(new MemoryStream(content, writable: false), ReaderSettings());
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null
    };

    /// <summary>共用字串項目的文字：一般文字與富文字每一段的 t 接起來，不含注音標示。</summary>
    private static string ItemText(XElement item) =>
        string.Concat(item
            .Descendants(Spreadsheet + "t")
            .Where(text => text.Parent?.Name != Spreadsheet + "rPh")
            .Select(text => text.Value));

    /// <summary>所有 c 的 r 涵蓋的範圍，寫法和 Excel 的 dimension 相同；只有一格時寫那一格，沒有格子時寫 A1。</summary>
    private static string WrittenRange(XElement worksheet)
    {
        var cells = worksheet
            .Descendants(Spreadsheet + "c")
            .Select(cell => ParseReference((string)cell.Attribute("r")!))
            .ToArray();
        if (cells.Length == 0)
        {
            return "A1";
        }

        var first = CellReference(cells.Min(cell => cell.Column), cells.Min(cell => cell.Row));
        var last = CellReference(cells.Max(cell => cell.Column), cells.Max(cell => cell.Row));
        return first == last ? first : first + ":" + last;
    }

    private static (uint Column, uint Row) ParseReference(string reference)
    {
        var column = 0U;
        var index = 0;
        while (index < reference.Length && char.IsAsciiLetterUpper(reference[index]))
        {
            column = column * 26U + (uint)(reference[index] - 'A' + 1);
            index++;
        }

        return (column, uint.Parse(reference.AsSpan(index), NumberStyles.None, CultureInfo.InvariantCulture));
    }

    private static string CellReference(uint column, uint row) => ReportSheetWriter.Reference(column, row);
}
