namespace JET.Infrastructure;

/// <summary>
/// infSamplePage 共用 SQL 片段(雙 provider 同一份;參數化、無方言差異)。
/// </summary>
internal static class InfSamplePageSql
{
    /// <summary>
    /// 指定 resultId 是來源追溯契約的一部分；值只以參數綁定，不插入 SQL。
    /// </summary>
    public const string ResultFilter = "s.result_id = @resultId";
}
