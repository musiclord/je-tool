namespace JET.Infrastructure;

/// <summary>
/// infSamplePage 的 SQL 片段(參數化)。
/// </summary>
internal static class InfSamplePageSql
{
    /// <summary>
    /// 只讀指定那一次驗證結果（result_id）抽出的樣本，樣本才對得回是哪一次驗證抽的。
    /// resultId 的值用參數綁定，不串進 SQL 字串。
    /// </summary>
    public const string ResultFilter = "s.result_id = @resultId";
}
