using System.Text.Json;
using System.Text.Json.Serialization;

namespace JET.Tests.Infrastructure;

internal enum PrivateCaseScenarioVerificationFailure
{
    InvalidEvidence,
}

internal sealed class PrivateCaseScenarioVerificationException : InvalidOperationException
{
    internal PrivateCaseScenarioVerificationException(PrivateCaseScenarioVerificationFailure failure)
        : base($"私人案件的篩選情境驗證資料無效（{failure}）。")
    {
        Failure = failure;
    }

    internal PrivateCaseScenarioVerificationFailure Failure { get; }
}

internal sealed record PrivateCaseScenarioActualCount(
    int Position,
    long VoucherCount,
    long RowCount)
{
    /// <summary>JET 畫面上的「符合條件的分錄筆數」；回應沒有這個欄位時是 null。</summary>
    internal long? SummaryRowCount { get; init; }
}

internal sealed class PrivateCaseScenarioVerificationFacts
{
    private PrivateCaseScenarioVerificationFacts(
        IReadOnlyList<PrivateCaseScenarioActualCount> counts)
    {
        Counts = counts;
    }

    [JsonIgnore]
    internal IReadOnlyList<PrivateCaseScenarioActualCount> Counts { get; }

    internal static PrivateCaseScenarioVerificationFacts Capture(JsonElement highRiskSummary)
    {
        if (!highRiskSummary.TryGetProperty("scenarios", out var scenarios)
            || scenarios.ValueKind != JsonValueKind.Array
            || scenarios.GetArrayLength() is < 1 or > 10)
        {
            throw Error();
        }

        var counts = new List<PrivateCaseScenarioActualCount>(scenarios.GetArrayLength());
        var positions = new HashSet<int>();
        foreach (var scenario in scenarios.EnumerateArray())
        {
            var position = RequiredPosition(scenario);
            if (!positions.Add(position))
            {
                throw Error();
            }
            counts.Add(new PrivateCaseScenarioActualCount(
                position,
                RequiredCount(scenario, "voucherHitCount"),
                RequiredCount(scenario, "rowHitCount"))
            {
                SummaryRowCount = scenario.TryGetProperty("summaryRowCount", out _)
                    ? RequiredCount(scenario, "summaryRowCount")
                    : null,
            });
        }
        if (!positions.SetEquals(Enumerable.Range(1, counts.Count)))
        {
            throw Error();
        }

        return new PrivateCaseScenarioVerificationFacts(
            counts.OrderBy(static count => count.Position).ToArray());
    }

    private static int RequiredPosition(JsonElement scenario)
    {
        var value = RequiredCount(scenario, "position");
        return value is >= 1 and <= 10 ? checked((int)value) : throw Error();
    }

    private static long RequiredCount(JsonElement scenario, string property)
    {
        if (!scenario.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var count)
            || count < 0)
        {
            throw Error();
        }
        return count;
    }

    private static PrivateCaseScenarioVerificationException Error() =>
        new(PrivateCaseScenarioVerificationFailure.InvalidEvidence);
}

