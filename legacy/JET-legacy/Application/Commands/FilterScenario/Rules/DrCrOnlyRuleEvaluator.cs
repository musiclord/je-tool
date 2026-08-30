namespace JET.Application.Commands.FilterScenario.Rules
{
    public sealed class DrCrOnlyRuleEvaluator : IScenarioRuleEvaluator
    {
        public string RuleType => "drCrOnly";

        public ScenarioRuleSqlFragment Build(ScenarioRule rule, ScenarioContext context, string parameterPrefix)
        {
            var predicate = string.Equals(rule.DrCr, "credit", StringComparison.OrdinalIgnoreCase)
                ? "g.amount < 0"
                : "g.amount >= 0";
            return new ScenarioRuleSqlFragment(predicate, new Dictionary<string, object?>());
        }
    }
}
