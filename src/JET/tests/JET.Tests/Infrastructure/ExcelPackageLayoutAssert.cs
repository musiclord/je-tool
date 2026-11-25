using System.IO.Compression;
using System.IO.Packaging;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 直接讀正式報告輸出的壓縮檔，核對寫法和 Excel 存檔時相同，用同一組檢查套在每一張工作表與每一個 XML 項目上：
/// <list type="bullet">
/// <item>關聯的目標用以來源零件為基準的相對路徑，而且解析得到壓縮檔裡的項目。</item>
/// <item>XML 與關聯項目不帶 BOM。</item>
/// <item>SpreadsheetML 的主要命名空間用預設命名空間，元素不帶前綴。</item>
/// <item>每一張工作表的 dimension 等於實際寫出的格子範圍。</item>
/// <item>文字寫進共用字串表，編號都在表裡，count 與 uniqueCount 正確，表的開頭仍是範本原有的項目；JET 讀回用的紀錄照樣是 inline string。</item>
/// <item>文字裡沒有寫成字元參照的 CR。</item>
/// </list>
/// 每一項不符的地方回傳一筆說明，只寫項目名稱、工作表與格子位置，不寫格子的文字。
/// </summary>
internal static class ExcelPackageLayoutAssert
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string PackageRelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeRelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string SharedStringsEntry = "xl/sharedStrings.xml";

    private static readonly XNamespace Spreadsheet = SpreadsheetNamespace;
    private static readonly XNamespace PackageRelationships = PackageRelationshipsNamespace;
    private static readonly XNamespace OfficeRelationships = OfficeRelationshipsNamespace;

    internal static void MatchesExcel(byte[] package, string templateName) =>
        Assert.Empty(Problems(package, templateName));

    /// <summary><paramref name="templateName"/> 是這份報告所用範本的檔名，用來核對共用字串表開頭的項目。</summary>
    internal static IReadOnlyList<string> Problems(byte[] package, string templateName)
    {
        var entries = Entries(package);
        var template = Entries(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Templates", templateName)));
        var problems = new List<string>();
        problems.AddRange(RelationshipProblems(entries));
        problems.AddRange(ByteOrderMarkProblems(entries));
        problems.AddRange(PrefixProblems(entries));
        problems.AddRange(WorksheetDimensionAssert.Problems(package));
        problems.AddRange(SharedStringTableAssert.Problems(package));
        problems.AddRange(SharedStringTableAssert.RecordCellsNotInline(package));
        problems.AddRange(TemplateSharedStringProblems(entries, template));
        problems.AddRange(CarriageReturnProblems(entries));
        return problems;
    }

    /// <summary>
    /// JET_Metadata 一定要在，而且 JET 讀回用的格子仍是 inline string：A1 的標記，以及第 2 列起 B 欄的分段至少一格。
    /// <see cref="SharedStringTableAssert.RecordCellsNotInline"/> 只回報不符的格子，沒有這張工作表時也會通過，所以另外核對它真的寫出來了。
    /// </summary>
    internal static IReadOnlyList<string> MetadataRecordProblems(byte[] package)
    {
        var entries = Entries(package);
        if (WorksheetEntry(entries, ReportWorkbookMetadataFormat.WorksheetName) is not { } entry)
        {
            return [$"沒有 {ReportWorkbookMetadataFormat.WorksheetName} 工作表"];
        }

        var cells = Parse(entries[entry]).Descendants(Spreadsheet + "c").ToArray();
        var problems = new List<string>();
        var marker = cells.SingleOrDefault(cell => string.Equals((string?)cell.Attribute("r"), "A1", StringComparison.Ordinal));
        if (!IsInline(marker))
        {
            problems.Add($"{ReportWorkbookMetadataFormat.WorksheetName}：A1 不是 inline string");
        }

        var segments = cells
            .Where(cell => ((string?)cell.Attribute("r"))?.StartsWith('B') == true
                           && !string.Equals((string?)cell.Attribute("r"), "B1", StringComparison.Ordinal))
            .ToArray();
        if (segments.Length == 0)
        {
            problems.Add($"{ReportWorkbookMetadataFormat.WorksheetName}：第 2 列起的 B 欄沒有分段");
        }

        problems.AddRange(segments
            .Where(cell => !IsInline(cell))
            .Select(cell => $"{ReportWorkbookMetadataFormat.WorksheetName}：{(string?)cell.Attribute("r")} 不是 inline string"));
        return problems;
    }

    /// <summary>
    /// <paramref name="textWithLineFeed"/> 是這份報告一定會寫出、中間有 LF 的合成文字。它至少在一個項目裡照 LF 寫出，
    /// 沒有任何項目把它寫成 CR LF，也沒有寫成字元參照的 CR。
    /// </summary>
    internal static IReadOnlyList<string> LineBreakProblems(byte[] package, string textWithLineFeed)
    {
        if (!textWithLineFeed.Contains('\n', StringComparison.Ordinal) || textWithLineFeed.Contains('\r', StringComparison.Ordinal))
        {
            throw new ArgumentException("合成文字要有 LF，而且不能有 CR。", nameof(textWithLineFeed));
        }

        var entries = Entries(package);
        var lineFeed = Encoding.UTF8.GetBytes(textWithLineFeed);
        var carriageReturnLineFeed = Encoding.UTF8.GetBytes(textWithLineFeed.Replace("\n", "\r\n", StringComparison.Ordinal));
        var entitizedCarriageReturn = Encoding.UTF8.GetBytes(textWithLineFeed.Replace("\n", "&#xD;\n", StringComparison.Ordinal));
        var problems = entries
            .Where(entry => entry.Value.AsSpan().IndexOf(carriageReturnLineFeed) >= 0
                            || entry.Value.AsSpan().IndexOf(entitizedCarriageReturn) >= 0)
            .Select(entry => $"{entry.Key}：換行寫成 CR LF")
            .Order(StringComparer.Ordinal)
            .ToList();
        if (!entries.Values.Any(content => content.AsSpan().IndexOf(lineFeed) >= 0))
        {
            problems.Add("沒有任何項目照 LF 寫出含換行的合成文字");
        }

        return problems;
    }

    /// <summary>JET 讀回用的紀錄以外，寫成 inline string 的文字格數；用來確認比較的兩份輸出寫法真的不同。</summary>
    internal static int InlineTextCellCount(byte[] package)
    {
        var entries = Entries(package);
        var workbook = Parse(entries["xl/workbook.xml"]);
        var relationships = WorkbookRelationshipTargets(entries);
        var count = 0;
        foreach (var sheet in workbook.Descendants(Spreadsheet + "sheet"))
        {
            var name = (string?)sheet.Attribute("name") ?? string.Empty;
            if (string.Equals(name, ReportWorkbookMetadataFormat.WorksheetName, StringComparison.Ordinal)
                || string.Equals(name, MappingMetadataFormat.WorksheetName, StringComparison.Ordinal))
            {
                continue;
            }

            var entry = relationships[(string)sheet.Attribute(OfficeRelationships + "id")!];
            count += Parse(entries[entry]).Descendants(Spreadsheet + "c").Count(IsInline);
        }

        return count;
    }

    /// <summary>
    /// 同一份資料用照原樣寫出的寫法與和 Excel 相同的寫法各產生一份，儲存格的內容與外觀都相同。
    /// 內容的比對把共用字串與 inline string 當成同一種文字，外觀的比對不看儲存格型別。
    /// </summary>
    internal static void SameContentAndAppearance(byte[] asWritten, byte[] sameAsExcel)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jet-report-layout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var asWrittenPath = Path.Combine(directory, "as-written.xlsx");
            var sameAsExcelPath = Path.Combine(directory, "same-as-excel.xlsx");
            File.WriteAllBytes(asWrittenPath, asWritten);
            File.WriteAllBytes(sameAsExcelPath, sameAsExcel);

            var asWrittenContent = Path.Combine(directory, "as-written.content.ndjson");
            var sameAsExcelContent = Path.Combine(directory, "same-as-excel.content.ndjson");
            SpreadsheetContentFingerprint.Capture(asWrittenPath, asWrittenContent, "report-layout", _ => SpreadsheetContentHeaderRule.None);
            SpreadsheetContentFingerprint.Capture(sameAsExcelPath, sameAsExcelContent, "report-layout", _ => SpreadsheetContentHeaderRule.None);

            Assert.Null(SpreadsheetContentFingerprint.DescribeFirstDifference(asWrittenContent, sameAsExcelContent));
            Assert.True(SpreadsheetContentFingerprint.FilesEqual(asWrittenContent, sameAsExcelContent));
            Assert.Equal(
                SpreadsheetAppearanceFingerprint.Capture(asWrittenPath).Json,
                SpreadsheetAppearanceFingerprint.Capture(sameAsExcelPath).Json);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static IEnumerable<string> RelationshipProblems(IReadOnlyDictionary<string, byte[]> entries)
    {
        foreach (var (name, content) in entries.Where(entry => IsRelationships(entry.Key)))
        {
            var source = PackUriHelper.GetSourcePartUriFromRelationshipPartUri(new Uri("/" + name, UriKind.Relative));
            foreach (var relationship in Parse(content).Elements(PackageRelationships + "Relationship"))
            {
                if (string.Equals((string?)relationship.Attribute("TargetMode"), "External", StringComparison.Ordinal))
                {
                    continue;
                }

                var target = (string)relationship.Attribute("Target")!;
                if (target.StartsWith('/'))
                {
                    yield return $"{name}：{target} 從根目錄起算";
                    continue;
                }

                var resolved = PackUriHelper.ResolvePartUri(source, new Uri(target, UriKind.Relative))
                    .OriginalString
                    .TrimStart('/');
                if (!entries.ContainsKey(resolved))
                {
                    yield return $"{name}：{target} 解析成 {resolved}，壓縮檔裡沒有這個項目";
                }
            }
        }
    }

    private static IEnumerable<string> ByteOrderMarkProblems(IReadOnlyDictionary<string, byte[]> entries) =>
        entries
            .Where(entry => IsXml(entry.Key) || IsRelationships(entry.Key))
            .Where(entry => entry.Value.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            .Select(entry => $"{entry.Key}：開頭有 BOM")
            .Order(StringComparer.Ordinal);

    private static IEnumerable<string> PrefixProblems(IReadOnlyDictionary<string, byte[]> entries)
    {
        foreach (var (name, content) in entries.Where(entry => IsXml(entry.Key)).OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            using var reader = XmlReader.Create(new MemoryStream(content, writable: false), ReaderSettings());
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element
                    && string.Equals(reader.NamespaceURI, SpreadsheetNamespace, StringComparison.Ordinal)
                    && reader.Prefix.Length > 0)
                {
                    yield return $"{name}：<{reader.Name}> 帶命名空間前綴";
                    break;
                }
            }
        }
    }

    /// <summary>共用字串表開頭的項目和範本原有的項目一一相同。統一字型會改範本項目裡的字型名稱，所以只比文字。</summary>
    private static IEnumerable<string> TemplateSharedStringProblems(
        IReadOnlyDictionary<string, byte[]> entries,
        IReadOnlyDictionary<string, byte[]> template)
    {
        if (!template.TryGetValue(SharedStringsEntry, out var templateSharedStrings))
        {
            yield break;
        }

        if (!entries.TryGetValue(SharedStringsEntry, out var sharedStrings))
        {
            yield return "範本有共用字串表，輸出檔沒有";
            yield break;
        }

        var templateTexts = Parse(templateSharedStrings).Elements(Spreadsheet + "si").Select(ItemText).ToArray();
        var outputTexts = Parse(sharedStrings).Elements(Spreadsheet + "si").Take(templateTexts.Length).Select(ItemText).ToArray();
        if (!templateTexts.SequenceEqual(outputTexts, StringComparer.Ordinal))
        {
            yield return $"共用字串表開頭的 {templateTexts.Length} 個項目和範本原有的項目不同";
        }
    }

    /// <summary>
    /// XML 讀取程式會把原樣寫出的 CR 換成 LF，讀回來還留著的 CR 只可能是字元參照。資料裡的 CR 要寫成 _x000D_，
    /// 換行只寫 LF，所以共用字串表與工作表的文字裡不該有 CR。
    /// </summary>
    private static IEnumerable<string> CarriageReturnProblems(IReadOnlyDictionary<string, byte[]> entries) =>
        entries
            .Where(entry => string.Equals(entry.Key, SharedStringsEntry, StringComparison.Ordinal) || IsWorksheet(entry.Key))
            .Where(entry => Parse(entry.Value)
                .Descendants(Spreadsheet + "t")
                .Any(text => text.Value.Contains('\r', StringComparison.Ordinal)))
            .Select(entry => $"{entry.Key}：文字裡有寫成字元參照的 CR")
            .Order(StringComparer.Ordinal);

    private static string? WorksheetEntry(IReadOnlyDictionary<string, byte[]> entries, string sheetName)
    {
        var sheet = Parse(entries["xl/workbook.xml"])
            .Descendants(Spreadsheet + "sheet")
            .SingleOrDefault(element => string.Equals((string?)element.Attribute("name"), sheetName, StringComparison.Ordinal));
        return sheet is null
            ? null
            : WorkbookRelationshipTargets(entries)[(string)sheet.Attribute(OfficeRelationships + "id")!];
    }

    /// <summary>活頁簿關聯的編號對應到壓縮檔裡的項目名稱。</summary>
    private static Dictionary<string, string> WorkbookRelationshipTargets(IReadOnlyDictionary<string, byte[]> entries)
    {
        var source = new Uri("/xl/workbook.xml", UriKind.Relative);
        return Parse(entries["xl/_rels/workbook.xml.rels"])
            .Elements(PackageRelationships + "Relationship")
            .Where(relationship => !string.Equals((string?)relationship.Attribute("TargetMode"), "External", StringComparison.Ordinal))
            .ToDictionary(
                relationship => (string)relationship.Attribute("Id")!,
                relationship => PackUriHelper.ResolvePartUri(source, new Uri((string)relationship.Attribute("Target")!, UriKind.Relative))
                    .OriginalString
                    .TrimStart('/'),
                StringComparer.Ordinal);
    }

    private static bool IsInline(XElement? cell) =>
        cell is not null && string.Equals((string?)cell.Attribute("t"), "inlineStr", StringComparison.Ordinal);

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

    private static XElement Parse(byte[] content)
    {
        using var reader = XmlReader.Create(new MemoryStream(content, writable: false), ReaderSettings());
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root!;
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
}
