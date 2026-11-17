using System.Globalization;
using JET.Domain;

namespace JET.Infrastructure;

internal sealed partial class LegacyReportWriter
{
    /// <summary>
    /// 一個查詢讀完的明細 entry_id，先收在記憶體裡，再依序一批一批交給讀原始欄位、寫工作表的程式。
    /// 第一次取批時才執行查詢；查詢讀完、連線關閉後才讀原始欄位，不會同時開著兩條查詢。
    /// 每筆只存一個 entry_id，一百萬筆約 8 MB。批次的游標是下一筆在清單裡的位置，只能依序往前讀。
    /// </summary>
    private sealed class BufferedEntryIds(Func<CancellationToken, IAsyncEnumerable<long>> readAll)
    {
        private List<long>? _entryIds;
        private int _nextIndex;

        internal async Task<PageResult<long>> ReadPageAsync(PageRequest page, CancellationToken cancellationToken)
        {
            if (_entryIds is null)
            {
                var entryIds = new List<long>();
                await foreach (var entryId in readAll(cancellationToken)
                                   .WithCancellation(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    entryIds.Add(entryId);
                }

                _entryIds = entryIds;
            }

            var expectedIndex = page.Cursor is null
                ? 0
                : int.Parse(page.Cursor, NumberStyles.None, CultureInfo.InvariantCulture);
            if (expectedIndex != _nextIndex)
            {
                throw new InvalidOperationException("Buffered detail entry ids are read in order only.");
            }

            var take = Math.Min(page.ClampedPageSize, _entryIds.Count - _nextIndex);
            var rows = _entryIds.GetRange(_nextIndex, take);
            _nextIndex += take;
            var nextCursor = _nextIndex < _entryIds.Count
                ? _nextIndex.ToString(CultureInfo.InvariantCulture)
                : null;
            return new PageResult<long>(rows, nextCursor);
        }
    }
}
