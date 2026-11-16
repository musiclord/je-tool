using System.Text;

namespace JET.Infrastructure;

public sealed partial class LegacyReportWriter
{
    /// <summary>
    /// 工作表的欄寬定義必須寫在 sheetData 前面，但欄寬要讀完全部資料才知道，而資料只從 repository 讀一次。
    /// 所以先把每一列要顯示的文字寫到關閉時自動刪除的暫存檔，一邊寫一邊記下各欄最寬的內容；
    /// 全部讀完後，再從暫存檔逐列讀回來寫進工作表。記憶體裡一次只放一列。
    /// </summary>
    private sealed class RawReportSpool : IDisposable
    {
        private readonly FileStream _stream;
        private readonly BinaryWriter _writer;
        private readonly ExcelDisplayWidthTracker _widths;
        private BinaryReader? _reader;
        private bool _completed;
        private bool _readStarted;
        private bool _disposed;

        private RawReportSpool(
            FileStream stream,
            IReadOnlyList<string> headers)
        {
            _stream = stream;
            _writer = new BinaryWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                leaveOpen: true);
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
            var path = Path.Combine(
                Path.GetTempPath(),
                $".jet-legacy-report-raw-{Guid.NewGuid():N}.tmp");
            var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            try
            {
                return new RawReportSpool(stream, headers);
            }
            catch
            {
                stream.Dispose();
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
            foreach (var value in values)
            {
                _writer.Write(value is not null);
                if (value is not null)
                {
                    _writer.Write(value);
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

            _writer.Flush();
            _stream.Position = 0;
            _reader = new BinaryReader(
                _stream,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true),
                leaveOpen: true);
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

            _reader?.Dispose();
            _writer.Dispose();
            _stream.Dispose();
            _disposed = true;
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
