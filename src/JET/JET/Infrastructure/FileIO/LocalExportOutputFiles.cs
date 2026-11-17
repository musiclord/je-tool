using JET.Domain;

namespace JET.Infrastructure;

/// <summary>在本機的案件資料夾建立匯出用的輸出檔，報告暫存檔與科目配對範本的工作檔共用。</summary>
public sealed class LocalExportOutputFiles : IExportOutputFiles
{
    private const int BufferSize = 128 * 1024;

    public ExportOutputStream CreateNew(string path, FileOptions options)
    {
        try
        {
            return new LocalExportOutputFile(new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                BufferSize,
                options));
        }
        catch (Exception exception) when (ExportFileWriteErrors.TryTranslate(exception) is { } writeFailure)
        {
            throw writeFailure;
        }
    }
}

/// <summary>
/// 包住已經開好的輸出檔串流，每一項操作遇到磁碟已滿或沒有寫入權限，就丟出案件資料夾的訊息。
/// </summary>
internal sealed class LocalExportOutputFile(Stream inner) : ExportOutputStream
{
    private bool _innerClosed;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set
        {
            try
            {
                inner.Position = value;
            }
            catch (Exception exception) when (ExportFileWriteErrors.TryTranslate(exception) is { } writeFailure)
            {
                throw writeFailure;
            }
        }
    }

    public override void Flush()
    {
        try
        {
            inner.Flush();
        }
        catch (Exception exception) when (ExportFileWriteErrors.TryTranslate(exception) is { } writeFailure)
        {
            throw writeFailure;
        }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (ExportFileWriteErrors.TryTranslate(exception) is { } writeFailure)
        {
            throw writeFailure;
        }
    }

    public override void FlushToDisk()
    {
        try
        {
            if (inner is FileStream file)
            {
                file.Flush(flushToDisk: true);
            }
            else
            {
                inner.Flush();
            }
        }
        catch (Exception exception) when (ExportFileWriteErrors.TryTranslate(exception) is { } writeFailure)
        {
            throw writeFailure;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            return inner.Read(buffer, offset, count);
        }
        catch (Exception exception) when (ExportFileWriteErrors.TryTranslate(exception) is { } writeFailure)
        {
            throw writeFailure;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        try
        {
            return inner.Seek(offset, origin);
        }
        catch (Exception exception) when (ExportFileWriteErrors.TryTranslate(exception) is { } writeFailure)
        {
            throw writeFailure;
        }
    }

    public override void SetLength(long value)
    {
        try
        {
            inner.SetLength(value);
        }
        catch (Exception exception) when (ExportFileWriteErrors.TryTranslate(exception) is { } writeFailure)
        {
            throw writeFailure;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        try
        {
            inner.Write(buffer, offset, count);
        }
        catch (Exception exception) when (ExportFileWriteErrors.TryTranslate(exception) is { } writeFailure)
        {
            throw writeFailure;
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        try
        {
            inner.Write(buffer);
        }
        catch (Exception exception) when (ExportFileWriteErrors.TryTranslate(exception) is { } writeFailure)
        {
            throw writeFailure;
        }
    }

    public override void WriteByte(byte value)
    {
        try
        {
            inner.WriteByte(value);
        }
        catch (Exception exception) when (ExportFileWriteErrors.TryTranslate(exception) is { } writeFailure)
        {
            throw writeFailure;
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (ExportFileWriteErrors.TryTranslate(exception) is { } writeFailure)
        {
            throw writeFailure;
        }
    }

    public override async ValueTask CloseAfterFailedWriteAsync()
    {
        if (!_innerClosed)
        {
            _innerClosed = true;
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            // 內層串流只關一次：關檔失敗時檔案已經關閉，再關一次只會再丟一次同樣的錯誤。
            if (disposing && !_innerClosed)
            {
                _innerClosed = true;
                inner.Dispose();
            }
        }
        catch (Exception exception) when (ExportFileWriteErrors.TryTranslate(exception) is { } writeFailure)
        {
            throw writeFailure;
        }
        finally
        {
            base.Dispose(disposing);
        }
    }
}
