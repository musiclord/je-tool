namespace JET.AuditCore;

/// <summary>
/// 由資料庫引擎決定寫法的 SQL 片段：週末判定、不分大小寫包含、轉大寫、主單位整數商、參數命名等。
/// 目前只有 SQLite 的實作。
/// </summary>
public interface ISqlDialect
{
    /// <summary>第 index 個參數的佔位名（如 @p0）。</summary>
    string ParameterName(int index);

    /// <summary>dateExpr 落在設定之非工作日(週幾,.NET DayOfWeek 編碼集合:週日=0…週六=6)的述詞片段；
    /// 空集合 → 恆偽 "0 = 1"。</summary>
    string WeekendPredicate(string dateExpr, IReadOnlyCollection<int> nonWorkingDays);

    /// <summary>columnExpr 不分大小寫包含 parameterName 參數值（NULL 以空字串參與）。</summary>
    string ContainsIgnoreCase(string columnExpr, string parameterName);

    /// <summary>
    /// 把 expr 轉成大寫，作為不分大小寫比較與分組的鍵；條件值在 .NET 端用 <c>ToUpperInvariant()</c> 轉換。
    /// SQLite 的連線把 <c>upper</c> 換成同一個 .NET 轉換。
    /// </summary>
    string Upper(string expr);

    /// <summary>
    /// 去掉 expr 頭尾的空白字元，字元集合是 .NET <c>string.Trim()</c> 的集合（<see cref="JET.Domain.TextWhitespace"/>）：
    /// 半形空白、Tab、換行、全形空白、不換行空格等。SQLite 原生 TRIM 只去半形空白，
    /// 所以判斷空白、比較文字與分組都必須經過這裡。
    /// </summary>
    string Trim(string expr);

    /// <summary>
    /// 非負整數 dividend 除以正整數 divisor 的向零整數商。
    /// </summary>
    string IntegerQuotient(string dividendExpression, string divisorExpression);

    /// <summary>
    /// ISO 日期字串（yyyy-MM-dd，日期欄都是這種字串）的「幾日」整數（1 到 31）。
    /// 用第 9 到 10 字元轉整數；空值由呼叫端另外判斷。
    /// </summary>
    string DayOfMonth(string dateExpr);

    /// <summary>ISO 日期所在月份的天數（含閏年）；不受案件期末日限制。</summary>
    string DaysInMonth(string dateExpr);

    /// <summary>
    /// INF v2 的 canonical PRF 排序鍵。實作必須精確渲染 AuditCore 的 signed-BIGINT-safe
    /// 整數語意，不得改用 provider hash／浮點函式。
    /// </summary>
    string InfSampleOrderingKey(string sourceRowNumberExpression, string seedExpression);

    /// <summary>取前 N 列子句(需查詢已有 ORDER BY)。</summary>
    string LimitClause(string parameterName);

    /// <summary>
    /// dev 唯讀檢視「列出使用者資料表名」查詢（第一欄為表名；排除引擎內部表）。
    /// </summary>
    string ListTablesSql { get; }

    /// <summary>dev 唯讀檢視「引擎版本字串」查詢（回單一 scalar）。</summary>
    string EngineVersionSql { get; }
}