internal sealed record PrivateCaseScenarioVerificationSummary(
    int Position,
    long ExpectedRowCount,
    long ActualRowCount,
    long? ExpectedVoucherCount,
    long ActualVoucherCount)
{
    public long? ExpandedVoucherRowCount { get; init; }

    public long? ExpandedRowsWhoseDirectHitHasNullOrEmptyDescription { get; init; }

    public long? VouchersWhoseDirectHitHasNullOrEmptyDescription { get; init; }

    public long? ExpandedRowsWithLegacyTextMatch { get; init; }

    public long? VouchersWithLegacyTextMatch { get; init; }

    public long? SameRowRuleMatchRowCount { get; init; }

    public long? SameRowRuleMatchVoucherCount { get; init; }

    public long? WeekendIncludingMakeupRowCount { get; init; }

    public long? WeekendIncludingMakeupVoucherCount { get; init; }

    /// <summary>JET 回傳的「符合條件的分錄筆數」。</summary>
    public long? ActualSummaryRowCount { get; init; }

    public bool SummaryRowCountCompared => ActualSummaryRowCount.HasValue;

    // 只用一條「未預期借貸組合」的情境，「符合條件的分錄筆數」是整張命中傳票的分錄數，要等於驗證框架展開的列數；
    // 其他情境是直接符合的分錄數。
    public bool SummaryRowCountMatches => !ActualSummaryRowCount.HasValue
        || RowCountBasis switch
        {
            PrivateCaseScenarioRowCountBasis.DirectHitRows =>
                ActualSummaryRowCount.Value == ActualRowCount,
            PrivateCaseScenarioRowCountBasis.ExportedVoucherRows =>
                ExpandedVoucherRowCount.HasValue
                && ActualSummaryRowCount.Value == ExpandedVoucherRowCount.Value,
            _ => false,
        };

    [JsonIgnore]
    internal PrivateCaseScenarioRowCountBasis RowCountBasis { get; init; }

    public bool RowCountMatches => RowCountBasis switch
    {
        PrivateCaseScenarioRowCountBasis.DirectHitRows =>
            ExpectedRowCount == ActualRowCount,
        PrivateCaseScenarioRowCountBasis.ExportedVoucherRows =>
            ExpandedVoucherRowsMatchExpected,
        _ => false,
    };

    public bool VoucherCountCompared => ExpectedVoucherCount.HasValue;

    public bool VoucherCountMatches => !ExpectedVoucherCount.HasValue
        || ExpectedVoucherCount.Value == ActualVoucherCount;

    public bool Passed => RowCountMatches
        && (!VoucherCountCompared || VoucherCountMatches)
        && SummaryRowCountMatches;

    public bool ExpandedVoucherRowsMatchExpected =>
        ExpandedVoucherRowCount.HasValue
        && ExpandedVoucherRowCount.Value == ExpectedRowCount;

    public bool DirectHitNullOrEmptyDescriptionVoucherRowsMatchExpected =>
        ExpandedRowsWhoseDirectHitHasNullOrEmptyDescription.HasValue
        && ExpandedRowsWhoseDirectHitHasNullOrEmptyDescription.Value == ExpectedRowCount
        && (!ExpectedVoucherCount.HasValue
            || VouchersWhoseDirectHitHasNullOrEmptyDescription == ExpectedVoucherCount.Value);

    public bool LegacyTextVoucherRowsMatchExpected =>
        ExpandedRowsWithLegacyTextMatch.HasValue
        && ExpandedRowsWithLegacyTextMatch.Value == ExpectedRowCount
        && (!ExpectedVoucherCount.HasValue
            || VouchersWithLegacyTextMatch == ExpectedVoucherCount.Value);

    public bool SameRowRuleVoucherCountMatchesExpected =>
        SameRowRuleMatchVoucherCount.HasValue
        && (!ExpectedVoucherCount.HasValue
            || SameRowRuleMatchVoucherCount == ExpectedVoucherCount.Value);

    public bool WeekendIncludingMakeupMatchesExpected =>
        WeekendIncludingMakeupRowCount.HasValue
        && WeekendIncludingMakeupRowCount.Value == ExpectedRowCount
        && (!ExpectedVoucherCount.HasValue
            || WeekendIncludingMakeupVoucherCount == ExpectedVoucherCount.Value);
}

internal sealed class PrivateCaseScenarioVerificationResult
{
    internal PrivateCaseScenarioVerificationResult(
        IReadOnlyList<PrivateCaseScenarioVerificationSummary> scenarios)
    {
        Scenarios = scenarios;
    }

    public int ScenarioCount => Scenarios.Count;

    public int FailedScenarioCount => Scenarios.Count(static scenario => !scenario.Passed);

    public bool Passed => FailedScenarioCount == 0;

    public IReadOnlyList<PrivateCaseScenarioVerificationSummary> Scenarios { get; }

    public override string ToString() =>
        $"private case scenario verification ({ScenarioCount} scenarios)";
}

