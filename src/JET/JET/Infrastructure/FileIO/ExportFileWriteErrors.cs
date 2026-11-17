using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 匯出報告、底稿與工作檔時，檔案寫不進去的兩種情形：所在磁碟已滿，以及沒有寫入權限。
/// 這兩種情形重新下載 JET 也沒有用，所以轉成寫明是哪裡、原因與審計員接下來能做什麼的訊息，不說成範本無法讀取或填入。
/// 案件資料夾裡的輸出檔、案件位置裡的報告鎖檔，以及 Windows 暫存資料夾裡的暫存檔，各用各的句子，
/// 呼叫端依例外來自哪一種檔案選用。磁碟已滿依 Windows 的錯誤碼判斷；其他例外不轉。
/// </summary>
internal static class ExportFileWriteErrors
{
    /// <summary>Windows 的 ERROR_HANDLE_DISK_FULL。</summary>
    private const int ErrorHandleDiskFull = 39;

    /// <summary>Windows 的 ERROR_DISK_FULL。</summary>
    private const int ErrorDiskFull = 112;

    /// <summary>
    /// 例外來自案件資料夾裡的輸出檔時使用：從這個例外一路往 InnerException 找磁碟已滿或沒有寫入權限，
    /// 找到時回傳給審計員看的錯誤；找不到時回 null。
    /// </summary>
    internal static JetActionException? TryTranslate(Exception exception) =>
        Translate(exception, DiskFull, NoWriteAccess);

    /// <summary>
    /// 例外來自案件位置根目錄裡的報告鎖檔時使用。匯出、開啟案件時標記過期與刪除案件都會取這把鎖，
    /// 所以句子寫「系統沒有開始作業」，不寫「本次未產生」。
    /// </summary>
    internal static JetActionException? TryTranslateCaseLocation(Exception exception) =>
        Translate(exception, CaseLocationDiskFull, CaseLocationNoWriteAccess);

    /// <summary>
    /// 例外來自寫出程式放在 Windows 暫存資料夾裡的暫存檔時使用；判斷方式和 <see cref="TryTranslate"/> 相同，句子改寫暫存資料夾。
    /// </summary>
    internal static JetActionException? TryTranslateTemporaryFolder(Exception exception) =>
        Translate(exception, TemporaryFolderDiskFull, TemporaryFolderNoWriteAccess);

    /// <summary>這個例外是 Windows 回報磁碟已滿。</summary>
    internal static bool IsDiskFull(Exception exception) =>
        exception is IOException
        && (exception.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000)
        && (exception.HResult & 0xFFFF) is ErrorHandleDiskFull or ErrorDiskFull;

    internal static JetActionException DiskFull(Exception innerException) => new(
        JetErrorCodes.FileReadError,
        "案件所在磁碟的空間不足，檔案無法寫入，本次未產生。請釋放空間後再試一次。",
        innerException: innerException);

    private static JetActionException NoWriteAccess(Exception innerException) => new(
        JetErrorCodes.FileReadError,
        "無法寫入案件資料夾，本次未產生。請確認案件資料夾不是唯讀，而且目前的 Windows 帳號可以寫入，再試一次。",
        innerException: innerException);

    private static JetActionException CaseLocationDiskFull(Exception innerException) => new(
        JetErrorCodes.FileReadError,
        "案件所在磁碟的空間不足，系統沒有開始作業。請釋放空間後再試一次。",
        innerException: innerException);

    private static JetActionException CaseLocationNoWriteAccess(Exception innerException) => new(
        JetErrorCodes.FileReadError,
        "無法寫入 JET 的案件位置，系統沒有開始作業。請確認案件位置不是唯讀，而且目前的 Windows 帳號可以寫入，再試一次。",
        innerException: innerException);

    private static JetActionException TemporaryFolderDiskFull(Exception innerException) => new(
        JetErrorCodes.FileReadError,
        "Windows 暫存資料夾所在磁碟的空間不足，本次未產生。請釋放空間後再試一次。",
        innerException: innerException);

    private static JetActionException TemporaryFolderNoWriteAccess(Exception innerException) => new(
        JetErrorCodes.FileReadError,
        "無法寫入 Windows 暫存資料夾，本次未產生。請確認目前的 Windows 帳號可以寫入暫存資料夾，再試一次。",
        innerException: innerException);

    private static JetActionException? Translate(
        Exception exception,
        Func<Exception, JetActionException> diskFull,
        Func<Exception, JetActionException> noWriteAccess)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (IsDiskFull(current))
            {
                return diskFull(exception);
            }

            if (current is UnauthorizedAccessException)
            {
                return noWriteAccess(exception);
            }
        }

        return null;
    }
}
