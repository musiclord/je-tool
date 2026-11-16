namespace JET.Tests.Infrastructure;

internal enum PrivateCaseScenarioPopulationComparisonFailure
{
    InvalidEvidence,
}

internal sealed class PrivateCaseScenarioPopulationComparisonException : InvalidOperationException
{
    internal PrivateCaseScenarioPopulationComparisonException(
        PrivateCaseScenarioPopulationComparisonFailure failure)
        : base($"私人案件的篩選傳票集合無法核對（{failure}）。")
    {
        Failure = failure;
    }

    internal PrivateCaseScenarioPopulationComparisonFailure Failure { get; }
}

internal sealed record PrivateCaseScenarioPopulationComparisonSummary(
    int Position,
    long LegacyVoucherCount,
    long CurrentVoucherCount,
    long OverlapVoucherCount,
    long LegacyOnlyVoucherCount,
    long CurrentOnlyVoucherCount)
{
    public bool Matches => LegacyOnlyVoucherCount == 0 && CurrentOnlyVoucherCount == 0;
}

internal sealed class PrivateCaseScenarioPopulationComparisonResult
{
    internal PrivateCaseScenarioPopulationComparisonResult(
        IReadOnlyList<PrivateCaseScenarioPopulationComparisonSummary> scenarios)
    {
        Scenarios = scenarios;
    }

    public IReadOnlyList<PrivateCaseScenarioPopulationComparisonSummary> Scenarios { get; }

    public bool Matches => Scenarios.All(static scenario => scenario.Matches);

    public override string ToString() =>
        $"private case scenario population comparison ({Scenarios.Count} scenarios)";
}

internal static class PrivateCaseScenarioPopulationComparator
{
    internal static PrivateCaseScenarioPopulationComparisonResult Compare(
        PrivateCaseLegacyScenarioEvidenceResult legacy,
        PrivateCaseScenarioDiagnosticFacts current)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(current);

        var legacySets = legacy.SelectedVoucherNumbersByPosition;
        var currentSets = current.SelectedVoucherNumbersByPosition;
        if (legacySets.Count != legacy.ScenarioCount
            || currentSets.Count != legacy.ScenarioCount
            || !legacySets.Keys.Order().SequenceEqual(Enumerable.Range(1, legacy.ScenarioCount))
            || !currentSets.Keys.Order().SequenceEqual(Enumerable.Range(1, legacy.ScenarioCount)))
        {
            throw Error();
        }

        var summaries = Enumerable.Range(1, legacy.ScenarioCount)
            .Select(position =>
            {
                var legacySet = legacySets[position];
                var currentSet = currentSets[position];
                if (legacySet is null || currentSet is null)
                {
                    throw Error();
                }

                var overlap = legacySet.LongCount(currentSet.Contains);
                return new PrivateCaseScenarioPopulationComparisonSummary(
                    position,
                    legacySet.Count,
                    currentSet.Count,
                    overlap,
                    legacySet.Count - overlap,
                    currentSet.Count - overlap);
            })
            .ToArray();

        return new PrivateCaseScenarioPopulationComparisonResult(summaries);
    }

    private static PrivateCaseScenarioPopulationComparisonException Error() =>
        new(PrivateCaseScenarioPopulationComparisonFailure.InvalidEvidence);
}
