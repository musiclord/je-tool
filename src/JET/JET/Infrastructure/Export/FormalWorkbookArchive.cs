using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;

namespace JET.Infrastructure;

/// <summary>
/// 正式輸出檔的壓縮檔。工作表內容寫出時直接壓縮進輸出檔，資料列寫出後就不留在記憶體，
/// 明細有幾百萬列也一樣；樣式表、workbook、摘要頁這些小的修改，則留在暫存的小活頁簿裡用 OpenXML SDK 改，
/// 寫完後再把小活頁簿其餘的項目逐一複製進輸出檔。
/// OpenXML SDK 開啟可寫的活頁簿時，會把每個改過的項目整份未壓縮地留在記憶體，等關閉時才壓縮寫出，
/// 單一項目超過 2 GB 還會失敗，所以大量資料不經過它。
/// 壓縮檔同時只能寫一個項目，每個項目只能寫一次，寫過的內容也不能讀回；工作表要一張寫完、關閉後才能開下一張。
/// 寫完時由 <see cref="Complete"/> 關閉壓縮檔；失敗或取消而沒有寫完時，由 <see cref="Dispose"/> 關閉。
/// </summary>
internal sealed class FormalWorkbookArchive : IDisposable
{
    private readonly ZipArchive _archive;
    private readonly HashSet<string> _writtenEntries = new(StringComparer.OrdinalIgnoreCase);
    private string? _openEntry;
    private Stream? _openContent;
    private bool _completed;
    private bool _disposed;

    internal FormalWorkbookArchive(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
    }

    /// <summary>讓 <paramref name="workbook"/> 裡的工作表寫出時，改寫進這個壓縮檔。</summary>
    internal void Attach(SpreadsheetDocument workbook)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        workbook.AddAnnotation(this);
    }

    /// <summary>
    /// 打開一張工作表的內容來寫，呼叫端寫完後要關閉回傳的串流。這張工作表屬於正式輸出時，內容直接壓縮進輸出檔；
    /// 不屬於正式輸出的活頁簿，照舊寫回工作表自己的 part。
    /// </summary>
    internal static Stream OpenWorksheetContent(WorksheetPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return part.OpenXmlPackage.Annotation<FormalWorkbookArchive>() is { } archive
            ? archive.OpenEntry(part)
            : part.GetStream(FileMode.Create, FileAccess.Write);
    }

    /// <summary>
    /// 把小活頁簿裡沒有直接寫進輸出檔的項目逐一複製過去，再寫完壓縮檔。小活頁簿要先關閉，內容才是最新的。
    /// 直接寫進輸出檔的工作表，在小活頁簿裡只剩範本原本的內容或空的項目，不複製。
    /// </summary>
    internal void Complete(Stream workbook, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        EnsureWritable();
        if (_openEntry is not null)
        {
            throw new InvalidOperationException($"工作表「{_openEntry}」還沒寫完，正式輸出檔不能收尾。");
        }

        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        workbook.Position = 0;
        using (var source = new ZipArchive(workbook, ZipArchiveMode.Read, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_writtenEntries.Contains(entry.FullName))
                {
                    skipped.Add(entry.FullName);
                    continue;
                }

                // 範本裡的圖片本來就沒有壓縮，壓縮後大小等於原始大小；這些項目照原樣不壓縮。
                var level = entry.CompressedLength == entry.Length
                    ? CompressionLevel.NoCompression
                    : CompressionLevel.Optimal;
                var target = _archive.CreateEntry(entry.FullName, level);
                target.LastWriteTime = entry.LastWriteTime;
                using var input = entry.Open();
                using var output = target.Open();
                input.CopyTo(output);
            }
        }

        // 小活頁簿裡找不到的工作表，表示寫出後又被移除，或是項目名稱對不上；照樣收尾會留下沒有人引用的工作表。
        var orphaned = _writtenEntries.Where(name => !skipped.Contains(name)).Order(StringComparer.Ordinal).ToArray();
        if (orphaned.Length > 0)
        {
            throw new InvalidOperationException(
                $"正式輸出檔裡的工作表在活頁簿裡找不到：{string.Join(", ", orphaned)}。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _archive.Dispose();
        _completed = true;
    }

    private Stream OpenEntry(WorksheetPart part)
    {
        EnsureWritable();
        var name = part.Uri.OriginalString.TrimStart('/');
        if (_openEntry is not null)
        {
            throw new InvalidOperationException(
                $"正式輸出檔同時只能寫一張工作表：「{_openEntry}」還沒寫完，就要開始寫「{name}」。");
        }

        if (!_writtenEntries.Add(name))
        {
            throw new InvalidOperationException($"工作表「{name}」已經寫進正式輸出檔，不能再寫一次。");
        }

        var entry = _archive.CreateEntry(name, CompressionLevel.Optimal);
        var content = new EntryContentStream(
            entry.Open(),
            () =>
            {
                _openEntry = null;
                _openContent = null;
            });
        _openEntry = name;
        _openContent = content;
        return content;
    }

    /// <summary>
    /// 放掉還開著的工作表項目並關閉壓縮檔，不丟出例外。已經寫完的壓縮檔在 <see cref="Complete"/> 就關閉了，這裡不再做事。
    /// 沒有寫完就關閉時，壓縮檔仍會把目錄補寫到輸出；這份輸出少了活頁簿其餘的項目，呼叫端不會留用，寫不進去也不往外丟。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ExportResources.ReleaseAll(_openContent, _archive);
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("正式輸出檔已經寫完，不能再寫入。");
        }
    }

    /// <summary>寫進壓縮檔一個項目的串流；關閉時通知壓縮檔可以開始寫下一個項目。</summary>
    private sealed class EntryContentStream(Stream inner, Action closed) : Stream
    {
        private bool _disposed;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => !_disposed;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        public override void WriteByte(byte value) => inner.WriteByte(value);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                try
                {
                    inner.Dispose();
                }
                finally
                {
                    closed();
                }
            }

            base.Dispose(disposing);
        }
    }
}
