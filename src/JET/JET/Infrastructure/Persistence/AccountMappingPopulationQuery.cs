using JET.AuditCore;

namespace JET.Infrastructure;

/// <summary>
/// 可配對的科目母體：有效總帳和試算表的聯集。空白編號由資料品質明細揭露，不列成可配對科目。
/// not_in_tb 是完整性 diff 的旗標（GL 有、TB 沒有 = 1）；同一個科目只會出現在 diff 的其中一段，所以取 MAX 不會混到兩段。
/// </summary>
internal static class AccountMappingPopulationQuery
{
    internal static string Cte(string prefix, ISqlDialect dialect) =>
        ValidationProcedures.CompletenessDiffCteFor(prefix) + $$"""
        , population AS (
            SELECT account_number, MAX(account_name) AS account_name, MAX(not_in_tb) AS not_in_tb
            FROM diff
            WHERE {{SqlBlankText.IsNotBlank(dialect, "account_number")}}
            GROUP BY account_number
        )
        """;
}
