using System.Text;

namespace JET.Infrastructure;

/// <summary>
/// 正式輸出這次新寫進共用字串表的項目。編號接在範本原有的項目後面，從範本的項目數起算。
/// 項目寫進 Windows 暫存資料夾裡的暫存檔，不留在記憶體；收尾時照位元組接在範本的項目後面，
/// 所以暫存檔裡每一筆都已經是完整的 <c>&lt;si&gt;</c> 元素，見 <see cref="ExcelText.WriteSharedStringItem"/>。
/// 同樣的短文字只存一份：長度在 <see cref="DeduplicatedTextLength"/> 字以內的文字記住它的編號，
/// 記住的文字估計占用的記憶體到 <see cref="DeduplicationMemoryBudget"/> 為止，之後不再記新的文字，已經記住的照樣共用。
/// 較長或沒有記住的文字每格各存一份，內容一樣正確，只是檔案大一些。
/// 收集器由正式輸出的壓縮檔建立與釋放；匯出失敗或取消時暫存檔跟著關閉並刪除。
/// </summary>
internal sealed class WorkbookSharedStrings : IDisposable
{
    /// <summary>記住編號的文字最多幾個字；以寫進檔案的字數計算。</summary>
    internal const int DeduplicatedTextLength = 256;

    /// <summary>記住的文字估計最多占用多少記憶體。</summary>
    internal const long DeduplicationMemoryBudget = 64L * 1024 * 1024;

    /// <summary>記住一筆文字時，除了文字本身以外，字典與字串物件另外占用的記憶體估計。</summary>
    private const int RememberedEntryOverhead = 64;

    private const int SpoolBufferSize = 1024 * 1024;

    private const int CopyBufferSize = 81_920;

    private readonly int _deduplicatedTextLength;
    private readonly long _deduplicationMemoryBudget;
    private readonly Dictionary<string, uint> _remembered = new(StringComparer.Ordinal);
    private long _rememberedBytes;
    private bool _rememberingStopped;
    private long _addedItemCount;
    private FileStream? _spool;
    private StreamWriter? _writer;
    private bool _disposed;

    internal WorkbookSharedStrings(uint templateItemCount)
        : this(templateItemCount, DeduplicatedTextLength, DeduplicationMemoryBudget)
    {
    }

    internal WorkbookSharedStrings(uint templateItemCount, int deduplicatedTextLength, long deduplicationMemoryBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(deduplicatedTextLength);
        ArgumentOutOfRangeException.ThrowIfNegative(deduplicationMemoryBudget);
        TemplateItemCount = templateItemCount;
        _deduplicatedTextLength = deduplicatedTextLength;
        _deduplicationMemoryBudget = deduplicationMemoryBudget;
    }

    /// <summary>範本原有的項目數，也就是這次新寫的第一個項目的編號。</summary>
    internal uint TemplateItemCount { get; }

    /// <summary>範本原有的項目加上這次新寫的項目，就是共用字串表的 uniqueCount。</summary>
    internal long ItemCount => TemplateItemCount + _addedItemCount;

    /// <summary>
    /// 回傳 <paramref name="encodedText"/> 在共用字串表的編號。文字要先經過 <see cref="ExcelText.Encode"/>；
    /// 已經記住的文字回傳同一個編號，其他的新寫一筆項目。
    /// </summary>
    internal uint Add(string encodedText)
    {
        ArgumentNullException.ThrowIfNull(encodedText);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var remembers = encodedText.Length <= _deduplicatedTextLength;
        if (remembers && _remembered.TryGetValue(encodedText, out var existing))
        {
            return existing;
        }

        var next = ItemCount;
        if (next >= uint.MaxValue)
        {
            throw new InvalidOperationException("共用字串表的項目超過能記錄的數量。");
        }

        var index = (uint)next;
        ExcelText.WriteSharedStringItem(Writer(), encodedText);
        _addedItemCount++;
        if (remembers && !_rememberingStopped)
        {
            Remember(encodedText, index);
        }

        return index;
    }

    /// <summary>把這次新寫的項目照位元組複製到 <paramref name="output"/>：UTF-8、不帶 BOM，每一筆都是完整的 si 元素。</summary>
    internal void CopyAddedItemsTo(Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_spool is null)
        {
            return;
        }

        _writer!.Flush();
        _spool.Position = 0;
        var buffer = new byte[CopyBufferSize];
        int read;
        while ((read = _spool.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
        }
    }

    /// <summary>關閉暫存檔，關閉時跟著刪除。這時內容已經寫完或用不到了，關閉失敗也不往外丟。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ExportResources.ReleaseAll(_writer, _spool);
    }

    /// <summary>記憶體上限放不下這一筆時，之後都不再記新的文字，以免每一筆都重新估計。</summary>
    private void Remember(string encodedText, uint index)
    {
        var bytes = 2L * encodedText.Length + RememberedEntryOverhead;
        if (_rememberedBytes + bytes > _deduplicationMemoryBudget)
        {
            _rememberingStopped = true;
            return;
        }

        _remembered.Add(encodedText, index);
        _rememberedBytes += bytes;
    }

    /// <summary>第一次新寫項目時才建立暫存檔；沒有新文字的活頁簿不用暫存檔。</summary>
    private StreamWriter Writer()
    {
        if (_writer is not null)
        {
            return _writer;
        }

        var spool = ExportSpoolFile.CreateStream(".jet-shared-strings-", SpoolBufferSize);
        try
        {
            _writer = new StreamWriter(
                spool,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: CopyBufferSize,
                leaveOpen: true);
        }
        catch
        {
            spool.Dispose();
            throw;
        }

        _spool = spool;
        return _writer;
    }
}
