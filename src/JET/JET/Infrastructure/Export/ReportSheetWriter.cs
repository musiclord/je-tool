using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;

namespace JET.Infrastructure;

internal sealed record ReportColumn(
    uint Min,
    uint Max,
    double? Width = null,
    bool Hidden = false,
    bool BestFit = false,
    uint? Style = null);

internal sealed record ReportPageMargins(
    double Left,
    double Right,
    double Top,
    double Bottom,
    double Header,
    double Footer);

internal sealed record ReportPageSetup(
    OrientationValues? Orientation = null,
    uint? PaperSize = null,
    uint? Scale = null,
    uint? FitToWidth = null,
    uint? FitToHeight = null);

/// <summary>
/// 報表工作表可被本地範本結構盤點證明的顯示／列印設定。未指定的屬性不寫入，
/// 避免把未觀察到的 Excel 偏好臆造成版型契約。
/// </summary>
internal sealed record ReportSheetOptions(
    bool? ShowGridLines = null,
    uint? ZoomScale = null,
    uint? ZoomScaleNormal = null,
    uint FreezeRows = 0,
    uint FreezeColumns = 0,
    double? DefaultRowHeight = null,
    ReportPageMargins? PageMargins = null,
    ReportPageSetup? PageSetup = null);

/// <summary>六份報告共用的 forward-only worksheet 原語。</summary>
internal sealed class ReportSheetWriter : IDisposable
{
    private readonly string _name;
    private readonly OpenXmlWriter _writer;
    private readonly List<string> _merges = [];
    private readonly List<(string Range, string Formula)> _validations = [];
    private readonly ReportSheetOptions? _options;
    private readonly Func<uint, uint> _mapStyle;
    private bool _protect;
    private bool _closed;
    private bool _disposed;
    private long _dataRows;

    public ReportSheetWriter(
        string name,
        WorksheetPart part,
        IReadOnlyList<ReportColumn>? columns = null,
        ReportSheetOptions? options = null,
        Func<uint, uint>? mapStyle = null)
    {
        _name = name;
        _options = options;
        _mapStyle = mapStyle ?? (static style => style);
        _writer = OpenXmlPartWriter.Create(part);
        _writer.WriteStartDocument();
        _writer.WriteStartElement(new Worksheet());

        WriteSheetView(options);
        if (options?.DefaultRowHeight is { } defaultRowHeight)
        {
            if (!double.IsFinite(defaultRowHeight) || defaultRowHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Default row height must be finite and positive.");
            }
            _writer.WriteElement(new SheetFormatProperties
            {
                DefaultRowHeight = defaultRowHeight,
                CustomHeight = true
            });
        }

        if (columns is { Count: > 0 })
        {
            var collection = new Columns();
            foreach (var column in columns)
            {
                var element = new Column
                {
                    Min = column.Min,
                    Max = column.Max,
                    Hidden = column.Hidden,
                    BestFit = column.BestFit,
                    Style = column.Style is { } style ? _mapStyle(style) : null
                };
                if (column.Width is { } width)
                {
                    if (!double.IsFinite(width) || width <= 0)
                    {
                        throw new ArgumentOutOfRangeException(nameof(columns), "Column width must be finite and positive.");
                    }
                    element.Width = width;
                    element.CustomWidth = true;
                }
                collection.Append(element);
            }

            _writer.WriteElement(collection);
        }

        _writer.WriteStartElement(new SheetData());
    }

    public void WriteFixedRow(uint rowIndex, IReadOnlyList<Cell> cells, double? height = null) =>
        WriteRowCore(rowIndex, cells, height);

    public void WriteDataRow(uint rowIndex, IReadOnlyList<Cell> cells, double? height = null)
    {
        WriteRowCore(rowIndex, cells, height);
        _dataRows++;
    }

    public void AddMerge(string range) => _merges.Add(range);

    public void AddListValidation(string range, string formula) => _validations.Add((range, formula));

    public void Protect() => _protect = true;

