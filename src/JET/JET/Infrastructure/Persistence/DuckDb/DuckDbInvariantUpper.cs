using System.Data.Common;
using DuckDB.NET.Data;

namespace JET.Infrastructure;

/// <summary>
/// 讓 DuckDB 的 <c>upper</c> 等於 .NET <see cref="string.ToUpperInvariant()"/>，和 SQLite 連線的覆寫相同。
/// 2026-10-19 逐一比對全部 Unicode 字元，DuckDB 內建 <c>upper</c> 只有 ß 與 ı 兩個字元和 .NET 不同；
/// 文字條件、關鍵字與人員比對都是兩側轉大寫再比較，條件值由 .NET 轉換，欄位必須用同一個定義。
/// 覆寫的函式屬於資料庫實體：同一實體的其他連線都看得到，所有連線關閉後實體重建就失效，
/// 所以每次開啟連線都先用 ß 檢查，還是內建版本才註冊。
/// </summary>
internal static class DuckDbInvariantUpper
{
    private const string Probe = "ß";
    private static readonly Lock RegistrationLock = new();

    internal static void Ensure(DbConnection connection)
    {
        if (connection is not DuckDBConnection duck)
        {
            return;
        }

        // 同一實體可能同時開啟多條連線；檢查與註冊放在同一把鎖，避免重複寫入目錄。
        lock (RegistrationLock)
        {
            using var check = duck.CreateCommand();
            check.CommandText = $"SELECT upper('{Probe}');";
            if (string.Equals(check.ExecuteScalar() as string, Probe, StringComparison.Ordinal))
            {
                return;
            }

            duck.RegisterScalarFunction<string, string?>("upper", static value => value?.ToUpperInvariant());
        }
    }
}
