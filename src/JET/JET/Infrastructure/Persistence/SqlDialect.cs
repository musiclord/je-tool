using JET.AuditCore;

namespace JET.Infrastructure;

/// <summary>
/// Infrastructure 的 SQL 方言合成介面。<see cref="ProviderName"/> 是診斷事件標籤而非 SQL 片段，
/// 因此不進 <see cref="ISqlDialect"/>；本介面讓本地 repository 仍從同一個 provider 物件取得兩種職責。
/// </summary>
public interface IProviderSqlDialect : ISqlDialect
{
    /// <summary>診斷日誌用的引擎標籤（sql.executed／tx.* 的 provider 欄位）。sqlite／duckdb／sqlServer。</summary>
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
        $"instr(UPPER(COALESCE({columnExpr}, '')), {parameterName}) > 0";

    public string IntegerQuotient(string dividendExpression, string divisorExpression) =>
        $"CAST(({dividendExpression}) / ({divisorExpression}) AS INTEGER)";

    public string InfSampleOrderingKey(string sourceRowNumberExpression, string seedExpression) =>
        InfSamplingPrf.SqlOrderingKey(this, sourceRowNumberExpression, seedExpression);

    public string LimitClause(string parameterName) => $"LIMIT {parameterName}";

    // dev 檢視（LocalDevDatabaseInspector 消費）：sqlite_master 列表 + sqlite_version()。
    public string ListTablesSql =>
        "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";

    public string EngineVersionSql => "SELECT sqlite_version();";
}

/// <summary>
/// DuckDB 方言（spec §3/§4，第二本地引擎）。與 SQLite 差異僅一項結構性（參數記號），由
/// <see cref="DuckDbCommandAdapter"/> 統一把 <c>@p{i}</c> 改寫為 <c>$p{i}</c>——方言照回 <c>@p{i}</c>、不分岔。
/// 週末判定用 DuckDB 正式形 <c>strftime(CAST(x AS DATE), '%w')</c>（<c>%w</c> 週日=0，與 .NET／SQLite 同碼；
/// 回傳文字，比對清單沿用 SQLite 的引號字面格式）；<c>instr</c>／<c>LIMIT</c> 探針已證與 SQLite 同形。
/// </summary>
public sealed class DuckDbDialect : IProviderSqlDialect
{
    public static readonly DuckDbDialect Instance = new();

    public string ProviderName => "duckdb";

    public string ParameterName(int index) => $"@p{index}";

    public string WeekendPredicate(string dateExpr, IReadOnlyCollection<int> nonWorkingDays)
    {
        if (nonWorkingDays.Count == 0)
        {
            return "0 = 1";
        }

        // strftime(CAST(x AS DATE), '%w') 回文字 '0'..'6'（週日=0，與 SQLite strftime('%w',…) 同碼、同文字比對）。
        // 清單字面格式與 SqliteDialect 一致（引號字串），確保跨本地引擎的述詞語意逐字對齊。
        var list = string.Join(",", nonWorkingDays.OrderBy(d => d).Select(d => $"'{d}'"));
        return $"strftime(CAST({dateExpr} AS DATE), '%w') IN ({list})";
    }

    public string ContainsIgnoreCase(string columnExpr, string parameterName) =>
        $"instr(UPPER(COALESCE({columnExpr}, '')), {parameterName}) > 0";

    public string IntegerQuotient(string dividendExpression, string divisorExpression) =>
        $"(({dividendExpression}) // ({divisorExpression}))";

    public string InfSampleOrderingKey(string sourceRowNumberExpression, string seedExpression) =>
        InfSamplingPrf.SqlOrderingKey(this, sourceRowNumberExpression, seedExpression);

    public string LimitClause(string parameterName) => $"LIMIT {parameterName}";

    // dev 檢視（LocalDevDatabaseInspector 消費）：information_schema 列表 + version()。
    // 序列（seq_*）不屬 tables，不列出（探針證實）。
    public string ListTablesSql =>
        "SELECT table_name FROM information_schema.tables WHERE table_schema = 'main' ORDER BY table_name;";

    public string EngineVersionSql => "SELECT version();";
}

/// <summary>
/// SQL Server 方言(guide §13)。日期以 ISO NVARCHAR 字串儲存,週末判定用
/// DATEFIRST/語言無關的式子:自固定錨點(1900-01-01,週一)起的天數模 7,
/// 週六=5、週日=6。不分大小寫包含用 CHARINDEX(參數值由呼叫端先轉大寫)。
/// </summary>
public sealed class SqlServerDialect : IProviderSqlDialect
{
    public static readonly SqlServerDialect Instance = new();

    // SqlServer* repository 家族自持 Provider 常數（"sqlServer"）不經此路徑；此值僅為介面完整性，與其一致。
    public string ProviderName => "sqlServer";

    public string ParameterName(int index) => $"@p{index}";

    public string WeekendPredicate(string dateExpr, IReadOnlyCollection<int> nonWorkingDays)
    {
        if (nonWorkingDays.Count == 0)
        {
            return "0 = 1";
        }

        // 錨點(1900-01-01,週一)起天數模 7:週一=0…週六=5、週日=6。
        // canonical(.NET DayOfWeek,週日=0…週六=6)→ 此編碼為 (d + 6) % 7。
        var list = string.Join(", ", nonWorkingDays.Select(d => (d + 6) % 7).OrderBy(m => m));
        return $"(DATEDIFF(day, '19000101', CONVERT(date, {dateExpr})) % 7) IN ({list})";
    }

    public string ContainsIgnoreCase(string columnExpr, string parameterName) =>
        $"CHARINDEX({parameterName}, UPPER(COALESCE({columnExpr}, N''))) > 0";

    public string IntegerQuotient(string dividendExpression, string divisorExpression) =>
        $"(({dividendExpression}) / ({divisorExpression}))";

    public string InfSampleOrderingKey(string sourceRowNumberExpression, string seedExpression) =>
        InfSamplingPrf.SqlOrderingKey(this, sourceRowNumberExpression, seedExpression);

    public string LimitClause(string parameterName) => $"OFFSET 0 ROWS FETCH NEXT {parameterName} ROWS ONLY";

    // 忠實搬自 SqlServerDevDatabaseInspector 既有查法（帶 @schema 具名參數，schema-per-project）。
    // 該檢視本身自持 schema 限定呼叫（綁定 @schema），不經此成員；此處為介面完整性與未來 SqlServer
    // 消費者提供正宗查法，不改動 SqlServer 家族既有行為。
    public string ListTablesSql =>
        "SELECT t.name FROM sys.tables t INNER JOIN sys.schemas s ON s.schema_id = t.schema_id "
        + "WHERE s.name = @schema ORDER BY t.name;";

    public string EngineVersionSql => "SELECT CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(128));";
}
