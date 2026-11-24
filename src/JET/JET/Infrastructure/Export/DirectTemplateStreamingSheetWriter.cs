using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 重寫固定範本裡的一張工作表：範本原有的固定列與其他設定照原樣保留，資料列從指定的列起
/// 用 OpenXmlWriter 一列一列直接寫進檔案。範本的工作表只有固定列，所以複製一份放在記憶體的大小有上限；
/// 資料列寫出後不留在記憶體。範本的 dimension 元素不寫出，讓 Excel 依實際的 sheetData 重新算出使用範圍。
/// </summary>
internal sealed class DirectTemplateStreamingSheetWriter : IDisposable
{
    private readonly string _name;
    private readonly WorksheetPart _part;
    private readonly Stream _content;
    private readonly OpenXmlWriter _writer;
    private readonly IReadOnlyList<OpenXmlElement> _afterSheetData;
    private readonly bool _ensureProtection;
    private bool _faulted;
    private bool _disposed;
    private long _dataRows;

    internal DirectTemplateStreamingSheetWriter(
        string name,
        WorksheetPart part,
        uint firstDynamicRow,
        bool ensureProtection = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(part);
        if (firstDynamicRow == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstDynamicRow));
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

        var children = source.ChildElements.ToArray();
        var sheetDataIndex = Array.IndexOf(children, sheetData);
        if (sheetDataIndex < 0)
        {
            throw new InvalidDataException(
                $"Template worksheet '{name}' has an invalid sheetData position.");
        }

        var before = children
            .Take(sheetDataIndex)
            .Where(element => element is not SheetDimension)
            .Select(element => element.CloneNode(true))
            .ToArray();
        _afterSheetData = children
            .Skip(sheetDataIndex + 1)
            .Select(element => element.CloneNode(true))
            .ToArray();

        _content = FormalWorkbookArchive.OpenWorksheetContent(part);
        try
        {
            _writer = OpenXmlWriter.Create(_content);
            _writer.WriteStartDocument();
            _writer.WriteStartElement(new Worksheet());
            foreach (var element in before)
            {
                _writer.WriteElement(element);
            }
            _writer.WriteStartElement(new SheetData());
            foreach (var row in staticRows)
            {
                _writer.WriteElement(row);
            }
        }
        catch
        {
            // 建構失敗時呼叫端拿不到這個寫入器，已經開啟的項目要在這裡放掉。
            ExportResources.ReleaseAll(_writer, _content);
            throw;
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
        row.Append(cells);
        try
        {
            _writer.WriteElement(row);
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
    /// </summary>
    private void Close()
    {
        try
        {
            if (!_disposed && !_faulted)
            {
                _writer.WriteEndElement();
                WriteElementsAfterSheetData();
                _writer.WriteEndElement();
                _writer.Dispose();
                _content.Dispose();
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
        ExportResources.ReleaseAll(_writer, _content);
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
            _writer.WriteElement(element);
        }
        if (needsProtection)
        {
            WriteProtection();
        }
    }

    private void WriteProtection() =>
        _writer.WriteElement(new SheetProtection
        {
            Sheet = true,
            Objects = true,
            Scenarios = true,
            SelectLockedCells = false,
            SelectUnlockedCells = false
        });
}
