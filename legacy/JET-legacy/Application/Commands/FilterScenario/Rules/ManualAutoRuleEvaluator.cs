namespace JET.Application.Commands.FilterScenario.Rules
{
    public sealed class ManualAutoRuleEvaluator : IScenarioRuleEvaluator
    {
        public string RuleType => "manualAuto";

        public ScenarioRuleSqlFragment Build(ScenarioRule rule, ScenarioContext context, string parameterPrefix)
        {
            var wantManual = !string.Equals(rule.IsManual, "false", StringComparison.OrdinalIgnoreCase);
            var predicate = wantManual
                ? "g.manual = 1"
                : "(g.manual IS NULL OR g.manual <> 1)";
            return new ScenarioRuleSqlFragment(predicate, new Dictionary<string, object?>());
        }
    }
}
