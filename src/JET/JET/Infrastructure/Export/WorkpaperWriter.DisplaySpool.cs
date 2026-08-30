using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml.Spreadsheet;

namespace JET.Infrastructure;

public sealed partial class WorkpaperWriter
{
    private readonly record struct WorkpaperDisplayCell(
        string? Text,
        string? NumberLiteral,
        uint Style,
        string RenderedText);

    private static WorkpaperDisplayCell ProjectText(
        string? value,
        uint style = WorkpaperStyles.Default) =>
        new(
            Text: value,
            NumberLiteral: null,
            Style: style,
            RenderedText: value ?? string.Empty);

    private static WorkpaperDisplayCell ProjectBlank(uint style = WorkpaperStyles.Default) =>
        new(
            Text: null,
            NumberLiteral: null,
            Style: style,
            RenderedText: string.Empty);

    private static WorkpaperDisplayCell ProjectInteger(
        long value,
        bool groupedForWidth = false) =>
        new(
            Text: null,
            NumberLiteral: value.ToString(CultureInfo.InvariantCulture),
            Style: WorkpaperStyles.Default,
            RenderedText: value.ToString(
                groupedForWidth ? "#,##0" : "0",
                CultureInfo.InvariantCulture));

    private static WorkpaperDisplayCell ProjectAmount(long scaled, int moneyScale)
    {
        var value = Display(scaled, moneyScale);
        return new WorkpaperDisplayCell(
            Text: null,
            NumberLiteral: value.ToString(CultureInfo.InvariantCulture),
            Style: WorkpaperStyles.Amount,
            RenderedText: value.ToString("#,##0.0000", CultureInfo.InvariantCulture));
    }

    private static IReadOnlyList<Cell> ProjectedCells(
        SheetWriter sheet,
        uint row,
        IReadOnlyList<WorkpaperDisplayCell> projected,
        uint firstColumn)
    {
        var cells = new Cell[projected.Count];
        for (var index = 0; index < projected.Count; index++)
        {
            var column = checked(firstColumn + (uint)index);
            var value = projected[index];
            cells[index] = value.NumberLiteral is { } numberLiteral
                ? sheet.NumberLiteralCell(row, column, numberLiteral, value.Style)
                : sheet.TextCell(row, column, value.Text, value.Style);
        }

        return cells;
    }

    private static async Task FillDisplaySpoolAsync(
        WorkpaperDisplaySpool spool,
        IAsyncEnumerable<IReadOnlyList<WorkpaperDisplayCell>> rows,
        CancellationToken cancellationToken)
    {
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            spool.Append(row);
        }

