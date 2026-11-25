using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 重寫固定範本裡的一張工作表：範本原有的固定列與其他設定照原樣保留，資料列從指定的列起
/// 一列一列直接寫進檔案，XML 的寫法和 Excel 存檔時相同，見 <see cref="FormalWorkbookArchive.OpenWorksheetWriter"/>。
/// 範本的工作表只有固定列，所以複製一份放在記憶體的大小有上限；資料列寫出後不留在記憶體。
/// 工作表開頭的 dimension 寫在所有列之前，所以呼叫端開頁時要說出資料列會寫出的格子範圍，加上範本固定列的格子，
/// 就是這張工作表的 dimension，取代範本原有的那一個。資料列的格子落在範圍外，或關閉時寫出的範圍和宣告的不同，
/// 都是程式算錯了範圍，丟出 <see cref="InvalidOperationException"/>。
/// </summary>
internal sealed class DirectTemplateStreamingSheetWriter : IDisposable
{
    private readonly string _name;
    private readonly WorksheetPart _part;
    private readonly SpreadsheetXmlWriter _writer;
    private readonly IReadOnlyList<OpenXmlElement> _afterSheetData;
    private readonly bool _ensureProtection;
    private readonly SheetUsedRange _declaredRange;
    private SheetUsedRange _writtenRange;
    private bool _faulted;
    private bool _disposed;
    private long _dataRows;

