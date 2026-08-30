namespace JET.Tests.Infrastructure;

/// <summary>
/// Keeps the runtime's compact filter positions bound to the original, deidentified
/// legacy scenario positions. The fixed stage-6 maps contain positions only; the
/// profile-derived guard proves they still describe the captured profiles.
/// </summary>
internal static class LegacyAuditParityFilterScenarioBindings
{
    private static readonly IReadOnlyList<LegacyFilterScenarioId> CaseA =
    [
        LegacyFilterScenarioId.FromOrdinal(1),
        LegacyFilterScenarioId.FromOrdinal(4),
        LegacyFilterScenarioId.FromOrdinal(5),
        LegacyFilterScenarioId.FromOrdinal(6),
    ];

    private static readonly IReadOnlyList<LegacyFilterScenarioId> CaseB =
    [
        LegacyFilterScenarioId.FromOrdinal(1),
    ];

    private static readonly IReadOnlyList<LegacyFilterScenarioId> CurrentCaseA = Enumerable
        .Range(1, 6)
        .Select(LegacyFilterScenarioId.FromOrdinal)
        .ToArray();

    private static readonly IReadOnlyList<LegacyFilterScenarioId> CurrentCaseB = Enumerable
        .Range(1, 2)
        .Select(LegacyFilterScenarioId.FromOrdinal)
        .ToArray();

    internal static IReadOnlyList<LegacyFilterScenarioId> ForCapturedStage6Case(
        LegacyParityCase @case) => @case switch
        {
            LegacyParityCase.CaseA => CaseA,
            LegacyParityCase.CaseB => CaseB,
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };

    internal static IReadOnlyList<LegacyFilterScenarioId> ForCurrentStage6ProfileCase(
        LegacyParityCase @case) => @case switch
        {
            LegacyParityCase.CaseA => CurrentCaseA,
            LegacyParityCase.CaseB => CurrentCaseB,
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };

    internal static IReadOnlyList<LegacyFilterScenarioId> FromProfile(
        LegacyAuditParityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var pendingCriteria = profile.LegacyScenarioCounts
            .Select(count => count.Position)
            .Where(position => profile.PendingFieldIds.Contains(
                $"scenario-{position:D2}-criteria-log",
                StringComparer.Ordinal))
            .ToHashSet();
        var ids = profile.LegacyScenarioCounts
            .OrderBy(count => count.Position)
            .Where(count => !pendingCriteria.Contains(count.Position))
            .Select(count => LegacyFilterScenarioId.FromOrdinal(count.Position))
            .ToArray();
        if (ids.Length != profile.Scenarios.Count
            || ids.Distinct().Count() != ids.Length)
        {
            throw new LegacyAuditParityJourneyCompletenessException(
                "filter.scenario-identities");
        }
        return Array.AsReadOnly(ids);
    }

    internal static LegacyAuditParityObservation RebindCapturedProviderObservation(
        LegacyAuditParityObservation observation,
        IReadOnlyList<LegacyFilterScenarioId> legacyIds)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(legacyIds);
        if (observation.IsLegacy
            || legacyIds.Any(id => !id.IsValid)
            || legacyIds.Distinct().Count() != legacyIds.Count)
        {
            throw new ArgumentException("A provider observation and unique legacy positions are required.");
        }

        var metrics = observation.Metrics;
        var compact = metrics.FilterScenarios
            .OrderBy(pair => pair.Key.Ordinal)
            .ToArray();
        if (compact.Length != legacyIds.Count
            || compact.Select((pair, index) => pair.Key.Ordinal == index + 1).Any(valid => !valid))
        {
            throw new ArgumentException(
                "Captured provider filter observations must use the original compact runtime positions.");
        }
        var rebound = compact
            .Select((pair, index) => new KeyValuePair<LegacyFilterScenarioId, LegacyRowVoucherCounts>(
                legacyIds[index],
                pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var reports = metrics.Reports.ToDictionary(pair => pair.Key, pair => pair.Value);
        reports[LegacyReportKind.CriteriaSelectionReport] = reports[
            LegacyReportKind.CriteriaSelectionReport].RebindCompactCriteriaScenarioPositions(legacyIds);
        var reboundMetrics = new LegacyAuditParityMetrics(
            metrics.Completeness,
            metrics.UnbalancedVoucherCount.RequireValue(
                LegacyAuditParityMetricIds.UnbalancedVoucherCount),
            metrics.Inf,
            metrics.PrescreenRules,
            rebound,
            reports,
            metrics.WeekendUnion,
            metrics.CreatorSummaryLegacyApplicability,
            metrics.RareAccountsLegacyApplicability);
        return LegacyAuditParityObservation.FromProvider(
            observation.Case,
            observation.Provider!.Value,
            reboundMetrics);
    }
}
