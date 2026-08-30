namespace JET.Application.Commands.FilterScenario.Rules
{
    /// <summary>
    /// Voucher-level rule: matched DocNum has at least one debit line whose account
    /// belongs to <c>DebitCategory</c> AND at least one credit line whose account
    /// belongs to <c>CreditCategory</c>.
    /// </summary>
    public sealed class AccountPairRuleEvaluator : IScenarioRuleEvaluator
    {
        public string RuleType => "accountPair";

        public ScenarioRuleSqlFragment Build(ScenarioRule rule, ScenarioContext context, string parameterPrefix)
        {
            var parameters = new Dictionary<string, object?>
            {
                [$"{parameterPrefix}ProjectId"] = context.ProjectId,
                [$"{parameterPrefix}Debit"] = (rule.DebitCategory ?? string.Empty).Trim().ToLowerInvariant(),
                [$"{parameterPrefix}Credit"] = (rule.CreditCategory ?? string.Empty).Trim().ToLowerInvariant()
            };

            var predicate = $@"g.doc_num IN (
                SELECT g2.doc_num FROM {context.GlTable} g2
                LEFT JOIN (
                    SELECT TRIM(json_extract(s.payload, '$[0]')) AS acc,
                           LOWER(COALESCE(json_extract(s.payload, '$[2]'), '')) AS cat
                    FROM {context.AccountMappingTable} s
                    WHERE s.batch_id = (
                        SELECT batch_id FROM {context.ImportBatchTable}
                        WHERE project_id = @{parameterPrefix}ProjectId AND dataset_kind = 'accountMapping'
                        ORDER BY imported_utc DESC LIMIT 1
                    )
                ) m ON m.acc = TRIM(COALESCE(g2.acc_num, ''))
                WHERE g2.project_id = @{parameterPrefix}ProjectId
                GROUP BY g2.doc_num
                HAVING SUM(CASE WHEN @{parameterPrefix}Debit <> '' AND m.cat LIKE '%' || @{parameterPrefix}Debit || '%' AND g2.amount >= 0 THEN 1 ELSE 0 END) >= 1
                   AND SUM(CASE WHEN @{parameterPrefix}Credit <> '' AND m.cat LIKE '%' || @{parameterPrefix}Credit || '%' AND g2.amount <= 0 THEN 1 ELSE 0 END) >= 1
            )";
            return new ScenarioRuleSqlFragment(predicate, parameters);
        }
    }
}
