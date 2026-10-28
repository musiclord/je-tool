namespace JET.AuditCore;

/// <summary>
/// 文字欄「是空白」「不是空白」的唯一 SQL 寫法。空白指 NULL 或去掉頭尾空白後是空字串；
/// 去掉的字元集合由 <see cref="ISqlDialect.Trim"/> 決定，和 .NET 相同，三種資料庫的答案一致。
/// 兩個片段都加外層括號，可以直接接在 AND 或 OR 之後。
/// </summary>
internal static class SqlBlankText
{
    internal static string IsBlank(ISqlDialect dialect, string expression)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        return $"({expression} IS NULL OR {dialect.Trim(expression)} = '')";
    }

    internal static string IsNotBlank(ISqlDialect dialect, string expression)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        return $"({expression} IS NOT NULL AND {dialect.Trim(expression)} <> '')";
    }
}
