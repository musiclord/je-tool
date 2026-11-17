using System.Globalization;
using JET.Domain;

namespace JET.Infrastructure;

internal sealed partial class LegacyReportWriter
{
    /// <summary>
    /// 條件篩選報告一個情境的整張傳票分錄。第一次取批時用一個查詢依序讀完，每筆的 entry_id 與是否符合條件
    /// 寫到關閉時自動刪除的暫存檔，之後每批從暫存檔接著讀。查詢讀完、連線關閉後才讀原始欄位與攸關資料元素，
    /// 不會同時開著兩條查詢；每筆只存 9 個位元組，大案件也不必把整個情境的分錄放進記憶體。
    /// 批次的游標是下一筆在暫存檔裡的序號，只能依序往前讀。
    /// </summary>
    private sealed class CriteriaVoucherRowSpool : IDisposable
    {
        private FileStream? _stream;
        private BinaryReader? _reader;
        private long _count;
        private long _nextIndex;
        private bool _disposed;

        internal bool IsFilled => _stream is not null;

        internal async Task FillAsync(
            IAsyncEnumerable<(long EntryId, bool IsHit)> rows,
            bool wholeVoucher,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stream is not null)
            {
                throw new InvalidOperationException("Criteria voucher row spool is already filled.");
            }

            var path = Path.Combine(
                Path.GetTempPath(),
                $".jet-legacy-report-criteria-{Guid.NewGuid():N}.tmp");
            var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            try
            {
                long count = 0;
                await using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
                    {
                        writer.Write(row.EntryId);
                        // 整張傳票計數的情境，命中傳票的每一筆分錄都算符合條件，和摘要的分錄筆數一致。
                        writer.Write(wholeVoucher || row.IsHit);
                        count++;
                    }
                }

                stream.Position = 0;
                _stream = stream;
                _reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
                _count = count;
                _nextIndex = 0;
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        internal PageResult<CriteriaVoucherRow> ReadPage(PageRequest page)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_reader is null)
            {
                throw new InvalidOperationException("Criteria voucher row spool must be filled before reading.");
            }

            var expectedIndex = page.Cursor is null
                ? 0
                : long.Parse(page.Cursor, NumberStyles.None, CultureInfo.InvariantCulture);
            if (expectedIndex != _nextIndex)
            {
                throw new InvalidOperationException("Criteria voucher rows are read in order only.");
            }

            var take = (int)Math.Min(page.ClampedPageSize, _count - _nextIndex);
            var rows = new CriteriaVoucherRow[take];
            for (var index = 0; index < take; index++)
            {
                rows[index] = new CriteriaVoucherRow(_reader.ReadInt64(), _reader.ReadBoolean());
            }

            _nextIndex += take;
            var nextCursor = _nextIndex < _count
                ? _nextIndex.ToString(CultureInfo.InvariantCulture)
                : null;
            return new PageResult<CriteriaVoucherRow>(rows, nextCursor);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _reader?.Dispose();
            _stream?.Dispose();
        }
    }
}
