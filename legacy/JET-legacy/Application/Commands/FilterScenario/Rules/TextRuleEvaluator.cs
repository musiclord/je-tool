namespace JET.Application.Commands.FilterScenario.Rules
{
    public sealed class TextRuleEvaluator : IScenarioRuleEvaluator
    {
        public string RuleType => "text";

        public ScenarioRuleSqlFragment Build(ScenarioRule rule, ScenarioContext context, string parameterPrefix)
        {
            var column = GlColumnResolver.Resolve(rule.Field);
            var keywords = (rule.Keywords ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(k => k.ToLowerInvariant())
                .ToArray();

            var parameters = new Dictionary<string, object?>();
            if (string.IsNullOrEmpty(column) || keywords.Length == 0)
            {
                return new ScenarioRuleSqlFragment("1 = 0", parameters);
            }

            var mode = (rule.Mode ?? "contains").Trim();
            var negate = string.Equals(mode, "notContains", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "notExact", StringComparison.OrdinalIgnoreCase);
            var exact = string.Equals(mode, "exact", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "notExact", StringComparison.OrdinalIgnoreCase);

            var clauses = new List<string>();
            for (var i = 0; i < keywords.Length; i++)
            {
                var pName = $"{parameterPrefix}kw{i}";
                parameters[pName] = exact ? keywords[i] : $"%{keywords[i]}%";
                clauses.Add(exact
                    ? $"LOWER(COALESCE(g.{column}, '')) = @{pName}"
                    : $"LOWER(COALESCE(g.{column}, '')) LIKE @{pName}");
            }

            var combined = "(" + string.Join(" OR ", clauses) + ")";
            if (negate) combined = $"NOT {combined}";
            return new ScenarioRuleSqlFragment(combined, parameters);
        }
    }
}