    public Cell TextCell(uint row, uint column, string? value, uint style = LegacyReportStyles.Body)
    {
        var cell = new Cell { CellReference = Reference(column, row), StyleIndex = _mapStyle(style) };
        if (!string.IsNullOrEmpty(value))
        {
            cell.DataType = CellValues.InlineString;
            cell.InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve });
        }

        return cell;
    }

    public Cell NumberCell(uint row, uint column, decimal value, uint style = LegacyReportStyles.Amount) => new()
    {
        CellReference = Reference(column, row),
        StyleIndex = _mapStyle(style),
        DataType = CellValues.Number,
        CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture))
    };

    public Cell BlankCell(uint row, uint column, uint style = LegacyReportStyles.Body) => new()
    {
        CellReference = Reference(column, row),
        StyleIndex = _mapStyle(style)
    };

    public SheetStat CloseAndSummarize(CancellationToken cancellationToken)
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

        CloseElements();
        _writer.Dispose();
        _disposed = true;
    }

    public static string Reference(uint column, uint row)
    {
        ExcelWorksheetConstraints.EnsureCell(row, column);
        return ColumnLetters(column) + row.ToString(CultureInfo.InvariantCulture);
    }

    private void WriteRowCore(uint rowIndex, IReadOnlyList<Cell> cells, double? height)
    {
        ExcelWorksheetConstraints.EnsureCell(rowIndex, 1);
        var row = new Row { RowIndex = rowIndex };
        if (height is not null)
        {
            row.Height = height;
            row.CustomHeight = true;
        }

        foreach (var cell in cells)
        {
            row.Append(cell);
        }

        _writer.WriteElement(row);
    }

    private void CloseElements()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _writer.WriteEndElement();

        if (_protect)
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

        if (_merges.Count > 0)
        {
            var merges = new MergeCells { Count = (uint)_merges.Count };
            foreach (var range in _merges)
            {
                merges.Append(new MergeCell { Reference = range });
            }

            _writer.WriteElement(merges);
        }

        if (_validations.Count > 0)
        {
            var validations = new DataValidations { Count = (uint)_validations.Count };
            foreach (var (range, formula) in _validations)
            {
                var validation = new DataValidation
                {
                    Type = DataValidationValues.List,
                    AllowBlank = true,
                    ShowErrorMessage = true,
                    SequenceOfReferences = new ListValue<StringValue> { InnerText = range }
                };
                validation.Append(new Formula1(formula));
                validations.Append(validation);
            }

            _writer.WriteElement(validations);
        }

        if (_options?.PageMargins is { } margins)
        {
            _writer.WriteElement(new PageMargins
            {
                Left = margins.Left,
                Right = margins.Right,
                Top = margins.Top,
                Bottom = margins.Bottom,
                Header = margins.Header,
                Footer = margins.Footer
            });
        }

        if (_options?.PageSetup is { } setup)
        {
            _writer.WriteElement(new PageSetup
            {
                Orientation = setup.Orientation,
                PaperSize = setup.PaperSize,
                Scale = setup.Scale,
                FitToWidth = setup.FitToWidth,
                FitToHeight = setup.FitToHeight
            });
        }

        _writer.WriteEndElement();
    }

    private void WriteSheetView(ReportSheetOptions? options)
    {
        if (options is null
            || (options.ShowGridLines is null
                && options.ZoomScale is null
                && options.ZoomScaleNormal is null
                && options.FreezeRows == 0
                && options.FreezeColumns == 0))
        {
            return;
        }

        if (options.FreezeRows >= ExcelWorksheetConstraints.MaxRows
            || options.FreezeColumns >= ExcelWorksheetConstraints.MaxColumns)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Freeze pane split exceeds Excel worksheet bounds.");
        }

        var view = new SheetView
        {
            WorkbookViewId = 0U,
            ShowGridLines = options.ShowGridLines,
            ZoomScale = options.ZoomScale,
            ZoomScaleNormal = options.ZoomScaleNormal
        };
        if (options.FreezeRows > 0 || options.FreezeColumns > 0)
        {
            view.Append(new Pane
            {
                HorizontalSplit = options.FreezeColumns > 0 ? options.FreezeColumns : null,
                VerticalSplit = options.FreezeRows > 0 ? options.FreezeRows : null,
                TopLeftCell = Reference(options.FreezeColumns + 1, options.FreezeRows + 1),
                ActivePane = options switch
                {
                    { FreezeRows: > 0, FreezeColumns: > 0 } => PaneValues.BottomRight,
                    { FreezeColumns: > 0 } => PaneValues.TopRight,
                    _ => PaneValues.BottomLeft
                },
                State = PaneStateValues.Frozen
            });
        }

        _writer.WriteElement(new SheetViews(view));
    }

    private static string ColumnLetters(uint column)
    {
        var letters = string.Empty;
        while (column > 0)
        {
            var remainder = (int)((column - 1) % 26);
            letters = (char)('A' + remainder) + letters;
            column = (column - 1) / 26;
        }

        return letters;
    }
}
