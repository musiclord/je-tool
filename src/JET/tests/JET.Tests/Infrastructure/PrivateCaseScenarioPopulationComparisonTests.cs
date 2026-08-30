using System.Text.Json;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class PrivateCaseScenarioPopulationComparisonTests
{
    [Fact]
    public void Compare_ReportsOnlyAggregateOverlapCounts()
    {
        var legacy = new PrivateCaseLegacyScenarioEvidenceResult(
            1,
            new Dictionary<int, IReadOnlySet<string>>
            {
                [1] = new HashSet<string>(["legacy-shared", "legacy-only"], StringComparer.Ordinal),
            });
        var current = new PrivateCaseScenarioDiagnosticFacts(
            [],
            new Dictionary<int, IReadOnlySet<string>>
            {
                [1] = new HashSet<string>(["legacy-shared", "current-only"], StringComparer.Ordinal),
            });

        var result = PrivateCaseScenarioPopulationComparator.Compare(legacy, current);

        var scenario = Assert.Single(result.Scenarios);
        Assert.False(result.Matches);
        Assert.Equal(1, scenario.OverlapVoucherCount);
        Assert.Equal(1, scenario.LegacyOnlyVoucherCount);
        Assert.Equal(1, scenario.CurrentOnlyVoucherCount);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("legacy-shared", json, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-only", json, StringComparison.Ordinal);
        Assert.DoesNotContain("current-only", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_MissingInMemorySets_AreRejectedWithAFixedFailure()
    {
        var exception = Assert.Throws<PrivateCaseScenarioPopulationComparisonException>(() =>
            PrivateCaseScenarioPopulationComparator.Compare(
                new PrivateCaseLegacyScenarioEvidenceResult(1),
                new PrivateCaseScenarioDiagnosticFacts([])));

        Assert.Equal(
            PrivateCaseScenarioPopulationComparisonFailure.InvalidEvidence,
            exception.Failure);
    }
}
