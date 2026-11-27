using System.Globalization;
using System.IO.Compression;
using System.IO.Packaging;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace JET.Infrastructure;

/// <summary>正式輸出檔收尾時，小活頁簿其餘的項目要不要整理成 Excel 存檔時的寫法。</summary>
internal enum FormalWorkbookLayout
{
    /// <summary>
    /// 和 Excel 存檔時相同：關聯的目標用以來源零件為基準的相對路徑，XML 不帶 BOM，主要命名空間用預設命名空間。
    /// 範本原有、沒有改過的項目本來就是這個寫法，照原樣複製。隱藏的中繼資料工作表也和其他工作表一樣寫出 dimension。
    /// 文字寫進共用字串表，格子只放編號；只有 JET 讀回用的隱藏紀錄照樣寫成 inline string。
    /// </summary>
    SameAsExcel,

    /// <summary>小活頁簿其餘的項目照 Open XML SDK 寫出的樣子複製，不整理；隱藏的中繼資料工作表也不加 dimension。</summary>
    AsWritten
}

/// <summary>
/// 正式輸出檔的壓縮檔。工作表內容寫出時直接壓縮進輸出檔，資料列寫出後就不留在記憶體，
/// 明細有幾百萬列也一樣；樣式表、workbook、摘要頁這些小的修改，則留在暫存的小活頁簿裡用 OpenXML SDK 改，
/// 寫完後再把小活頁簿其餘的項目逐一複製進輸出檔。
/// OpenXML SDK 開啟可寫的活頁簿時，會把每個改過的項目整份未壓縮地留在記憶體，等關閉時才壓縮寫出，
/// 單一項目超過 2 GB 還會失敗，所以大量資料不經過它。
/// 壓縮檔同時只能寫一個項目，每個項目只能寫一次，寫過的內容也不能讀回；工作表要一張寫完、關閉後才能開下一張。
/// 選了 <see cref="FormalWorkbookLayout.SameAsExcel"/> 時，這次新寫的共用字串先收在 <see cref="WorkbookSharedStrings"/> 的暫存檔，
/// 收尾時接在範本原有的項目後面，共用字串表最後才寫。
/// 寫完時由 <see cref="Complete"/> 關閉壓縮檔；失敗或取消而沒有寫完時，由 <see cref="Dispose"/> 關閉，共用字串的暫存檔也一起刪除。
/// </summary>
internal sealed class FormalWorkbookArchive : IDisposable
{
    private const string PackageRelationshipsNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    private static readonly XmlReaderSettings EntryReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = false,
        IgnoreWhitespace = false,
        IgnoreProcessingInstructions = false
    };

    private readonly ZipArchive _archive;
    private readonly FormalWorkbookLayout _layout;
    private readonly HashSet<string> _writtenEntries = new(StringComparer.OrdinalIgnoreCase);
    private WorkbookSharedStrings? _sharedStrings;
    private string? _sharedStringsEntry;
    private long _sharedStringReferences;
    private string? _openEntry;
    private Stream? _openContent;
    private bool _completed;
    private bool _disposed;

    internal FormalWorkbookArchive(Stream output, FormalWorkbookLayout layout = FormalWorkbookLayout.SameAsExcel)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!Enum.IsDefined(layout))
        {
            throw new ArgumentOutOfRangeException(nameof(layout));
        }

        _layout = layout;
        _archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
    }

    /// <summary>
    /// 讓 <paramref name="workbook"/> 裡的工作表寫出時，改寫進這個壓縮檔。選了 <see cref="FormalWorkbookLayout.SameAsExcel"/> 時，
    /// 先數範本共用字串表原有的項目數，新寫的文字從這個編號接下去；範本沒有共用字串表時新增一份空的。
    /// </summary>
    internal void Attach(SpreadsheetDocument workbook)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        EnsureWritable();
        if (_layout == FormalWorkbookLayout.SameAsExcel && _sharedStrings is null)
        {
            var workbookPart = workbook.WorkbookPart
                ?? throw new ArgumentException("正式輸出的活頁簿沒有 workbook 零件。", nameof(workbook));
            var part = workbookPart.SharedStringTablePart ?? AddSharedStringTablePart(workbookPart);
            uint templateItems;
            using (var stream = part.GetStream(FileMode.Open, FileAccess.Read))
            using (var reader = XmlReader.Create(stream, EntryReaderSettings))
            {
                templateItems = CountSharedStringItems(reader);
            }

            _sharedStringsEntry = part.Uri.OriginalString.TrimStart('/');
            _sharedStrings = new WorkbookSharedStrings(templateItems);
        }

        workbook.AddAnnotation(this);
    }

    /// <summary>
    /// <paramref name="package"/> 寫進的正式輸出檔收集共用字串的地方；不屬於正式輸出，或正式輸出選了
    /// <see cref="FormalWorkbookLayout.AsWritten"/> 時回傳 null，文字寫成 inline string。
    /// </summary>
    internal static WorkbookSharedStrings? SharedStringsOf(OpenXmlPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return package.Annotation<FormalWorkbookArchive>()?._sharedStrings;
    }

    /// <summary><paramref name="package"/> 寫進的正式輸出檔選了哪一種寫法；不屬於正式輸出的活頁簿回傳 null。</summary>
    internal static FormalWorkbookLayout? LayoutOf(OpenXmlPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return package.Annotation<FormalWorkbookArchive>()?._layout;
    }

    /// <summary>
    /// 打開一張工作表的內容來寫，呼叫端寫完後要關閉回傳的串流。這張工作表屬於正式輸出時，內容直接壓縮進輸出檔；
    /// 不屬於正式輸出的活頁簿，寫回工作表自己的 part。
    /// 直接寫進這個串流的內容不經過 XML 寫出程式，參照共用字串表的格子不會算進共用字串表的 count，
    /// 所以只給寫成 inline string 的工作底稿用；其他輸出用 <see cref="OpenWorksheetWriter"/>。
    /// </summary>
    internal static Stream OpenWorksheetContent(WorksheetPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return part.OpenXmlPackage.Annotation<FormalWorkbookArchive>() is { } archive
            ? archive.OpenEntry(part)
            : part.GetStream(FileMode.Create, FileAccess.Write);
    }

    /// <summary>
    /// 打開一張工作表來寫 XML，寫法和 Excel 存檔時相同，見 <see cref="SpreadsheetXmlWriter"/>。
    /// 寫到哪裡和 <see cref="OpenWorksheetContent"/> 相同；呼叫端寫完後關閉回傳的寫出程式，工作表的內容跟著關閉。
    /// 關閉時把這張工作表參照共用字串表的格數加進共用字串表的 count。
    /// </summary>
    internal static SpreadsheetXmlWriter OpenWorksheetWriter(WorksheetPart part)
    {
        var content = OpenWorksheetContent(part);
        try
        {
            var archive = part.OpenXmlPackage.Annotation<FormalWorkbookArchive>();
            return SpreadsheetXmlWriter.Create(content, archive is null ? null : archive.AddSharedStringReferences);
        }
        catch
        {
            ExportResources.ReleaseAll(content);
            throw;
        }
    }

    /// <summary>
    /// 把小活頁簿裡沒有直接寫進輸出檔的項目逐一複製過去，再寫完壓縮檔。小活頁簿要先關閉，內容才是最新的。
    /// 直接寫進輸出檔的工作表，在小活頁簿裡只剩範本原本的內容或空的項目，不複製。
    /// 選了 <see cref="FormalWorkbookLayout.SameAsExcel"/> 時，XML 與關聯項目在這裡整理成 Excel 存檔時的寫法，
    /// 見 <see cref="ArrangeLikeExcel"/>；複製的工作表順便數參照共用字串表的格數，共用字串表等其他項目都寫完才寫，
    /// 見 <see cref="WriteSharedStrings"/>。
    /// </summary>
    internal void Complete(Stream workbook, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        EnsureWritable();
        if (_openEntry is not null)
        {
            throw new InvalidOperationException($"工作表「{_openEntry}」還沒寫完，正式輸出檔不能收尾。");
        }

        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        workbook.Position = 0;
        using (var source = new ZipArchive(workbook, ZipArchiveMode.Read, leaveOpen: true))
        {
            ZipArchiveEntry? sharedStrings = null;
            foreach (var entry in source.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_writtenEntries.Contains(entry.FullName))
                {
                    skipped.Add(entry.FullName);
                    continue;
                }

                if (_sharedStrings is not null
                    && string.Equals(entry.FullName, _sharedStringsEntry, StringComparison.OrdinalIgnoreCase))
                {
                    // 共用字串表的 count 要等所有工作表都數過才知道，所以最後寫。
                    sharedStrings = entry;
                    continue;
                }

                // 範本裡的圖片本來就沒有壓縮，壓縮後大小等於原始大小；這些項目照原樣不壓縮。
                var level = entry.CompressedLength == entry.Length
                    ? CompressionLevel.NoCompression
                    : CompressionLevel.Optimal;
                var target = _archive.CreateEntry(entry.FullName, level);
                target.LastWriteTime = entry.LastWriteTime;
                using var output = target.Open();
                if (_layout == FormalWorkbookLayout.SameAsExcel && IsXmlOrRelationships(entry.FullName))
                {
                    var content = ReadEntry(entry);
                    if (_sharedStrings is not null && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    {
                        _sharedStringReferences += SharedStringReferencesIn(content);
                    }

                    output.Write(ArrangeLikeExcel(entry.FullName, content));
                    continue;
                }

                using var input = entry.Open();
                input.CopyTo(output);
            }

            if (_sharedStrings is not null)
            {
                WriteSharedStrings(
                    sharedStrings ?? throw new InvalidOperationException("活頁簿裡找不到共用字串表，正式輸出檔不能收尾。"),
                    _sharedStrings,
                    cancellationToken);
            }
        }

        // 小活頁簿裡找不到的工作表，表示寫出後又被移除，或是項目名稱對不上；照樣收尾會留下沒有人引用的工作表。
        var orphaned = _writtenEntries.Where(name => !skipped.Contains(name)).Order(StringComparer.Ordinal).ToArray();
        if (orphaned.Length > 0)
        {
            throw new InvalidOperationException(
                $"正式輸出檔裡的工作表在活頁簿裡找不到：{string.Join(", ", orphaned)}。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _archive.Dispose();
        _completed = true;
        // 共用字串已經寫進輸出檔，暫存檔用不到了。
        ExportResources.ReleaseAll(_sharedStrings);
    }

    /// <summary>
    /// 寫出共用字串表：根元素的屬性除了 count 與 uniqueCount 照範本，範本原有的項目照讀到的逐個節點寫出，
    /// 這次新寫的項目從暫存檔照位元組接在後面、extLst 之前。count 是所有工作表參照共用字串表的格數，
    /// uniqueCount 是範本的項目加上新寫的項目。新寫的編號從開始寫時範本的項目數接下去；寫出期間範本的項目數變了，
    /// 已經寫出的編號就會指錯文字，這是程式錯誤，丟出例外，不寫出這份檔案。
    /// </summary>
    private void WriteSharedStrings(ZipArchiveEntry entry, WorkbookSharedStrings sharedStrings, CancellationToken cancellationToken)
    {
        uint templateItems;
        using (var input = entry.Open())
        using (var reader = XmlReader.Create(input, EntryReaderSettings))
        {
            templateItems = CountSharedStringItems(reader);
        }

        if (templateItems != sharedStrings.TemplateItemCount)
        {
            throw new InvalidOperationException(
                $"共用字串表原有 {sharedStrings.TemplateItemCount} 個項目，寫出期間變成 {templateItems} 個，新寫的編號會指錯文字。");
        }

        var target = _archive.CreateEntry(entry.FullName, CompressionLevel.Optimal);
        target.LastWriteTime = entry.LastWriteTime;
        using var source = entry.Open();
        using var template = XmlReader.Create(source, EntryReaderSettings);
        using var writer = SpreadsheetXmlWriter.Create(target.Open());
        writer.WriteStartDocument(standalone: true);
        template.MoveToContent();
        writer.WriteStartElement(string.Empty, "sst", SpreadsheetXmlWriter.MainNamespace);
        if (template.MoveToFirstAttribute())
        {
            do
            {
                if (template.NamespaceURI.Length == 0 && template.LocalName is ("count" or "uniqueCount"))
                {
                    continue;
                }

                writer.WriteStartAttribute(template.Prefix, template.LocalName, template.NamespaceURI);
                writer.WriteString(template.Value);
                writer.WriteEndAttribute();
            }
            while (template.MoveToNextAttribute());

            template.MoveToElement();
        }

        writer.WriteAttributeString("count", _sharedStringReferences.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("uniqueCount", sharedStrings.ItemCount.ToString(CultureInfo.InvariantCulture));
        var addedItemsWritten = false;
        if (!template.IsEmptyElement)
        {
            var depth = template.Depth;
            template.Read();
            while (!template.EOF && template.Depth > depth)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!addedItemsWritten && IsMainElement(template, "extLst"))
                {
                    writer.WritePreparedContent(sharedStrings.CopyAddedItemsTo, cancellationToken);
                    addedItemsWritten = true;
                }

                writer.WriteNode(template, defattr: true);
            }
        }

        if (!addedItemsWritten)
        {
            writer.WritePreparedContent(sharedStrings.CopyAddedItemsTo, cancellationToken);
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    /// <summary>從共用字串表的根元素起，數直接放在根元素下的 si 有幾個。</summary>
    private static uint CountSharedStringItems(XmlReader reader)
    {
        reader.MoveToContent();
        if (!IsMainElement(reader, "sst"))
        {
            throw new InvalidDataException("共用字串表的根元素不是 sst。");
        }

        if (reader.IsEmptyElement)
        {
            return 0;
        }

        var depth = reader.Depth;
        var items = 0U;
        reader.Read();
        while (!reader.EOF && reader.Depth > depth)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            if (IsMainElement(reader, "si"))
            {
                items = checked(items + 1);
            }

            reader.Skip();
        }

        return items;
    }

    /// <summary>根元素是工作表時，數主要命名空間裡 t="s" 的 c 元素；其他項目回傳 0。</summary>
    private static long SharedStringReferencesIn(byte[] content)
    {
        using var reader = XmlReader.Create(new MemoryStream(content, writable: false), EntryReaderSettings);
        reader.MoveToContent();
        if (!IsMainElement(reader, "worksheet"))
        {
            return 0;
        }

        var references = 0L;
        while (reader.Read())
        {
            if (IsMainElement(reader, "c") && string.Equals(reader.GetAttribute("t"), "s", StringComparison.Ordinal))
            {
                references++;
            }
        }

        return references;
    }

    private static bool IsMainElement(XmlReader reader, string localName) =>
        reader.NodeType == XmlNodeType.Element
        && string.Equals(reader.LocalName, localName, StringComparison.Ordinal)
        && string.Equals(reader.NamespaceURI, SpreadsheetXmlWriter.MainNamespace, StringComparison.Ordinal);

    /// <summary>範本沒有共用字串表時，新增一份空的，存回小活頁簿；收尾時再寫成 Excel 的寫法。</summary>
    private static SharedStringTablePart AddSharedStringTablePart(WorkbookPart workbookPart)
    {
        var part = workbookPart.AddNewPart<SharedStringTablePart>();
        part.SharedStringTable = new SharedStringTable();
        part.SharedStringTable.Save();
        return part;
    }

    private void AddSharedStringReferences(long references) => _sharedStringReferences += references;

    /// <summary>
    /// 把小活頁簿裡一個 XML 或關聯項目整理成 Excel 存檔時的寫法，內容不變：
    /// 去掉開頭的 BOM；關聯的目標從根目錄起算時，改成以來源零件為基準的相對路徑；
    /// 根元素的主要命名空間帶前綴時，整份改用預設命名空間重寫。
    /// Open XML SDK 新增零件時一律把關聯寫成從根目錄起算的路徑，存關聯與內容類型時帶 BOM，
    /// 用 DOM 存回的零件一律加前綴 x，而且都沒有設定能改，所以在收尾時統一整理。
    /// 範本原有、沒有改過的項目不符合任何一種情形，內容照原樣。
    /// </summary>
    private static byte[] ArrangeLikeExcel(string entryName, byte[] content)
    {
        var result = WithoutByteOrderMark(content);
        if (entryName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
        {
            return WithRelativeTargets(entryName, result);
        }

        return WithDefaultMainNamespace(result);
    }

    private static bool IsXmlOrRelationships(string entryName) =>
        entryName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
        || entryName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var copy = new MemoryStream();
        input.CopyTo(copy);
        return copy.ToArray();
    }

    private static byte[] WithoutByteOrderMark(byte[] content) =>
        content.AsSpan().StartsWith(Encoding.UTF8.Preamble)
            ? content[Encoding.UTF8.Preamble.Length..]
            : content;

    /// <summary>
    /// 關聯的目標依規範以來源零件所在的位置為基準解析，Excel 也這樣寫，例如 workbook.xml 指到工作表寫成 worksheets/sheet2.xml。
    /// 只改不是外部連結、而且從根目錄起算的目標；套件層級的關聯以套件根目錄為基準，去掉開頭的斜線就是相對路徑。
    /// 沒有這種目標的關聯項目照原樣。
    /// </summary>
    private static byte[] WithRelativeTargets(string entryName, byte[] content)
    {
        XDocument document;
        using (var reader = XmlReader.Create(new MemoryStream(content, writable: false), EntryReaderSettings))
        {
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }

        var source = PackUriHelper.GetSourcePartUriFromRelationshipPartUri(new Uri("/" + entryName, UriKind.Relative));
        var packageLevel = string.Equals(source.OriginalString, "/", StringComparison.Ordinal);
        XNamespace relationships = PackageRelationshipsNamespace;
        var changed = false;
        foreach (var relationship in document.Root?.Elements(relationships + "Relationship") ?? [])
        {
            var target = relationship.Attribute("Target");
            if (target is null
                || !target.Value.StartsWith('/')
                || string.Equals((string?)relationship.Attribute("TargetMode"), "External", StringComparison.Ordinal))
            {
                continue;
            }

            target.Value = packageLevel
                ? target.Value[1..]
                : PackUriHelper.GetRelativeUri(source, new Uri(target.Value, UriKind.Relative)).OriginalString;
            changed = true;
        }

        if (!changed)
        {
            return content;
        }

        using var output = new MemoryStream(content.Length);
        using (var writer = SpreadsheetXmlWriter.Create(output))
        {
            writer.WriteStartDocument(standalone: true);
            document.Root!.WriteTo(writer);
            writer.WriteEndDocument();
        }

        return output.ToArray();
    }

    /// <summary>
    /// 根元素的主要命名空間帶前綴時，用 <see cref="SpreadsheetXmlWriter"/> 逐個節點重寫，元素改用預設命名空間；
    /// 其他命名空間、屬性、空白與文字照讀到的寫出。根元素原本就是預設命名空間的項目照原樣。
    /// </summary>
    private static byte[] WithDefaultMainNamespace(byte[] content)
    {
        using var reader = XmlReader.Create(new MemoryStream(content, writable: false), EntryReaderSettings);
        reader.MoveToContent();
        if (reader.NodeType != XmlNodeType.Element
            || reader.Prefix.Length == 0
            || !string.Equals(reader.NamespaceURI, SpreadsheetXmlWriter.MainNamespace, StringComparison.Ordinal))
        {
            return content;
        }

        using var output = new MemoryStream(content.Length);
        using (var writer = SpreadsheetXmlWriter.Create(output))
        {
            writer.WriteStartDocument(standalone: true);
            writer.WriteNode(reader, defattr: true);
            writer.WriteEndDocument();
        }

        return output.ToArray();
    }

    private Stream OpenEntry(WorksheetPart part)
    {
        EnsureWritable();
        var name = part.Uri.OriginalString.TrimStart('/');
        if (_openEntry is not null)
        {
            throw new InvalidOperationException(
                $"正式輸出檔同時只能寫一張工作表：「{_openEntry}」還沒寫完，就要開始寫「{name}」。");
        }

        if (!_writtenEntries.Add(name))
        {
            throw new InvalidOperationException($"工作表「{name}」已經寫進正式輸出檔，不能再寫一次。");
        }

        var entry = _archive.CreateEntry(name, CompressionLevel.Optimal);
        var content = new EntryContentStream(
            entry.Open(),
            () =>
            {
                _openEntry = null;
                _openContent = null;
            });
        _openEntry = name;
        _openContent = content;
        return content;
    }

    /// <summary>
    /// 放掉還開著的工作表項目並關閉壓縮檔，不丟出例外。已經寫完的壓縮檔在 <see cref="Complete"/> 就關閉了，這裡不再做事。
    /// 沒有寫完就關閉時，壓縮檔仍會把目錄補寫到輸出；這份輸出少了活頁簿其餘的項目，呼叫端不會留用，寫不進去也不往外丟。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ExportResources.ReleaseAll(_openContent, _archive, _sharedStrings);
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("正式輸出檔已經寫完，不能再寫入。");
        }
    }

    /// <summary>寫進壓縮檔一個項目的串流；關閉時通知壓縮檔可以開始寫下一個項目。</summary>
    private sealed class EntryContentStream(Stream inner, Action closed) : Stream
    {
        private bool _disposed;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => !_disposed;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        public override void WriteByte(byte value) => inner.WriteByte(value);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                try
                {
                    inner.Dispose();
                }
                finally
                {
                    closed();
                }
            }

            base.Dispose(disposing);
        }
    }
}
