namespace JET.Application.Commands.FilterScenario.Rules
{
    /// <summary>
    /// Composes per-rule predicate fragments into a single boolean WHERE clause.
    /// Combines rules within a group with AND/OR (per-rule .Join), and groups with
    /// AND/OR (per-group .Join), preserving the prior LINQ semantics. Avoids
    /// UNION/INTERSECT (SQLite forbids parenthesised compound operands).
    /// </summary>
    public sealed class ScenarioGroupComposer
    {
        private readonly IReadOnlyDictionary<string, IScenarioRuleEvaluator> _evaluators;

        public ScenarioGroupComposer(IEnumerable<IScenarioRuleEvaluator> evaluators)
        {
            _evaluators = evaluators.ToDictionary(e => e.RuleType, StringComparer.OrdinalIgnoreCase);
        }

        public static ScenarioGroupComposer Default() => new(new IScenarioRuleEvaluator[]
        {
            new PrescreenRuleEvaluator(),
            new TextRuleEvaluator(),
            new DateRangeRuleEvaluator(),
            new NumRangeRuleEvaluator(),
            new AccountPairRuleEvaluator(),
            new DrCrOnlyRuleEvaluator(),
            new ManualAutoRuleEvaluator(),
        });

        public bool Supports(string ruleType) => _evaluators.ContainsKey(ruleType ?? string.Empty);

        public ComposedScenario Compose(ScenarioDefinition scenario, ScenarioContext context)
        {
            var allParameters = new Dictionary<string, object?>();
            var groupExprs = new List<(string Sql, string Join)>();

            for (var gi = 0; gi < scenario.Groups.Count; gi++)
            {
                var group = scenario.Groups[gi];
                var ruleExprs = new List<(string Sql, string Join)>();

                for (var ri = 0; ri < group.Rules.Count; ri++)
                {
                    var rule = group.Rules[ri];
                    if (!_evaluators.TryGetValue(rule.Type ?? string.Empty, out var evaluator)) continue;

                    var prefix = $"p{gi}_{ri}_";
                    var fragment = evaluator.Build(rule, context, prefix);
                    foreach (var (key, value) in fragment.Parameters)
                    {
                        allParameters[key] = value;
                    }
                    ruleExprs.Add(("(" + fragment.Predicate + ")", rule.Join ?? "AND"));
                }

                if (ruleExprs.Count == 0) continue;

                var groupSql = ruleExprs[0].Sql;
                for (var i = 1; i < ruleExprs.Count; i++)
                {
                    var op = string.Equals(ruleExprs[i].Join, "OR", StringComparison.OrdinalIgnoreCase) ? "OR" : "AND";
                    groupSql = $"({groupSql} {op} {ruleExprs[i].Sql})";
                }
                groupExprs.Add((groupSql, group.Join ?? "AND"));
            }

            if (groupExprs.Count == 0)
            {
                return new ComposedScenario(string.Empty, allParameters, false);
            }

            var finalSql = groupExprs[0].Sql;
            for (var i = 1; i < groupExprs.Count; i++)
            {
                var op = string.Equals(groupExprs[i].Join, "OR", StringComparison.OrdinalIgnoreCase) ? "OR" : "AND";
                finalSql = $"({finalSql} {op} {groupExprs[i].Sql})";
            }

            return new ComposedScenario(finalSql, allParameters, true);
        }

        public IReadOnlyList<string> Describe(ScenarioDefinition scenario)
        {
            var summary = new List<string>();
            foreach (var group in scenario.Groups)
            {
                foreach (var rule in group.Rules)
                {
                    summary.Add(rule.Type switch
                    {
                        "prescreen" => $"預篩選：{rule.PrescreenKey}",
                        "text" => $"文字條件：{rule.Field}",
                        "dateRange" => $"日期條件：{rule.Field}",
                        "numRange" => $"數值條件：{rule.Field}",
                        "accountPair" => $"借貸組合：借方 {rule.DebitCategory} / 貸方 {rule.CreditCategory}",
                        "drCrOnly" => $"借貸限定：{rule.DrCr}",
                        "manualAuto" => $"分錄性質：{rule.IsManual}",
                        _ => rule.Type ?? "rule"
                    });
                }
            }
            return summary;
        }
    }

    public sealed record ComposedScenario(string Predicate, IReadOnlyDictionary<string, object?> Parameters, bool HasRules);
}
