namespace JET.AuditCore;

/// <summary>
/// 人員的識別值，只有這一處定義：去掉頭尾空白後不分大小寫。編製者彙總的分組、授權編製人員名單的比對、
/// 非授權編製人員與編製、核准同一人的判斷都用它，同一個人不會因為空白或大小寫被當成兩個人。
/// </summary>
internal static class PersonIdentity
{
    /// <summary>拿來比對與分組的識別值。</summary>
    internal static string Key(ISqlDialect dialect, string expression) =>
        dialect.Upper(dialect.Trim(expression));

    /// <summary>
    /// 編製者彙總顯示的寫法：GL 的建立人員去掉頭尾空白，空值視為空字串。
    /// 分組時再對它取 <see cref="GroupKey"/>，顯示值取同一組裡碼位最小的寫法。
    /// </summary>
    internal static string PreparerDisplay(ISqlDialect dialect) =>
        dialect.Trim("COALESCE(created_by, '')");

    /// <summary>已經去掉頭尾空白的寫法，轉成不分大小寫的分組鍵。</summary>
    internal static string GroupKey(ISqlDialect dialect, string trimmedExpression) =>
        dialect.Upper(trimmedExpression);
}
