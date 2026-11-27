using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>TRIM 字元清單寫法，碼位來自 <see cref="TextWhitespace.CodePoints"/>。</summary>
internal static class SqlWhitespaceList
{
    /// <summary>SQLite：<c>char(9,10,...)</c> 一次接受多個碼位。</summary>
    public static readonly string Sqlite =
        "char(" + string.Join(",", TextWhitespace.CodePoints) + ")";
}

/// <summary>
/// Infrastructure 用的 SQL 方言介面：<see cref="ISqlDialect"/> 加上 <see cref="ProviderName"/>。
/// <see cref="ProviderName"/> 是寫進診斷日誌的資料庫名稱，不是 SQL 片段，所以不放進 <see cref="ISqlDialect"/>；
/// 有了這個介面，repository 可以從同一個物件拿到 SQL 寫法和這個名稱。
/// </summary>
public interface IProviderSqlDialect : ISqlDialect
{
    /// <summary>診斷日誌裡的資料庫名稱，寫在 sql.executed 與 tx.* 事件的 provider 欄位；SQLite 方言回 sqlite。</summary>
    string ProviderName { get; }
}

/// <summary>SQLite 方言。</summary>
public sealed class SqliteDialect : IProviderSqlDialect
{
    public static readonly SqliteDialect Instance = new();

    public string ProviderName => "sqlite";

    public string ParameterName(int index) => $"@p{index}";

    public string WeekendPredicate(string dateExpr, IReadOnlyCollection<int> nonWorkingDays)
    {
        if (nonWorkingDays.Count == 0)
        {
            return "0 = 1";
        }

        // strftime('%w') 與 .NET DayOfWeek 同編碼(週日=0…週六=6),直接列出。
        var list = string.Join(",", nonWorkingDays.OrderBy(d => d).Select(d => $"'{d}'"));
        return $"strftime('%w', {dateExpr}) IN ({list})";
    }

    public string ContainsIgnoreCase(string columnExpr, string parameterName) =>
        $"instr({Upper($"COALESCE({columnExpr}, '')")}, {parameterName}) > 0";

    // SqliteProjectDatabase 開啟連線時把 upper 換成 .NET ToUpperInvariant()。
    public string Upper(string expr) => $"UPPER({expr})";

    public string Trim(string expr) => $"TRIM({expr}, {SqlWhitespaceList.Sqlite})";

    public string IntegerQuotient(string dividendExpression, string divisorExpression) =>
        $"CAST(({dividendExpression}) / ({divisorExpression}) AS INTEGER)";

    public string DayOfMonth(string dateExpr) => $"CAST(substr(({dateExpr}), 9, 2) AS INTEGER)";

    public string DaysInMonth(string dateExpr) =>
        // 12 月固定 31 天，直接回 31：用「下個月第一天減一天」來算的話，9999 年 12 月會超出 SQLite 的日期上限。
        $"(CASE WHEN substr(({dateExpr}), 6, 2) = '12' THEN 31 "
        + $"ELSE CAST(strftime('%d', date({dateExpr}, 'start of month', '+1 month', '-1 day')) AS INTEGER) END)";

    public string InfSampleOrderingKey(string sourceRowNumberExpression, string seedExpression) =>
        InfSamplingPrf.SqlOrderingKey(this, sourceRowNumberExpression, seedExpression);

    public string LimitClause(string parameterName) => $"LIMIT {parameterName}";

    // 以下兩句給開發用的唯讀檢視工具 LocalDevDatabaseInspector 用：從 sqlite_master 列出資料表，以及用 sqlite_version() 查版本。
    public string ListTablesSql =>
        "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";

    public string EngineVersionSql => "SELECT sqlite_version();";
}
