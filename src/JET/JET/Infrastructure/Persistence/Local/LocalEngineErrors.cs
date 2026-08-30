using DuckDB.NET.Data;
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
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteCorrupt = 11;
    private const int SqliteFull = 13;
    private const int SqliteNotADatabase = 26;

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
        return (codeWithExtension & PrimaryResultCodeMask) switch
        {
            SqliteBusy => DatabaseBusy(),
            SqliteLocked => DatabaseLocked(),
            SqliteFull => DatabaseStorageFull(),
            SqliteCorrupt or SqliteNotADatabase => DatabaseCorrupt(),
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

        if (Contains(message, "No space left on device")
            || Contains(message, "There is not enough space on the disk")
            || Contains(message, "The disk is full"))
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

        return null;
    }

    private static bool Contains(string message, string pattern) =>
        message.Contains(pattern, StringComparison.OrdinalIgnoreCase);

    private static JetActionException DatabaseBusy() => new(
        JetErrorCodes.DatabaseBusy,
        "案件資料庫目前忙碌，另一項寫入尚未完成。請稍候後重試。");

    private static JetActionException DatabaseLocked() => new(
        JetErrorCodes.DatabaseLocked,
        "案件資料庫正由另一個程序鎖定。請先關閉使用同一案件的其他 JET 執行個體，再重試。");

    private static JetActionException DatabaseStorageFull() => new(
        JetErrorCodes.DatabaseStorageFull,
        "專案所在磁碟的儲存空間不足，資料庫無法繼續寫入。請釋放空間後重試。");

    private static JetActionException DatabaseCorrupt() => new(
        JetErrorCodes.DatabaseCorrupt,
        "案件資料庫已損壞或不是有效的資料庫檔案。請停止寫入，並從已知良好的備份復原或交由維護人員處理。");
}
