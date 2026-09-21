namespace JET.AuditCore;

/// <summary>
/// SQL 方言縫隙（guide §13）：只收錄引擎間「確實不同」的片段——
/// 週末判定（SQLite strftime ↔ SQL Server DATEPART）、不分大小寫包含
/// （instr ↔ CHARINDEX）、主單位整數商、參數命名。取模、ABS、EXISTS、ISO 日期字串比較
/// 皆共通，不進介面。新增 provider = 新增一個 ISqlDialect 實作
/// + 一組 *FilterRunRepository / *PrescreenRunRepository 的 SELECT 骨架。
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
    /// 非負整數 dividend 除以正整數 divisor 的向零整數商。DuckDB 的 <c>/</c> 會回浮點，
    /// 必須使用 <c>//</c>；SQLite／SQL Server 則使用整數 <c>/</c>。
    /// </summary>
    string IntegerQuotient(string dividendExpression, string divisorExpression);

    /// <summary>
    /// ISO 日期字串（yyyy-MM-dd，三個 provider 的日期欄都是這種字串）的「幾日」整數（1 到 31）。
    /// 用第 9 到 10 字元轉整數，不依賴各引擎的日期函式；空值由呼叫端另外判斷。
    /// </summary>
    string DayOfMonth(string dateExpr);

    /// <summary>ISO 日期所在月份的天數（含閏年）；不受案件期末日限制。</summary>
    string DaysInMonth(string dateExpr);

    /// <summary>
    /// INF v2 的 canonical PRF 排序鍵。實作必須精確渲染 AuditCore 的 signed-BIGINT-safe
    /// 整數語意，不得改用 provider hash／浮點函式。
    /// </summary>
    string InfSampleOrderingKey(string sourceRowNumberExpression, string seedExpression);

    /// <summary>取前 N 列子句(需查詢已有 ORDER BY)。SQLite: LIMIT @p;SQL Server: OFFSET 0 ROWS FETCH NEXT @p ROWS ONLY。</summary>
    string LimitClause(string parameterName);

    /// <summary>
    /// dev 唯讀檢視「列出使用者資料表名」查詢（第一欄為表名；排除引擎內部表）。
    /// 本地引擎（SQLite／DuckDB）為整檔一份、無參數；SQL Server 版帶 <c>@schema</c> 具名參數
    /// （schema-per-project，由呼叫端綁定），故其 dev 檢視自持 schema 限定查法、不走本成員。
    /// </summary>
    string ListTablesSql { get; }

    /// <summary>dev 唯讀檢視「引擎版本字串」查詢（回單一 scalar）。</summary>
    string EngineVersionSql { get; }
}
