using System.Globalization;

namespace JET.Application.Commands.FilterScenario.Rules
{
    public sealed class NumRangeRuleEvaluator : IScenarioRuleEvaluator
    {
        public string RuleType => "numRange";

        public ScenarioRuleSqlFragment Build(ScenarioRule rule, ScenarioContext context, string parameterPrefix)
        {
            var column = GlColumnResolver.Resolve(rule.Field);
            var parameters = new Dictionary<string, object?>();
            if (string.IsNullOrEmpty(column))
            {
                return new ScenarioRuleSqlFragment("1 = 0", parameters);
            }

            var clauses = new List<string>();
            if (decimal.TryParse(rule.From, NumberStyles.Any, CultureInfo.InvariantCulture, out var from))
            {
                parameters[$"{parameterPrefix}From"] = from;
                clauses.Add($"CAST(g.{column} AS REAL) >= @{parameterPrefix}From");
            }
            if (decimal.TryParse(rule.To, NumberStyles.Any, CultureInfo.InvariantCulture, out var to))
            {
                parameters[$"{parameterPrefix}To"] = to;
                clauses.Add($"CAST(g.{column} AS REAL) <= @{parameterPrefix}To");
            }
            if (clauses.Count == 0) clauses.Add("1 = 1");
            return new ScenarioRuleSqlFragment("(" + string.Join(" AND ", clauses) + ")", parameters);
        }
    }
}
