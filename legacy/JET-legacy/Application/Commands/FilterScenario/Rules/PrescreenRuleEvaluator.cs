namespace JET.Application.Commands.FilterScenario.Rules
{
    /// <summary>
    /// Maps the prescreen rule key (R1 / R2 / R3 / R4 / R5 / R6 / descNull) onto the same
    /// SQL predicates used by <c>SqlitePreScreenRepository</c>. Returns a boolean
    /// expression over GL alias <c>g</c>.
    /// </summary>
    public sealed class PrescreenRuleEvaluator : IScenarioRuleEvaluator
    {
        public string RuleType => "prescreen";

        public ScenarioRuleSqlFragment Build(ScenarioRule rule, ScenarioContext context, string parameterPrefix)
        {
            var key = (rule.PrescreenKey ?? string.Empty).Trim().ToLowerInvariant();
            var parameters = new Dictionary<string, object?>
            {
                [$"{parameterPrefix}ProjectId"] = context.ProjectId
            };

            string predicate = key switch
            {
                "r1" => $"COALESCE(NULLIF(TRIM(g.doc_date), ''), NULLIF(TRIM(g.post_date), '')) >= (SELECT last_period_start FROM {context.ProjectTable} WHERE project_id = @{parameterPrefix}ProjectId)",
                "r2" => "(LOWER(COALESCE(g.description, '')) LIKE '%adj%' OR LOWER(COALESCE(g.description, '')) LIKE '%rev%' OR LOWER(COALESCE(g.description, '')) LIKE '%reclass%' OR LOWER(COALESCE(g.description, '')) LIKE '%suspense%' OR LOWER(COALESCE(g.description, '')) LIKE '%error%' OR LOWER(COALESCE(g.description, '')) LIKE '%wrong%' OR COALESCE(g.description, '') LIKE '%調整%' OR COALESCE(g.description, '') LIKE '%迴轉%' OR COALESCE(g.description, '') LIKE '%沖銷%' OR COALESCE(g.description, '') LIKE '%重分類%' OR COALESCE(g.description, '') LIKE '%避險%' OR COALESCE(g.description, '') LIKE '%重編%' OR COALESCE(g.description, '') LIKE '%錯誤%' OR COALESCE(g.description, '') LIKE '%計畫外%' OR COALESCE(g.description, '') LIKE '%預算外%')",
                "r3" => BuildR3Predicate(context, parameterPrefix),
                "r4" => "ABS(CAST(g.amount AS INTEGER)) >= 1000 AND ABS(CAST(g.amount AS INTEGER)) % 1000 = 0",
                "r5" => "g.create_by IS NOT NULL AND TRIM(g.create_by) <> ''",
                "r6" => $"g.acc_num IN (WITH freq AS (SELECT acc_num, COUNT(1) AS cnt FROM {context.GlTable} WHERE project_id = @{parameterPrefix}ProjectId AND acc_num IS NOT NULL AND TRIM(acc_num) <> '' GROUP BY acc_num), avg_freq AS (SELECT AVG(cnt) AS avg_cnt FROM freq) SELECT freq.acc_num FROM freq, avg_freq WHERE freq.cnt < avg_freq.avg_cnt * 0.25)",
                "descnull" => "(g.description IS NULL OR TRIM(g.description) = '')",
                _ => "1 = 0"
            };

            return new ScenarioRuleSqlFragment(predicate, parameters);
        }

        private static string BuildR3Predicate(ScenarioContext context, string prefix)
        {
            return $@"g.doc_num IN (
                SELECT g2.doc_num
                FROM {context.GlTable} g2
                LEFT JOIN (
                    SELECT TRIM(json_extract(s.payload, '$[0]')) AS acc,
                           LOWER(COALESCE(json_extract(s.payload, '$[2]'), '')) AS cat
                    FROM {context.AccountMappingTable} s
                    WHERE s.batch_id = (
                        SELECT batch_id FROM {context.ImportBatchTable}
                        WHERE project_id = @{prefix}ProjectId AND dataset_kind = 'accountMapping'
                        ORDER BY imported_utc DESC LIMIT 1
                    )
                ) m ON m.acc = TRIM(COALESCE(g2.acc_num, ''))
                WHERE g2.project_id = @{prefix}ProjectId
                GROUP BY g2.doc_num
                HAVING SUM(CASE WHEN (m.cat LIKE '%revenue%' OR m.cat LIKE '%income%' OR m.cat LIKE '%收入%') AND g2.amount < 0 THEN 1 ELSE 0 END) >= 1
                   AND SUM(CASE WHEN (m.cat LIKE '%receivable%' OR m.cat LIKE '%應收%' OR m.cat LIKE '%cash%' OR m.cat LIKE '%bank%' OR m.cat LIKE '%現金%' OR m.cat LIKE '%advance%' OR m.cat LIKE '%deferred%' OR m.cat LIKE '%預收%') AND g2.amount > 0 THEN 1 ELSE 0 END) >= 1
            )";
        }
    }
}
