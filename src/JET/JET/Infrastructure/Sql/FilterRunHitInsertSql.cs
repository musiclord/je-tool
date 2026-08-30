namespace JET.Infrastructure;

/// <summary>
/// filter.commit 與惰性補算共用的 result_filter_run INSERT…SELECT 骨架。
/// Provider 仍各自提供 table prefix、population predicate 與已組譯的情境述詞。
/// </summary>
internal static class FilterRunHitInsertSql
{
    public static string Build(
        string tablePrefix,
        string populationPredicateSql,
        string scenarioPredicateSql) =>
        $"INSERT INTO {tablePrefix}result_filter_run (scenario_position, entry_id) "
        + $"SELECT @scenarioPosition, g.entry_id FROM {tablePrefix}target_gl_entry g "
        + $"WHERE {populationPredicateSql} AND ({scenarioPredicateSql});";
}
