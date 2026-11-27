using System.Text;

namespace JET.Infrastructure;

/// <summary>
/// 匯出時暫放內容的暫存檔：放在 Windows 暫存資料夾、只給這一次匯出用，關閉時自動刪除。
/// 寫出程式先把內容寫進來，寫完再從頭讀回。
/// </summary>
internal sealed class ExportSpoolFile : IDisposable
{
    private BinaryReader? _reader;
    private bool _disposed;

    private ExportSpoolFile(FileStream stream)
    {
        Stream = stream;
        Writer = new BinaryWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
    }

    internal FileStream Stream { get; }

    internal BinaryWriter Writer { get; }

    /// <summary>在 Windows 暫存資料夾建立新的暫存檔：不和其他程式共用，關閉時自動刪除。</summary>
    internal static FileStream CreateStream(string namePrefix, int bufferSize) => new(
        Path.Combine(Path.GetTempPath(), $"{namePrefix}{Guid.NewGuid():N}.tmp"),
        FileMode.CreateNew,
        FileAccess.ReadWrite,
        FileShare.None,
        bufferSize,
        FileOptions.SequentialScan | FileOptions.DeleteOnClose);

    internal static ExportSpoolFile Create(string namePrefix, int bufferSize)
    {
        var stream = CreateStream(namePrefix, bufferSize);
        try
        {
            return new ExportSpoolFile(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 寫完之後從頭讀回：先把緩衝裡的內容寫進檔案，再回到開頭。
    /// 這時寫不進去的錯誤照常丟出，由外層轉成給使用者看的訊息。
    /// </summary>
    internal BinaryReader StartReading(bool throwOnInvalidBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_reader is not null)
        {
            throw new InvalidOperationException("Export spool reading has already started.");
        }

        Writer.Flush();
        Stream.Position = 0;
        return _reader = new BinaryReader(
            Stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes),
            leaveOpen: true);
    }

    /// <summary>
    /// 放掉暫存檔。這時內容已經讀完，或匯出已經失敗、取消而用不到了，所以緩衝裡的內容寫不出去也不丟出新的錯誤，
    /// 以免蓋掉原本的原因；檔案一定關閉，關閉時跟著刪除。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ExportResources.ReleaseAll(_reader, Writer, Stream);
    }
}
