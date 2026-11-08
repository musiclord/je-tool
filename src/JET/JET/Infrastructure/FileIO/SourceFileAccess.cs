namespace JET.Infrastructure;

/// <summary>
/// 來源檔的開檔方式。審計員常在 Excel 開著來源檔時匯入，所以共用讀寫開檔：Excel 開著也讀得到，
/// 讀到的是最後一次存檔的內容（裁定 2026-11-06-12）。FileShare.Delete 讓 Excel 存檔換檔時不受這個 handle 阻擋，
/// 已開著的 handle 繼續讀換檔前的內容。
/// </summary>
internal static class SourceFileAccess
{
    internal const FileShare ReadShare = FileShare.ReadWrite | FileShare.Delete;

    internal static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, ReadShare);

    /// <summary>
    /// 其他程式（例如 Excel）是否正以可寫入的方式開著這個檔案：試開一個不准別人寫入的唯讀 handle，
    /// 共用違規就是有人開著；不讀內容。檔案不在或判斷不了時回 false，讀檔時照原本的方式報錯。
    /// </summary>
    internal static bool IsOpenElsewhere(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (IOException error) when (SourceFileErrors.IsInUse(error))
        {
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