    /// <param name="name">工作表名稱，寫進錯誤訊息。</param>
    /// <param name="part">要重寫的範本工作表。</param>
    /// <param name="firstDynamicRow">第一筆資料列；範本裡這一列以上的列照原樣保留。</param>
    /// <param name="dynamicRange">
    /// 資料列會寫出的格子範圍，從 <paramref name="firstDynamicRow"/> 起往下；不寫資料列時用 <see cref="SheetUsedRange.Empty"/>。
    /// </param>
    /// <param name="ensureProtection">範本沒有工作表保護時，補上不設密碼的保護。</param>
    internal DirectTemplateStreamingSheetWriter(
        string name,
        WorksheetPart part,
        uint firstDynamicRow,
        SheetUsedRange dynamicRange,
        bool ensureProtection = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(part);
        if (firstDynamicRow == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstDynamicRow));
        }

        if (!dynamicRange.IsEmpty && dynamicRange.FirstRow < firstDynamicRow)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dynamicRange),
                $"Dynamic range {dynamicRange} of worksheet '{name}' starts above row {firstDynamicRow}.");
        }

        _name = name;
        _part = part;
        _ensureProtection = ensureProtection;
        var source = (Worksheet)part.RequiredWorksheet().CloneNode(true);
        var sheetData = source.GetFirstChild<SheetData>()
            ?? throw new InvalidDataException(
                $"Template worksheet '{name}' has no sheetData element.");
        var staticRows = sheetData.Elements<Row>()
            .Where(row => (row.RowIndex?.Value ?? 0) < firstDynamicRow)
            .Select(row => row.CloneNode(true))
            .ToArray();
        // 固定列照原樣寫出，它們的格子一開始就算在寫出的範圍裡。
        _writtenRange = SheetUsedRange.OfCells(staticRows.SelectMany(row => row.Descendants<Cell>()));
        _declaredRange = _writtenRange.Union(dynamicRange);

        var children = source.ChildElements.ToArray();
        var sheetDataIndex = Array.IndexOf(children, sheetData);
        if (sheetDataIndex < 0)
        {
            throw new InvalidDataException(
                $"Template worksheet '{name}' has an invalid sheetData position.");
        }

        var before = WithDimension(children.Take(sheetDataIndex), _declaredRange)
            .Select(element => element.CloneNode(true))
            .ToArray();
        _afterSheetData = children
            .Skip(sheetDataIndex + 1)
            .Select(element => element.CloneNode(true))
            .ToArray();

        _writer = FormalWorkbookArchive.OpenWorksheetWriter(part);
        try
        {
            _writer.WriteStartDocument(standalone: true);
            WriteStartWorksheet(source);
            foreach (var element in before)
            {
                element.WriteTo(_writer);
            }
            _writer.WriteStartElement(string.Empty, "sheetData", SpreadsheetXmlWriter.MainNamespace);
            foreach (var row in staticRows)
            {
                row.WriteTo(_writer);
            }
        }
        catch
        {
            // 建構失敗時呼叫端拿不到這個寫入器，已經開啟的項目要在這裡放掉。
            ExportResources.ReleaseAll(_writer);
            throw;
        }
    }

    /// <summary>
    /// sheetData 前面的元素照範本的順序，只把 dimension 換成這張工作表寫出的範圍。範本沒有 dimension 時，
    /// 照工作表的元素順序加在 sheetPr 之後、其他元素之前。
    /// </summary>
    private static IEnumerable<OpenXmlElement> WithDimension(IEnumerable<OpenXmlElement> elements, SheetUsedRange range)
    {
        var dimension = new SheetDimension { Reference = range.Reference };
        var written = false;
        foreach (var element in elements)
        {
            if (element is SheetDimension)
            {
                if (!written)
                {
                    yield return dimension;
                    written = true;
                }

                continue;
            }

            if (!written && element is not SheetProperties)
            {
                yield return dimension;
                written = true;
            }

            yield return element;
        }

        if (!written)
        {
            yield return dimension;
        }
    }

    /// <summary>
    /// 照範本寫出根元素的開始標記：範本宣告的命名空間與根元素的屬性照原樣保留，mc:Ignorable 這類相容性屬性也包含在屬性裡。
    /// 前綴宣告寫在屬性前面，用到那些前綴的屬性才有宣告可用。指向主要命名空間的前綴 x 由寫出程式拿掉，改用預設命名空間。
    /// </summary>
    private void WriteStartWorksheet(Worksheet source)
    {
        _writer.WriteStartElement(string.Empty, "worksheet", SpreadsheetXmlWriter.MainNamespace);
        foreach (var (prefix, uri) in source.NamespaceDeclarations)
        {
            if (prefix.Length > 0)
            {
                _writer.WriteAttributeString("xmlns", prefix, null, uri);
            }
        }

        foreach (var attribute in source.GetAttributes())
        {
            _writer.WriteAttributeString(attribute.Prefix, attribute.LocalName, attribute.NamespaceUri, attribute.Value);
        }
    }

    internal void WriteDataRow(
        uint rowIndex,
        IReadOnlyCollection<Cell> cells,
        double? height = null)
    {
        WriteRowCore(rowIndex, cells, height);
        _dataRows++;
    }

    /// <summary>寫一列不算資料列的格子，例如資料下面只用來鎖定的空白列。</summary>
    internal void WriteFixedRow(uint rowIndex, IReadOnlyCollection<Cell> cells) =>
        WriteRowCore(rowIndex, cells, height: null);

    private void WriteRowCore(
        uint rowIndex,
        IReadOnlyCollection<Cell> cells,
        double? height)
    {
        ExcelWorksheetConstraints.EnsureCell(rowIndex, 1);
        var row = new Row { RowIndex = rowIndex };
        if (height is { } value)
        {
            if (!double.IsFinite(value) || value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height));
            }
            row.Height = value;
            row.CustomHeight = true;
        }
        _writtenRange = _writtenRange.IncludeDeclaredCells(_declaredRange, cells, _name);
        row.Append(cells);
        try
        {
            row.WriteTo(_writer);
        }
        catch (Exception error) when (Fault(error, cells))
        {
            throw;
        }
    }

    /// <summary>寫到一半的輸出之後不再補結束標記。取消不是寫入失敗，不記工作表位置。</summary>
    private bool Fault(Exception error, IEnumerable<Cell> cells)
    {
        _faulted = true;
        if (error is not OperationCanceledException)
        {
            ExcelText.MarkWriteFailure(error, _part, cells);
        }

        return false;
    }

    internal SheetStat CloseAndSummarize(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stat = new SheetStat(_name, _dataRows);
        Close();
        return stat;
    }

    /// <summary>
    /// 寫完結尾並關閉這張工作表。寫結尾、把緩衝寫出去或關閉壓縮檔項目失敗時往外丟，因為這時失敗代表工作表不完整，
    /// 例如輸出檔所在磁碟剛好滿了。失敗後資源已經放掉，呼叫端在 finally 再 Dispose 一次時，往外丟的仍是第一次的錯誤。
    /// 寫列時丟出例外後 XML 停在半途，再寫結束標記只會丟出新的例外，所以那之後不補結尾。
    /// 寫出的格子比開頁時宣告的範圍少時，dimension 已經寫錯了，不補結尾，丟出程式錯誤。
    /// </summary>
    private void Close()
    {
        try
        {
            if (!_disposed && !_faulted)
            {
                SheetUsedRange.EnsureWrittenAsDeclared(_declaredRange, _writtenRange, _name);
                _writer.WriteEndElement();
                WriteElementsAfterSheetData();
                _writer.WriteEndElement();
                _writer.WriteEndDocument();
                _writer.Dispose();
            }
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>
    /// 只放掉資源，不補寫結尾，也不丟出例外。沒有先關閉就釋放，表示匯出已經失敗或取消，這張工作表的內容不要了。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ExportResources.ReleaseAll(_writer);
    }

    /// <summary>
    /// 照範本原來的順序寫回 sheetData 之後的元素。這張工作表要保護、範本又沒有保護時，補上不設密碼的保護。
    /// 工作表的元素順序規定 sheetData 之後只有 sheetCalcPr 排在 sheetProtection 前面，
    /// 所以補上的保護接在 sheetCalcPr 後面、其他元素前面。
    /// </summary>
    private void WriteElementsAfterSheetData()
    {
        var needsProtection = _ensureProtection
            && !_afterSheetData.Any(element => element is SheetProtection);
        foreach (var element in _afterSheetData)
        {
            var protectionGoesHere = needsProtection && element is not SheetCalculationProperties;
            if (protectionGoesHere)
            {
                WriteProtection();
                needsProtection = false;
            }
            element.WriteTo(_writer);
        }
        if (needsProtection)
        {
            WriteProtection();
        }
    }

    private void WriteProtection() =>
        new SheetProtection
        {
            Sheet = true,
            Objects = true,
            Scenarios = true,
            SelectLockedCells = false,
            SelectUnlockedCells = false
        }.WriteTo(_writer);
}
