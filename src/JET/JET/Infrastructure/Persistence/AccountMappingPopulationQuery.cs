using JET.AuditCore;

namespace JET.Infrastructure;

/// <summary>
/// 可配對的科目母體：有效總帳和試算表的聯集。空白編號由資料品質明細揭露，不列成可配對科目。
/// not_in_tb 是完整性 diff 的旗標（GL 有、TB 沒有 = 1）；同一個科目只會出現在 diff 的其中一段，所以取 MAX 不會混到兩段。
/// 科目名稱就是完整性 diff 的名稱：diff 裡同一個科目編號只有一列，這裡的 MAX 只是配合分組，不會換成別的名稱。
/// </summary>
internal static class AccountMappingPopulationQuery
{
    /// <param name="prefix">資料表名稱前的限定詞；SQLite 使用空字串。</param>
    /// <param name="dialect">空白名稱與空白科目編號的判定照它的寫法。</param>
    /// <param name="useTbAccountNames">
    /// 畫面上的科目配對清單、「配對檔未列的科目」與資料預覽的科目配對用預設值：先取 TB 的名稱，TB 沒有名稱時取 GL 的名稱。
    /// 科目配對範本與畫面分類時要存的名稱傳 true：只取 TB 的名稱，TB 沒有名稱或科目只在 GL 時是 NULL。
    /// </param>
    internal static string Cte(string prefix, ISqlDialect dialect, bool useTbAccountNames = false) =>
        ValidationProcedures.CompletenessDiffCteFor(dialect, prefix, useTbAccountNames) + $$"""
        , population AS (
            SELECT account_number, MAX(account_name) AS account_name, MAX(not_in_tb) AS not_in_tb
            FROM diff
            WHERE {{SqlBlankText.IsNotBlank(dialect, "account_number")}}
            GROUP BY account_number
        )
        """;
}
