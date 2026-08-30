using System.Text.Json;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class PrivateCaseScenarioVerifierTests
{
    [Fact]
    public void Verify_MatchingRowsAndVouchers_PassesWithoutSerializingCounts()
    {
        var facts = Capture((1, 3, 5), (2, 7, 11));

        var result = PrivateCaseScenarioVerifier.Verify(
            [
                new PrivateCaseScenarioCount(1, 3, 5),
                new PrivateCaseScenarioCount(2, 7, 11),
            ],
            facts);

        Assert.True(result.Passed);
        Assert.Equal(2, result.ScenarioCount);
        Assert.Equal(0, result.FailedScenarioCount);
        Assert.All(result.Scenarios, static scenario => Assert.True(scenario.Passed));
        Assert.StartsWith("private case scenario verification", result.ToString(), StringComparison.Ordinal);

        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("rowHitCount", json, StringComparison.Ordinal);
        Assert.DoesNotContain("voucherHitCount", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_NullLegacyVoucherCount_UsesTheLegacyRowCountOnly()
    {
        var result = PrivateCaseScenarioVerifier.Verify(
            [new PrivateCaseScenarioCount(1, null, 5)],
            Capture((1, 99, 5)));

        var scenario = Assert.Single(result.Scenarios);
        Assert.True(result.Passed);
        Assert.False(scenario.VoucherCountCompared);
        Assert.True(scenario.RowCountMatches);
    }

    [Fact]
    public void Verify_AChangedLegacyResult_ReturnsADeidentifiedFailure()
    {
        var result = PrivateCaseScenarioVerifier.Verify(
            [new PrivateCaseScenarioCount(1, 3, 5)],
            Capture((1, 4, 6)));

        var scenario = Assert.Single(result.Scenarios);
        Assert.False(result.Passed);
        Assert.False(scenario.RowCountMatches);
        Assert.False(scenario.VoucherCountMatches);
    }

    [Fact]
    public void Verify_DiagnosticAlternatives_ShowWhichLegacyCountCanBeExplained()
    {
        var diagnostics = new PrivateCaseScenarioDiagnosticFacts(
        [
            new PrivateCaseScenarioDiagnosticCount(
                Position: 1,
                ExpandedVoucherRowCount: 5,
                ExpandedVoucherCount: 3,
                ExpandedRowsWhoseDirectHitHasNullOrEmptyDescription: 5,
                VouchersWhoseDirectHitHasNullOrEmptyDescription: 3,
                ExpandedRowsWithLegacyTextMatch: 5,
                VouchersWithLegacyTextMatch: 3,
                SameRowRuleMatchRowCount: 4,
                SameRowRuleMatchVoucherCount: 3,
                WeekendIncludingMakeupRowCount: 5,
                WeekendIncludingMakeupVoucherCount: 3),
        ]);

        var result = PrivateCaseScenarioVerifier.Verify(
            [new PrivateCaseScenarioCount(1, 3, 5)],
            Capture((1, 4, 6)),
            diagnostics);

        var scenario = Assert.Single(result.Scenarios);
        Assert.False(scenario.Passed);
        Assert.True(scenario.ExpandedVoucherRowsMatchExpected);
        Assert.True(scenario.DirectHitNullOrEmptyDescriptionVoucherRowsMatchExpected);
        Assert.True(scenario.LegacyTextVoucherRowsMatchExpected);
        Assert.True(scenario.SameRowRuleVoucherCountMatchesExpected);
        Assert.True(scenario.WeekendIncludingMakeupMatchesExpected);
    }

    [Fact]
    public void Verify_ExportedVoucherRowsBasis_UsesTheRowsWrittenToTheReport()
    {
        var diagnostics = new PrivateCaseScenarioDiagnosticFacts(
        [
            new PrivateCaseScenarioDiagnosticCount(
                Position: 1,
                ExpandedVoucherRowCount: 10,
                ExpandedVoucherCount: 5,
                ExpandedRowsWhoseDirectHitHasNullOrEmptyDescription: null,
                VouchersWhoseDirectHitHasNullOrEmptyDescription: null,
                ExpandedRowsWithLegacyTextMatch: null,
                VouchersWithLegacyTextMatch: null,
                SameRowRuleMatchRowCount: null,
                SameRowRuleMatchVoucherCount: null,
                WeekendIncludingMakeupRowCount: null,
                WeekendIncludingMakeupVoucherCount: null),
        ]);

        var result = PrivateCaseScenarioVerifier.Verify(
            [
                new PrivateCaseScenarioCount(
                    1,
                    5,
                    10,
                    PrivateCaseScenarioRowCountBasis.ExportedVoucherRows),
            ],
            Capture((1, 5, 5)),
            diagnostics);

        var scenario = Assert.Single(result.Scenarios);
        Assert.True(result.Passed);
        Assert.True(scenario.RowCountMatches);
        Assert.True(scenario.ExpandedVoucherRowsMatchExpected);
    }

    [Fact]
    public void Verify_ExportedVoucherRowsBasis_RequiresDiagnosticRows()
    {
        Assert.Throws<PrivateCaseScenarioVerificationException>(() =>
            PrivateCaseScenarioVerifier.Verify(
                [
                    new PrivateCaseScenarioCount(
                        1,
                        5,
                        10,
                        PrivateCaseScenarioRowCountBasis.ExportedVoucherRows),
                ],
                Capture((1, 5, 5))));
    }

    [Fact]
    public void Verify_IncompleteDiagnosticPositions_AreRejected()
    {
        var diagnostics = new PrivateCaseScenarioDiagnosticFacts([]);

        Assert.Throws<PrivateCaseScenarioVerificationException>(() =>
            PrivateCaseScenarioVerifier.Verify(
                [new PrivateCaseScenarioCount(1, 3, 5)],
                Capture((1, 3, 5)),
                diagnostics));
    }

    [Fact]
    public void Capture_MissingOrDuplicatePositions_IsRejected()
    {
        using var missing = JsonDocument.Parse(
            """{"scenarios":[{"position":2,"voucherHitCount":1,"rowHitCount":1}]}""");
        using var duplicate = JsonDocument.Parse(
            """{"scenarios":[{"position":1,"voucherHitCount":1,"rowHitCount":1},{"position":1,"voucherHitCount":1,"rowHitCount":1}]}""");

        Assert.Throws<PrivateCaseScenarioVerificationException>(() =>
            PrivateCaseScenarioVerificationFacts.Capture(missing.RootElement));
        Assert.Throws<PrivateCaseScenarioVerificationException>(() =>
            PrivateCaseScenarioVerificationFacts.Capture(duplicate.RootElement));
    }

    private static PrivateCaseScenarioVerificationFacts Capture(
        params (int Position, long VoucherCount, long RowCount)[] counts)
    {
        var json = JsonSerializer.Serialize(new
        {
            scenarios = counts.Select(count => new
            {
                position = count.Position,
                voucherHitCount = count.VoucherCount,
                rowHitCount = count.RowCount,
            }),
        });
        using var document = JsonDocument.Parse(json);
        return PrivateCaseScenarioVerificationFacts.Capture(document.RootElement);
    }
}
