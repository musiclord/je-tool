namespace JET.Application.Commands.FilterScenario.Rules
{
    /// <summary>
    /// Resolved provider-specific table names plus the active project id, passed to every
    /// evaluator. Evaluators must remain provider-agnostic: take SQL identifiers from
    /// <see cref="ScenarioContext"/> and never hard-code physical names.
    /// </summary>
    public sealed record ScenarioContext(
        string ProjectId,
        string GlTable,
        string AccountMappingTable,
        string ImportBatchTable,
        string ProjectTable);

    /// <summary>
    /// SQL fragment produced by <see cref="IScenarioRuleEvaluator"/>. The
    /// <see cref="Predicate"/> is a boolean SQL expression that references the GL alias
    /// <c>g</c> (e.g. <c>g.amount &lt; 0</c>). The composer combines these with AND/OR
    /// inside a single WHERE clause; no UNION/INTERSECT, so SQLite parses cleanly.
    /// </summary>
    public sealed record ScenarioRuleSqlFragment(
        string Predicate,
        IReadOnlyDictionary<string, object?> Parameters);

    public interface IScenarioRuleEvaluator
    {
        string RuleType { get; }
        ScenarioRuleSqlFragment Build(ScenarioRule rule, ScenarioContext context, string parameterPrefix);
    }
}
