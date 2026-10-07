using JET.AuditCore;

namespace JET.Infrastructure;

/// <summary>可配對的科目母體：有效總帳和試算表的聯集。空白編號由資料品質明細揭露，不列成可配對科目。</summary>
internal static class AccountMappingPopulationQuery
{
    internal static string Cte(string prefix, ISqlDialect dialect) =>
        ValidationProcedures.CompletenessDiffCteFor(prefix) + $$"""
        , population AS (
            SELECT account_code, MAX(account_name) AS account_name
            FROM diff
            WHERE account_code IS NOT NULL AND {{dialect.Trim("account_code")}} <> ''
            GROUP BY account_code
        )
        """;
}
