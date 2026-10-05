using System.Text;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 來源檔讀不到時給審計員看的訊息。只寫檔名，不寫完整路徑，也不把 .NET 的英文例外文字放進訊息；
/// 每一句都寫出接下來能做什麼。原始例外保留在 InnerException，支援日誌只取具名的安全欄位。
/// </summary>
internal static class SourceFileErrors
{
    /// <summary>Windows 的共用違規：ERROR_SHARING_VIOLATION（32）與 ERROR_LOCK_VIOLATION（33）。</summary>
    public static bool IsInUse(Exception error) =>
        error is IOException io && (io.HResult & 0xFFFF) is 32 or 33;

    public static JetActionException CannotOpen(string filePath, Exception error)
    {
        var name = Path.GetFileName(filePath);
        if (IsInUse(error))
        {
            return new JetActionException(
                JetErrorCodes.FileReadError,
                $"檔案 '{name}' 正被其他程式開啟（例如 Excel）。請關閉該檔案後重試。",
                innerException: error);
        }

        if (error is UnauthorizedAccessException)
        {
            return new JetActionException(
                JetErrorCodes.FileReadError,
                $"目前的帳號沒有權限讀取檔案 '{name}'。請確認檔案所在位置的存取權限，或把檔案複製到自己的資料夾後重試。",
                innerException: error);
        }

        return new JetActionException(
            JetErrorCodes.FileReadError,
            $"無法讀取檔案 '{name}'。請確認檔案可以正常開啟且沒有損壞，必要時另存一份後重試；仍失敗時可匯出支援日誌。",
            innerException: error);
    }

    public static JetActionException CannotDecode(string filePath, Encoding encoding, DecoderFallbackException error) =>
        CannotDecode(filePath, EncodingDetector.WireNameOf(encoding), error);

    public static JetActionException CannotDecode(string filePath, string encodingName, DecoderFallbackException error)
    {
        return new JetActionException(
            JetErrorCodes.FileReadError,
            $"檔案 '{Path.GetFileName(filePath)}' 有無法以 {encodingName} 解讀的內容。" +
            "請在匯入清單改選編碼（utf-8、big5 或 utf-16）後重試，或把檔案另存為 UTF-8。",
            innerException: error);
    }

    public static JetActionException UnbalancedQuote(string filePath, CsvStructureException error)
    {
        return new JetActionException(
            JetErrorCodes.FileReadError,
            $"檔案 '{Path.GetFileName(filePath)}' 第 {error.RecordNumber} 列開始的引號沒有成對，讀不出後面的欄位結構。" +
            "請用 Excel 開啟後另存成 CSV，或修正該列的引號後重試。",
            innerException: error);
    }
}
