using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class PrivateCaseAcceptanceResultTests
{
    // 篩選情境命中的傳票集合要和舊版條件篩選報告列出的傳票相同；不同時第一次比對與每一段後續操作都不算通過。
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Passed_RequiresTheSelectedVoucherSetsToMatchTheLegacyReport(
        bool populationsMatch,
        bool passed)
    {
        var population = Population(populationsMatch);
        var followUps = PrivateCaseAcceptanceRunner.FollowUps
            .Select(static followUp => new PrivateCaseAcceptanceFollowUpResult(
                followUp,
                ReportCount,
                Inf(),
                Scenarios(),
                Population(matches: true),
                Reports()))
            .ToArray();

        var result = new PrivateCaseAcceptanceResult(
            "synthetic-case",
            LegacyAuditParityProvider.Sqlite,
            ReportCount,
            PrivateCaseAcceptancePolicy.Current,
            new PrivateCaseLegacyScenarioEvidenceResult(1),
            Inf(),
            Scenarios(),
            population,
            Reports(),
            followUps);
        var followUpResult = new PrivateCaseAcceptanceFollowUpResult(
            LegacyAuditParityFollowUp.Reopen,
            ReportCount,
            Inf(),
            Scenarios(),
            population,
            Reports());

        Assert.Equal(passed, result.Passed);
        Assert.Equal(passed, followUpResult.Passed);
    }

    private static int ReportCount => Enum.GetValues<LegacyReportKind>().Length;

    private static PrivateCaseInfVerificationResult Inf() => new(
        new PrivateCaseInfVerificationFacts(10, 10, 0, 0, 0, 10, 10),
        PrivateCaseAcceptancePolicy.RequiredInfSampleSize);

    private static PrivateCaseScenarioVerificationResult Scenarios() => new([]);

    private static PrivateCaseReportComparisonResult Reports() => new([]);

    private static PrivateCaseScenarioPopulationComparisonResult Population(bool matches) => new(
    [
        new PrivateCaseScenarioPopulationComparisonSummary(
            Position: 1,
            LegacyVoucherCount: 2,
            CurrentVoucherCount: 2,
            OverlapVoucherCount: matches ? 2 : 1,
            LegacyOnlyVoucherCount: matches ? 0 : 1,
            CurrentOnlyVoucherCount: matches ? 0 : 1),
    ]);
}
