using System.ComponentModel;
using DuckDB.NET.Data;
using DuckDB.NET.Native;
using JET.Domain;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure;

/// <summary>
/// SQLite 與 DuckDB 引擎錯誤的單一映射點。只接受明確的 SQLite result code 或
/// DuckDB 強訊息型樣；未知例外維持 dispatcher 的 bridge_error fallback。
/// </summary>
public static class LocalEngineErrors
{
    private const int PrimaryResultCodeMask = 0xFF;
    private const int SqlitePermission = 3;
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteNoMemory = 7;
    private const int SqliteReadOnly = 8;
    private const int SqliteIoError = 10;
    private const int SqliteCorrupt = 11;
    private const int SqliteFull = 13;
    private const int SqliteCannotOpen = 14;
    private const int SqliteNotADatabase = 26;

    /// <summary>SQLITE_IOERR_NOMEM：讀寫時配置不到記憶體，算記憶體不足，不算讀寫錯誤。</summary>
    private const int SqliteIoErrorNoMemory = SqliteIoError | (12 << 8);

    /// <summary>沿 InnerException 鏈尋找本地引擎例外；不可辨識時回 null。</summary>
    public static JetActionException? TryTranslate(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var translated = current switch
            {
                SqliteException sqlite => TranslateSqliteException(sqlite),
                DuckDBException duckDb => TranslateDuckDbException(duckDb),
                _ => null
            };

            if (translated is not null)
            {
                return translated;
            }
        }

        return null;
    }

    private static JetActionException? TranslateSqliteException(SqliteException exception)
    {
        var codeWithExtension = exception.SqliteExtendedErrorCode != 0
            ? exception.SqliteExtendedErrorCode
            : exception.SqliteErrorCode;
        if (codeWithExtension == SqliteIoErrorNoMemory)
        {
            return DatabaseOutOfMemory();
        }

        return (codeWithExtension & PrimaryResultCodeMask) switch
        {
            SqliteBusy => DatabaseBusy(),
            SqliteLocked => DatabaseLocked(),
            SqliteFull => DatabaseStorageFull(),
            SqliteCorrupt or SqliteNotADatabase => DatabaseCorrupt(),
            // 唯讀包括資料庫檔設成唯讀、放在唯讀的位置，以及共用記憶體檔無法寫入（各種 SQLITE_READONLY 延伸碼）。
            SqliteReadOnly => DatabaseReadOnly(),
            // 沒有權限、開不了檔或讀寫失敗：常見原因是權限、拔掉的磁碟或網路位置，以及防毒軟體暫時占用檔案。
            SqlitePermission or SqliteCannotOpen or SqliteIoError => DatabaseUnavailable(),
            SqliteNoMemory => DatabaseOutOfMemory(),
            _ => null
        };
    }

    private static JetActionException? TranslateDuckDbException(DuckDBException exception)
    {
        var message = exception.Message;

        if (Contains(message, "Could not set lock on file")
            || Contains(message, "Conflicting lock is held"))
        {
            return DatabaseLocked();
        }

        if (Contains(message, "Conflict on update")
            || Contains(message, "Conflict on tuple deletion")
            || Contains(message, "write-write conflict"))
        {
            return DatabaseBusy();
        }

        if (exception.ErrorType == DuckDBErrorType.OutOfMemory || Contains(message, "Out of Memory Error"))
        {
            return DatabaseOutOfMemory();
        }

        if (Contains(message, "No space left on device")
            || Contains(message, "There is not enough space on the disk")
            || Contains(message, "The disk is full")
            || DiskFullSystemMessages.Any(text => Contains(message, text)))
        {
            return DatabaseStorageFull();
        }

        if (Contains(message, "Corrupt database file")
            || Contains(message, "Corrupt WAL file")
            || Contains(message, "Data corruption detected")
            || Contains(message, "checksum mismatch")
            || (Contains(message, "computed checksum") && Contains(message, "does not match stored checksum")))
        {
            return DatabaseCorrupt();
        }

        // 其餘開不了或讀寫不了資料庫檔的錯誤：沒有權限、檔案唯讀、磁碟不在，或防毒軟體這類程式獨占檔案。
        // DuckDB 這類訊息以英文的 IO Error 開頭，後面接作業系統依語言翻譯的原因，所以只看開頭與錯誤分類，不比對原因的文字。
        if (exception.ErrorType == DuckDBErrorType.Io || Contains(message, "IO Error:"))
        {
            return DatabaseUnavailable();
        }

        return null;
    }

    /// <summary>
    /// 磁碟已滿時作業系統的錯誤句子（ERROR_HANDLE_DISK_FULL、ERROR_DISK_FULL）。DuckDB 把作業系統依語言翻譯的句子
    /// 直接放進訊息，中文 Windows 上不會出現上面的英文句子，所以向這台電腦的 Windows 取得同一句來比對。
    /// </summary>
    private static readonly string[] DiskFullSystemMessages = SystemMessages(39, 112);

    private static string[] SystemMessages(params int[] win32Codes) =>
        win32Codes
            .Select(code => new Win32Exception(code).Message.Trim().TrimEnd('.', '。'))
            .Where(text => text.Length > 0)
            .ToArray();

    private static bool Contains(string message, string pattern) =>
        message.Contains(pattern, StringComparison.OrdinalIgnoreCase);

    private static JetActionException DatabaseBusy() => new(
        JetErrorCodes.DatabaseBusy,
        "案件資料庫目前忙碌，另一項寫入尚未完成。請稍候後重試。");

    private static JetActionException DatabaseLocked() => new(
        JetErrorCodes.DatabaseLocked,
        "案件資料庫正被其他程式使用。請先關閉開啟同一案件的其他 JET 視窗，再試一次。");

    private static JetActionException DatabaseStorageFull() => new(
        JetErrorCodes.DatabaseStorageFull,
        "案件所在磁碟的空間不足，資料庫無法繼續寫入。請釋放空間後再試一次。");

    private static JetActionException DatabaseCorrupt() => new(
        JetErrorCodes.DatabaseCorrupt,
        "案件資料庫已損壞，無法繼續使用。請從備份還原案件資料夾，或重新建立案件並匯入資料。");

    // 唯讀時已經開著的連線不會變成可寫，所以下一步是取消唯讀後重新開啟案件；離開案件時 JET 會關閉這些連線。
    private static JetActionException DatabaseReadOnly() => new(
        JetErrorCodes.DatabaseReadOnly,
        "案件資料庫是唯讀的，資料無法寫入。請確認案件資料夾和裡面的檔案不是唯讀，再重新開啟案件。");

    private static JetActionException DatabaseUnavailable() => new(
        JetErrorCodes.DatabaseUnavailable,
        "JET 無法讀寫案件資料庫。請確認案件資料夾所在的磁碟仍然連接，資料夾和裡面的檔案不是唯讀，而且目前的 Windows 帳號可以寫入。"
        + "防毒軟體或雲端同步正在處理這個資料夾時，請等處理完成後再試一次。");

    private static JetActionException DatabaseOutOfMemory() => new(
        JetErrorCodes.DatabaseOutOfMemory,
        "電腦的記憶體不足，此操作未完成。請關閉其他程式後再試一次。");
}
