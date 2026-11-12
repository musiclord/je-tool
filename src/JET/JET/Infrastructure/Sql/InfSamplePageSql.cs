namespace JET.Infrastructure;

/// <summary>
/// infSamplePage 的 SQL 片段(參數化)。
/// </summary>
internal static class InfSamplePageSql
{
    /// <summary>
    /// 指定 resultId 是來源追溯契約的一部分；值只以參數綁定，不插入 SQL。
    /// </summary>
    public const string ResultFilter = "s.result_id = @resultId";
}
