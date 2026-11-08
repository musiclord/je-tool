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
/// Infrastructure 的 SQL 方言合成介面。<see cref="ProviderName"/> 是診斷事件標籤而非 SQL 片段，
/// 因此不進 <see cref="ISqlDialect"/>；本介面讓本地 repository 仍從同一個 provider 物件取得兩種職責。
/// </summary>
public interface IProviderSqlDialect : ISqlDialect
{
    /// <summary>診斷日誌用的引擎標籤（sql.executed／tx.* 的 provider 欄位）。目前只有 sqlite。</summary>
    string ProviderName { get; }
}

/// <summary>SQLite 方言。目前只支援 SQLite 案件（裁定 2026-11-06-08）。</summary>
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
        // December is always 31 days; avoid stepping beyond SQLite's year 9999 limit.
        $"(CASE WHEN substr(({dateExpr}), 6, 2) = '12' THEN 31 "
        + $"ELSE CAST(strftime('%d', date({dateExpr}, 'start of month', '+1 month', '-1 day')) AS INTEGER) END)";

    public string InfSampleOrderingKey(string sourceRowNumberExpression, string seedExpression) =>
        InfSamplingPrf.SqlOrderingKey(this, sourceRowNumberExpression, seedExpression);

    public string LimitClause(string parameterName) => $"LIMIT {parameterName}";

    // dev 檢視（LocalDevDatabaseInspector 消費）：sqlite_master 列表 + sqlite_version()。
    public string ListTablesSql =>
        "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";

    public string EngineVersionSql => "SELECT sqlite_version();";
}