internal static class PrivateCaseScenarioVerifier
{
    internal static PrivateCaseScenarioVerificationResult Verify(
        IReadOnlyList<PrivateCaseScenarioCount> expected,
        PrivateCaseScenarioVerificationFacts actual,
        PrivateCaseScenarioDiagnosticFacts? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        if (expected.Count is < 1 or > 10
            || actual.Counts.Count != expected.Count
            || expected.Any(static count => count.Position is < 1 or > 10
                || count.RowCount < 0
                || count.VoucherCount < 0
                || count.RowCountBasis is not PrivateCaseScenarioRowCountBasis.DirectHitRows
                    and not PrivateCaseScenarioRowCountBasis.ExportedVoucherRows)
            || (diagnostics is null
                && expected.Any(static count =>
                    count.RowCountBasis == PrivateCaseScenarioRowCountBasis.ExportedVoucherRows))
            || expected.Select(static count => count.Position).Distinct().Count() != expected.Count)
        {
            throw new PrivateCaseScenarioVerificationException(
                PrivateCaseScenarioVerificationFailure.InvalidEvidence);
        }

        var actualByPosition = actual.Counts.ToDictionary(static count => count.Position);
        if (!expected.All(count => actualByPosition.ContainsKey(count.Position)))
        {
            throw new PrivateCaseScenarioVerificationException(
                PrivateCaseScenarioVerificationFailure.InvalidEvidence);
        }

        IReadOnlyDictionary<int, PrivateCaseScenarioDiagnosticCount>? diagnosticsByPosition = null;
        if (diagnostics is not null)
        {
            if (diagnostics.Counts.Count != expected.Count
                || diagnostics.Counts.Select(static count => count.Position).Distinct().Count() != expected.Count
                || diagnostics.Counts.Any(static count =>
                    count.Position is < 1 or > 10
                    || count.ExpandedVoucherRowCount < 0
                    || count.ExpandedVoucherCount < 0
                    || (count.ExpandedRowsWhoseDirectHitHasNullOrEmptyDescription.HasValue
                        != count.VouchersWhoseDirectHitHasNullOrEmptyDescription.HasValue)
                    || count.ExpandedRowsWhoseDirectHitHasNullOrEmptyDescription < 0
                    || count.VouchersWhoseDirectHitHasNullOrEmptyDescription < 0
                    || (count.ExpandedRowsWithLegacyTextMatch.HasValue
                        != count.VouchersWithLegacyTextMatch.HasValue)
                    || count.ExpandedRowsWithLegacyTextMatch < 0
                    || count.VouchersWithLegacyTextMatch < 0
                    || (count.SameRowRuleMatchRowCount.HasValue
                        != count.SameRowRuleMatchVoucherCount.HasValue)
                    || count.SameRowRuleMatchRowCount < 0
                    || count.SameRowRuleMatchVoucherCount < 0
                    || (count.WeekendIncludingMakeupRowCount.HasValue
                        != count.WeekendIncludingMakeupVoucherCount.HasValue)
                    || count.WeekendIncludingMakeupRowCount < 0
                    || count.WeekendIncludingMakeupVoucherCount < 0))
            {
                throw new PrivateCaseScenarioVerificationException(
                    PrivateCaseScenarioVerificationFailure.InvalidEvidence);
            }
            diagnosticsByPosition = diagnostics.Counts.ToDictionary(static count => count.Position);
            if (!expected.All(count => diagnosticsByPosition.ContainsKey(count.Position)))
            {
                throw new PrivateCaseScenarioVerificationException(
                    PrivateCaseScenarioVerificationFailure.InvalidEvidence);
            }
        }

        return new PrivateCaseScenarioVerificationResult(
            expected
                .OrderBy(static count => count.Position)
                .Select(count =>
                {
                    var observed = actualByPosition[count.Position];
                    var diagnostic = diagnosticsByPosition?.GetValueOrDefault(count.Position);
                    return new PrivateCaseScenarioVerificationSummary(
                        count.Position,
                        count.RowCount,
                        observed.RowCount,
                        count.VoucherCount,
                        observed.VoucherCount)
                    {
                        RowCountBasis = count.RowCountBasis,
                        ActualSummaryRowCount = observed.SummaryRowCount,
                        ExpandedVoucherRowCount = diagnostic?.ExpandedVoucherRowCount,
                        ExpandedRowsWhoseDirectHitHasNullOrEmptyDescription =
                            diagnostic?.ExpandedRowsWhoseDirectHitHasNullOrEmptyDescription,
                        VouchersWhoseDirectHitHasNullOrEmptyDescription =
                            diagnostic?.VouchersWhoseDirectHitHasNullOrEmptyDescription,
                        ExpandedRowsWithLegacyTextMatch =
                            diagnostic?.ExpandedRowsWithLegacyTextMatch,
                        VouchersWithLegacyTextMatch =
                            diagnostic?.VouchersWithLegacyTextMatch,
                        SameRowRuleMatchRowCount =
                            diagnostic?.SameRowRuleMatchRowCount,
                        SameRowRuleMatchVoucherCount =
                            diagnostic?.SameRowRuleMatchVoucherCount,
                        WeekendIncludingMakeupRowCount =
                            diagnostic?.WeekendIncludingMakeupRowCount,
                        WeekendIncludingMakeupVoucherCount =
                            diagnostic?.WeekendIncludingMakeupVoucherCount,
                    };
                })
                .ToArray());
    }
}
