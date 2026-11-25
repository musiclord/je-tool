namespace JET.Infrastructure;

internal sealed partial class LegacyReportWriter
{
    /// <summary>
    /// 工作表的欄寬定義必須寫在 sheetData 前面，但欄寬要讀完全部資料才知道，而資料只從 repository 讀一次。
    /// 所以先把每一列要顯示的文字寫到關閉時自動刪除的暫存檔，一邊寫一邊記下各欄最寬的內容；
    /// 全部讀完後，再從暫存檔逐列讀回來寫進工作表。記憶體裡一次只放一列。
    /// </summary>
    private sealed class RawReportSpool : IDisposable
    {
        private readonly ExportSpoolFile _file;
        private readonly ExcelDisplayWidthTracker _widths;
        private BinaryReader? _reader;
        private bool _completed;
        private bool _readStarted;
        private bool _disposed;

        private RawReportSpool(
            ExportSpoolFile file,
            IReadOnlyList<string> headers)
        {
            _file = file;
            _widths = new ExcelDisplayWidthTracker(headers);
        }

        internal long RowCount { get; private set; }

        internal IReadOnlyList<double> Widths
        {
            get
            {
                ThrowIfDisposed();
                if (!_completed)
                {
                    throw new InvalidOperationException(
                        "Raw report spool must be completed before reading column widths.");
                }

                return _widths.Widths;
            }
        }

        internal static RawReportSpool Create(IReadOnlyList<string> headers)
        {
            var file = ExportSpoolFile.Create(".jet-legacy-report-raw-", bufferSize: 1024 * 1024);
            try
            {
                return new RawReportSpool(file, headers);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        internal void Append(IReadOnlyList<string?> values)
        {
            ThrowIfDisposed();
            if (_completed)
            {
                throw new InvalidOperationException(
                    "Raw report spool cannot append rows after completion.");
            }

            _widths.Observe(values);
            var writer = _file.Writer;
            foreach (var value in values)
            {
                writer.Write(value is not null);
                if (value is not null)
                {
                    writer.Write(value);
                }
            }
            RowCount = checked(RowCount + 1);
        }

        internal void Complete()
        {
            ThrowIfDisposed();
            if (_completed)
            {
                return;
            }

            _reader = _file.StartReading(throwOnInvalidBytes: true);
            _completed = true;
        }

        internal IEnumerable<IReadOnlyList<string?>> ReadRows(
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!_completed)
            {
                throw new InvalidOperationException(
                    "Raw report spool must be completed before reading rows.");
            }
            if (_readStarted)
            {
                throw new InvalidOperationException(
                    "Raw report spool rows can only be read once.");
            }

            _readStarted = true;
            var reader = _reader
                ?? throw new InvalidOperationException("Raw report spool reader is missing.");
            for (long row = 0; row < RowCount; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = new string?[_widths.ColumnCount];
                for (var column = 0; column < values.Length; column++)
                {
                    values[column] = reader.ReadBoolean() ? reader.ReadString() : null;
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
            _file.Dispose();
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
