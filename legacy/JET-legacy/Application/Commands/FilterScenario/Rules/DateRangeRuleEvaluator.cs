namespace JET.Application.Commands.FilterScenario.Rules
{
    public sealed class DateRangeRuleEvaluator : IScenarioRuleEvaluator
    {
        public string RuleType => "dateRange";

        public ScenarioRuleSqlFragment Build(ScenarioRule rule, ScenarioContext context, string parameterPrefix)
        {
            var column = GlColumnResolver.Resolve(rule.Field);
            var parameters = new Dictionary<string, object?>();
            if (string.IsNullOrEmpty(column))
            {
                return new ScenarioRuleSqlFragment("1 = 0", parameters);
            }

            var clauses = new List<string> { $"g.{column} IS NOT NULL", $"TRIM(g.{column}) <> ''" };
            if (!string.IsNullOrWhiteSpace(rule.From))
            {
                parameters[$"{parameterPrefix}From"] = rule.From.Trim();
                clauses.Add($"g.{column} >= @{parameterPrefix}From");
            }
            if (!string.IsNullOrWhiteSpace(rule.To))
            {
                parameters[$"{parameterPrefix}To"] = rule.To.Trim();
                clauses.Add($"g.{column} <= @{parameterPrefix}To");
            }
            return new ScenarioRuleSqlFragment("(" + string.Join(" AND ", clauses) + ")", parameters);
        }
    }
}
