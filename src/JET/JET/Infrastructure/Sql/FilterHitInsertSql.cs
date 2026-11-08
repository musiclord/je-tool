using System.Data.Common;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Infrastructure;

/// <summary>
/// 寫入一個情境結果的唯一寫法：filter_hit 存命中的分錄，filter_hit_condition 存命中傳票裡每筆分錄符合第幾組第幾條。
/// 儲存一個情境、整批發布與結果過期後重算都經過這裡；三種資料庫只差表名前綴與建立命令的方式。
/// 結果在儲存情境時存好，檢視、換頁、展開與匯出都讀它（裁定 2026-10-30-33）。
/// </summary>
internal static class FilterHitInsertSql
{
    public static string Build(
        string tablePrefix,
        string populationPredicateSql,
        string scenarioPredicateSql) =>
        $"INSERT INTO {tablePrefix}filter_hit (scenario_id, entry_id) "
        + $"SELECT @scenarioId, g.entry_id FROM {tablePrefix}gl g "
        + $"WHERE {populationPredicateSql} AND ({scenarioPredicateSql});";

    /// <summary>
    /// 一條條件在命中傳票裡符合的分錄。只看這個情境已寫入的命中傳票；同一張傳票裡沒命中的分錄也可能符合其中幾條，
    /// 檢視時照樣寫出「符合主要條件」或「整張傳票條件成立」。
    /// </summary>
    public static string BuildCondition(
        string tablePrefix,
        string populationPredicateSql,
        string conditionPredicateSql) =>
        $"INSERT INTO {tablePrefix}filter_hit_condition (scenario_id, entry_id, group_no, condition_no) "
        + $"SELECT @scenarioId, g.entry_id, @groupNo, @conditionNo FROM {tablePrefix}gl g "
        + $"WHERE {populationPredicateSql} AND g.voucher_number IN ("
        + $"SELECT hg.voucher_number FROM {tablePrefix}filter_hit hh JOIN {tablePrefix}gl hg ON hg.entry_id = hh.entry_id "
        + "WHERE hh.scenario_id = @scenarioId AND hg.voucher_number IS NOT NULL) "
        + $"AND ({conditionPredicateSql});";

    /// <summary>刪掉一個情境的結果；先刪符合第幾組第幾條，再刪命中。</summary>
    public static IReadOnlyList<string> DeleteOne(string tablePrefix) =>
    [
        $"DELETE FROM {tablePrefix}filter_hit_condition WHERE scenario_id = @scenarioId;",
        $"DELETE FROM {tablePrefix}filter_hit WHERE scenario_id = @scenarioId;"
    ];

    /// <summary>刪掉全部情境的結果。</summary>
    public static IReadOnlyList<string> DeleteAll(string tablePrefix) =>
    [
        $"DELETE FROM {tablePrefix}filter_hit_condition;",
        $"DELETE FROM {tablePrefix}filter_hit;"
    ];

    /// <summary>
    /// 在呼叫端的交易裡寫入一個情境的命中與符合第幾組第幾條。<paramref name="createCommand"/> 依資料庫建立命令
    /// （原本 SQL Server 會把 <c>{s}</c> 換成案件的 schema）；<paramref name="schemaPrefix"/> 是條件述詞用的表名前綴。
    /// </summary>
    public static async Task InsertOneAsync(
        Func<string, DbCommand> createCommand,
        DbTransaction transaction,
        ISqlDialect dialect,
        string tablePrefix,
        string schemaPrefix,
        int scenarioId,
        FilterScenarioSpec spec,
        FilterConditionContext context,
        ILogger log,
        string provider,
        CancellationToken cancellationToken)
    {
        var builder = new GlFilterWhereBuilder(dialect, new GlRulePredicates(dialect, GlPopulationScopeSql.Predicate));
        var plan = builder.BuildPlan(spec, context, schemaPrefix, includeEvidence: true);
        var population = GlPopulationScopeSql.Predicate(context, "g");

        await using (var hits = createCommand(Build(tablePrefix, population, plan.Sql)))
        {
            hits.Transaction = transaction;
            plan.BindParametersTo(hits);
            hits.AddWithValue("@scenarioId", scenarioId);
            await hits.ExecuteNonQueryLoggedAsync(log, provider, cancellationToken);
        }

        foreach (var evidence in plan.EvidencePredicates)
        {
            var sql = BuildCondition(tablePrefix, population, evidence.Predicate);
            await using var conditions = createCommand(sql);
            conditions.Transaction = transaction;
            plan.BindParametersUsedBy(conditions, evidence.Predicate);
            conditions.AddWithValue("@scenarioId", scenarioId);
            conditions.AddWithValue("@groupNo", evidence.Position.Group);
            conditions.AddWithValue("@conditionNo", evidence.Position.Rule);
            await conditions.ExecuteNonQueryLoggedAsync(log, provider, cancellationToken);
        }
    }
}
