namespace JET.Application.Commands.FilterScenario.Rules
{
    /// <summary>
    /// Single rule inside a scenario group, parsed from the bridge JSON payload.
    /// Mirrors the contract documented under <c>filter.preview</c> in
    /// <c>docs/action-contract-manifest.md</c>.
    /// </summary>
    public sealed class ScenarioRule
    {
        public string Join { get; init; } = "AND";
        public string Type { get; init; } = string.Empty;
        public string PrescreenKey { get; init; } = string.Empty;
        public string Field { get; init; } = string.Empty;
        public string Keywords { get; init; } = string.Empty;
        public string Mode { get; init; } = "contains";
        public string From { get; init; } = string.Empty;
        public string To { get; init; } = string.Empty;
        public string DebitCategory { get; init; } = string.Empty;
        public string CreditCategory { get; init; } = string.Empty;
        public string DrCr { get; init; } = "debit";
        public string IsManual { get; init; } = "true";
    }

    public sealed class ScenarioGroup
    {
        public string Join { get; init; } = "AND";
        public IReadOnlyList<ScenarioRule> Rules { get; init; } = Array.Empty<ScenarioRule>();
    }

    public sealed class ScenarioDefinition
    {
        public string Name { get; init; } = string.Empty;
        public IReadOnlyList<ScenarioGroup> Groups { get; init; } = Array.Empty<ScenarioGroup>();
    }
}