        spool.Complete();
    }

    private static async IAsyncEnumerable<IReadOnlyList<WorkpaperDisplayCell>>
        ProjectRowsAsync<T>(
            IAsyncEnumerable<T> rows,
            Func<T, IReadOnlyList<WorkpaperDisplayCell>> project,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
    {
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return project(row);
        }
    }

    private static async IAsyncEnumerable<IReadOnlyList<WorkpaperDisplayCell>>
        ReplayDisplaySpoolAsync(
            WorkpaperDisplaySpool spool,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        foreach (var row in spool.ReadRows(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }
    }

    private static IReadOnlyList<double> MeasureDisplayWidths(
        IReadOnlyList<string?> headers,
        IReadOnlyList<double> minimumWidths,
        IEnumerable<IReadOnlyList<string?>> rows)
    {
        ValidateDisplaySchema(headers, minimumWidths);
        var maximums = headers.Select(ExcelDisplayWidth.Measure).ToArray();
        foreach (var row in rows)
        {
            if (row.Count != maximums.Length)
            {
                throw new InvalidDataException(
                    $"WorkingPaper display row column count {row.Count} "
                    + $"does not match schema count {maximums.Length}.");
            }

            for (var index = 0; index < row.Count; index++)
            {
                maximums[index] = Math.Max(
                    maximums[index],
                    ExcelDisplayWidth.Measure(row[index]));
            }
        }

        return maximums
            .Select((maximum, index) => Math.Max(
                minimumWidths[index],
                ExcelDisplayWidth.ToColumnWidth(maximum)))
            .ToArray();
    }

    private static void ApplyDisplayWidths(
        SheetWriter sheet,
        uint firstColumn,
        IReadOnlyList<double> widths)
    {
        for (var index = 0; index < widths.Count; index++)
        {
            var column = checked(firstColumn + (uint)index);
            sheet.SetColumnWidth(
                column,
                column,
                widths[index],
                bestFit: true);
        }
    }

    private static void ValidateDisplaySchema(
        IReadOnlyList<string?> headers,
        IReadOnlyList<double> minimumWidths)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(minimumWidths);
        if (headers.Count == 0 || headers.Count != minimumWidths.Count)
        {
            throw new ArgumentException(
                "WorkingPaper display headers and minimum widths must have the same non-zero count.");
        }
        if (minimumWidths.Any(width =>
                !double.IsFinite(width)
                || width <= 0
                || width > 255))
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumWidths),
                "WorkingPaper minimum column widths must be finite values in (0, 255].");
        }
    }

    /// <summary>
    /// Worksheet cols must precede sheetData. This DeleteOnClose spool bridges that
    /// constraint without retaining source populations or starting a second query:
    /// one source pass records only projected cells, styles, and rendered widths;
    /// one bounded replay writes the worksheet.
    /// </summary>
    private sealed class WorkpaperDisplaySpool : IDisposable
    {
        private readonly FileStream _stream;
        private readonly BinaryWriter _writer;
        private readonly double[] _minimumWidths;
        private readonly double[] _maximums;
        private BinaryReader? _reader;
        private bool _completed;
        private bool _readStarted;
        private bool _disposed;

        private WorkpaperDisplaySpool(
            FileStream stream,
            IReadOnlyList<string?> headers,
            IReadOnlyList<double> minimumWidths)
        {
            ValidateDisplaySchema(headers, minimumWidths);
            _stream = stream;
            _writer = new BinaryWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                leaveOpen: true);
            _minimumWidths = minimumWidths.ToArray();
            _maximums = headers.Select(ExcelDisplayWidth.Measure).ToArray();
        }

        internal long RowCount { get; private set; }

        internal int ColumnCount => _maximums.Length;

        internal IReadOnlyList<double> Widths
        {
            get
            {
                if (!_completed)
                {
                    throw new InvalidOperationException(
                        "WorkingPaper display spool is not complete.");
                }

                return _maximums
                    .Select((maximum, index) => Math.Max(
                        _minimumWidths[index],
                        ExcelDisplayWidth.ToColumnWidth(maximum)))
                    .ToArray();
            }
        }

        internal static WorkpaperDisplaySpool Create(
            IReadOnlyList<string?> headers,
            IReadOnlyList<double> minimumWidths)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $".jet-workpaper-display-{Guid.NewGuid():N}.tmp");
            var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            try
            {
                return new WorkpaperDisplaySpool(stream, headers, minimumWidths);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        internal void Append(IReadOnlyList<WorkpaperDisplayCell> values)
        {
            ThrowIfDisposed();
            if (_completed)
            {
                throw new InvalidOperationException(
                    "WorkingPaper display spool is already complete.");
            }
            if (values.Count != ColumnCount)
            {
                throw new InvalidDataException(
                    $"WorkingPaper projected value count {values.Count} "
                    + $"does not match schema count {ColumnCount}.");
            }

            for (var index = 0; index < values.Count; index++)
            {
                var value = values[index];
                _maximums[index] = Math.Max(
                    _maximums[index],
                    ExcelDisplayWidth.Measure(value.RenderedText));
                if (value.NumberLiteral is { } numberLiteral)
                {
                    _writer.Write((byte)2);
                    _writer.Write(value.Style);
                    _writer.Write(numberLiteral);
                }
                else if (value.Text is { } text)
                {
                    _writer.Write((byte)1);
                    _writer.Write(value.Style);
                    _writer.Write(text);
                }
                else
                {
                    _writer.Write((byte)0);
                    _writer.Write(value.Style);
                }
            }

            RowCount = checked(RowCount + 1);
        }

        internal void Complete()
        {
            ThrowIfDisposed();
            if (_completed)
            {
                throw new InvalidOperationException(
                    "WorkingPaper display spool is already complete.");
            }

            _writer.Flush();
            _stream.Position = 0;
            _reader = new BinaryReader(
                _stream,
                Encoding.UTF8,
                leaveOpen: true);
            _completed = true;
        }

        internal IEnumerable<IReadOnlyList<WorkpaperDisplayCell>> ReadRows(
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!_completed || _reader is null)
            {
                throw new InvalidOperationException(
                    "WorkingPaper display spool is not complete.");
            }
            if (_readStarted)
            {
                throw new InvalidOperationException(
                    "WorkingPaper display spool can only be replayed once.");
            }
            _readStarted = true;

            for (var row = 0L; row < RowCount; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = new WorkpaperDisplayCell[ColumnCount];
                for (var index = 0; index < ColumnCount; index++)
                {
                    var kind = _reader.ReadByte();
                    var style = _reader.ReadUInt32();
                    values[index] = kind switch
                    {
                        0 => new WorkpaperDisplayCell(
                            null,
                            null,
                            style,
                            string.Empty),
                        1 => new WorkpaperDisplayCell(
                            _reader.ReadString(),
                            null,
                            style,
                            string.Empty),
                        2 => new WorkpaperDisplayCell(
                            null,
                            _reader.ReadString(),
                            style,
                            string.Empty),
                        _ => throw new InvalidDataException(
                            $"WorkingPaper display spool cell kind '{kind}' is invalid.")
                    };
                }

                yield return values;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try
            {
                _reader?.Dispose();
            }
            finally
            {
                try
                {
                    _writer.Dispose();
                }
                finally
                {
                    _stream.Dispose();
                }
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
