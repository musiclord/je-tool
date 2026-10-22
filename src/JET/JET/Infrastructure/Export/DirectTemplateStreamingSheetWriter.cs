using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 保留 fixed-template worksheet 的 bounded static skeleton，並從指定列起直接
/// SAX append 動態資料。來源 worksheet 只含固定列，故 clone 大小有界；動態列
/// 從不回存 DOM。dimension 省略，讓 Excel 依實際 sheetData 重建 used range。
/// </summary>
internal sealed class DirectTemplateStreamingSheetWriter : IDisposable
{
    private readonly string _name;
    private readonly WorksheetPart _part;
    private readonly OpenXmlWriter _writer;
    private readonly IReadOnlyList<OpenXmlElement> _afterSheetData;
    private readonly bool _ensureProtection;
    private bool _closed;
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
        var source = (Worksheet)part.Worksheet.CloneNode(true);
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

        _writer = OpenXmlPartWriter.Create(part);
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

    internal void WriteDataRow(
        uint rowIndex,
        IReadOnlyCollection<Cell> cells,
        double? height = null)
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

        _dataRows++;
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
        Dispose();
        return stat;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // 寫列時丟出例外後 XML 停在半途，再寫結束標記只會丟出新的例外，蓋掉原本的失敗原因。
        if (!_closed && !_faulted)
        {
            _writer.WriteEndElement();
            if (_ensureProtection
                && !_afterSheetData.Any(element => element is SheetProtection))
            {
                _writer.WriteElement(new SheetProtection
                {
                    Sheet = true,
                    Objects = true,
                    Scenarios = true,
                    SelectLockedCells = false,
                    SelectUnlockedCells = false
                });
            }
            foreach (var element in _afterSheetData)
            {
                _writer.WriteElement(element);
            }
            _writer.WriteEndElement();
            _closed = true;
        }

        _writer.Dispose();
        _disposed = true;
    }
}
