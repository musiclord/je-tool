using System.Text.Json;
using JET.Tests.TestInfrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class PrivateCaseAcceptanceTests
{
    internal const string RootEnvironmentVariable = "JET_PRIVATE_CASE_ROOT";
    internal const string ManifestEnvironmentVariable = "JET_PRIVATE_CASE_MANIFEST";
    internal const string ProviderEnvironmentVariable = "JET_PRIVATE_CASE_PROVIDER";

    [Fact]
    [Trait(TestProfileTraits.Key, "PrivateCase")]
    public async Task Run_AuthorizedCase_CompletesBusinessAndWorkbookAcceptance()
    {
        var root = RequiredEnvironmentVariable(RootEnvironmentVariable);
        var manifest = RequiredEnvironmentVariable(ManifestEnvironmentVariable);
        var provider = ParseProvider(RequiredEnvironmentVariable(ProviderEnvironmentVariable));

        var result = await PrivateCaseAcceptanceRunner.RunAsync(
            root,
            manifest,
            provider);

        Assert.True(result.Passed, BuildSafeFailureSummary(result));

        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(root, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(manifest, json, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSafeFailureSummary(PrivateCaseAcceptanceResult result)
    {
        var summary = "私人案件未通過：legacyScenarios=pass；" + BuildSegmentSummary(
            result.InfVerification,
            result.ScenarioVerification,
            result.ScenarioPopulationComparison,
            result.ReportComparison);
        // 後續操作只列沒通過的段落，格式和第一次相同；比對沒通過不會中斷，三段都會做完。
        var followUps = string.Concat(result.FollowUps
            .Where(static followUp => !followUp.Passed)
            .Select(static followUp => $"｜{followUp.FollowUp}：reportCount={followUp.ReportCount}；"
                + BuildSegmentSummary(
                    followUp.InfVerification,
                    followUp.ScenarioVerification,
                    followUp.ScenarioPopulationComparison,
                    followUp.ReportComparison)));
        return $"{summary}；followUps={result.FollowUps.Count}{followUps}。";
    }

    private static string BuildSegmentSummary(
        PrivateCaseInfVerificationResult infVerification,
        PrivateCaseScenarioVerificationResult scenarioVerification,
        PrivateCaseScenarioPopulationComparisonResult scenarioPopulationComparison,
        PrivateCaseReportComparisonResult reportComparison)
    {
        var inf = infVerification.Passed
            ? "pass"
            : string.Join(
                ",",
                new[]
                {
                    ("population_partition", infVerification.PopulationPartitionMatches),
                    ("excluded_partition", infVerification.ExcludedPartitionMatches),
                    ("sample_rule", infVerification.SampleSizeMatchesRule),
                    ("gl", infVerification.GlPageMatchesRun),
                }
                .Where(static item => !item.Item2)
                .Select(static item => item.Item1));
        var scenarios = scenarioVerification.Passed
            ? "pass"
            : string.Join(
                ",",
                scenarioVerification.Scenarios
                    .Where(static scenario => !scenario.Passed)
                    .Select(static scenario =>
                    {
                        var differences = new List<string>(5);
                        if (!scenario.RowCountMatches)
                        {
                            differences.Add($"rows({scenario.ExpectedRowCount}->{scenario.ActualRowCount})");
                        }
                        if (scenario.VoucherCountCompared && !scenario.VoucherCountMatches)
                        {
                            differences.Add(
                                $"vouchers({scenario.ExpectedVoucherCount}->{scenario.ActualVoucherCount})");
                        }
                        if (scenario.ExpandedVoucherRowCount.HasValue)
                        {
                            differences.Add($"allVoucherRows({scenario.ExpandedVoucherRowCount})");
                        }
                        if (scenario.ExpandedRowsWhoseDirectHitHasNullOrEmptyDescription.HasValue)
                        {
                            differences.Add(
                                $"directHitBlankVoucherRows("
                                + $"{scenario.ExpandedRowsWhoseDirectHitHasNullOrEmptyDescription}/"
                                + $"{scenario.VouchersWhoseDirectHitHasNullOrEmptyDescription})");
                        }
                        if (scenario.ExpandedRowsWithLegacyTextMatch.HasValue)
                        {
                            differences.Add(
                                $"legacyRegexVoucherRows({scenario.ExpandedRowsWithLegacyTextMatch}/"
                                + $"{scenario.VouchersWithLegacyTextMatch})");
                        }
                        if (scenario.SameRowRuleMatchRowCount.HasValue)
                        {
                            differences.Add(
                                $"sameRow({scenario.SameRowRuleMatchRowCount}/"
                                + $"{scenario.SameRowRuleMatchVoucherCount})");
                        }
                        if (scenario.WeekendIncludingMakeupRowCount.HasValue)
                        {
                            differences.Add(
                                $"withMakeup({scenario.WeekendIncludingMakeupRowCount}/"
                                + $"{scenario.WeekendIncludingMakeupVoucherCount})");
                        }
                        return $"{scenario.Position}:{string.Join('+', differences)}";
                    }));
        var populations = scenarioPopulationComparison.Matches
            ? "pass"
            : string.Join(
                ",",
                scenarioPopulationComparison.Scenarios
                    .Where(static scenario => !scenario.Matches)
                    .Select(static scenario =>
                        $"{scenario.Position}:overlap({scenario.OverlapVoucherCount})"
                        + $"+legacyOnly({scenario.LegacyOnlyVoucherCount})"
                        + $"+currentOnly({scenario.CurrentOnlyVoucherCount})"));
        var reports = reportComparison.Passed
            ? "pass"
            : string.Join(
                ",",
                reportComparison.Reports
                    .Where(static report => !report.Passed)
                    .Select(static report =>
                    {
                        var differences = new List<string>(2);
                        if (!report.ContentMatches)
                        {
                            var dimensions = string.Join(
                                "+",
                                report.BlockingContentDifferences.Select(static difference =>
                                    $"{difference.Dimension}:{difference.EntryCount}/{difference.DifferenceCount}"));
                            var accepted = string.Join(
                                "+",
                                report.AcceptedContentDecisions.Select(static decision =>
                                    $"{decision.DecisionId}:{decision.GroupCount}/{decision.DifferenceCount}"));
                            differences.Add(
                                $"content({report.BlockingContentDifferenceCount}/"
                                + $"{report.ContentDifferenceCount}/"
                                + $"{report.ComparedContentDimensionCount};{dimensions};"
                                + string.Join(
                                    ",",
                                    report.BlockingContentDifferenceLocations
                                        .Take(10)
                                        .Select(static difference =>
                                            $"{difference.ScopeKind[0]}{difference.ScopePosition}."
                                            + $"{difference.Dimension}:{difference.DifferenceCount}"))
                                + ";"
                                + string.Join(
                                    ",",
                                    report.ContentFamilyCoverage
                                        .Take(6)
                                        .Select(static family =>
                                            $"f{family.FamilyPosition}[cols="
                                            + $"{family.SharedColumnCount}/"
                                            + $"{family.ValueMatchedColumnCount}/"
                                            + $"{family.UnmatchedExpectedColumnCount}/"
                                            + $"{family.UnmatchedActualColumnCount};rows="
                                            + $"{family.ExpectedRowCount}->{family.ActualRowCount};values="
                                            + $"{family.DifferingSharedColumnCount}/"
                                            + $"{family.EstimatedDifferingValueCount}]"))
                                + ";columns="
                                + string.Join(
                                    ",",
                                    report.BlockingContentColumnDetails
                                        .Take(10)
                                        .Select(static column =>
                                            $"f{column.FamilyPosition}.{column.ColumnRole}["
                                            + $"{column.ExpectedValueCount}->"
                                            + $"{column.ActualValueCount};"
                                            + $"{column.EstimatedDifferingValueCount}"
                                            + (column.ScenarioTagValues is null
                                                ? string.Empty
                                                : $";YNO="
                                                    + $"{column.ScenarioTagValues.ExpectedYesCount}/"
                                                    + $"{column.ScenarioTagValues.ExpectedNoCount}/"
                                                    + $"{column.ScenarioTagValues.ExpectedOtherCount}->"
                                                    + $"{column.ScenarioTagValues.ActualYesCount}/"
                                                    + $"{column.ScenarioTagValues.ActualNoCount}/"
                                                    + $"{column.ScenarioTagValues.ActualOtherCount}")
                                            + "]"))
                                + $";accepted={accepted}"
                                + ")");
                        }
                        if (!report.AppearanceMatches)
                        {
                            var groups = string.Join(
                                "+",
                                report.UnclassifiedAppearanceDifferences
                                    .Take(5)
                                    .Select(static difference =>
                                        $"{difference.ScopeKind}.{difference.Property}:{difference.DifferenceCount}"));
                            var details = string.Join(
                                ",",
                                report.UnclassifiedAppearanceDifferenceDetails
                                    .Take(10)
                                    .Select(static difference =>
                                        $"s{difference.SheetPosition}.{difference.ScopeKind}."
                                        + $"{difference.Property}[{difference.ExpectedValueClass}>"
                                        + $"{difference.ActualValueClass}]:{difference.DifferenceCount}"));
                            var accepted = string.Join(
                                "+",
                                report.AcceptedAppearanceDecisions.Select(static decision =>
                                    $"{decision.DecisionId}:{decision.GroupCount}/{decision.DifferenceCount}"));
                            differences.Add(
                                $"appearance({report.UnclassifiedAppearanceDifferenceCount}/"
                                + $"{report.AppearanceDifferenceCount};{groups};{details};"
                                + $"accepted={accepted})");
                        }
                        return $"{report.Kind}:{string.Join('+', differences)}";
                    }));

        return $"INF={inf}；scenarios={scenarios}；populations={populations}；reports={reports}";
    }

    private static string RequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new PrivateCaseAcceptanceException(PrivateCaseAcceptanceFailure.InvalidInput)
            : value;
    }

    private static LegacyAuditParityProvider ParseProvider(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "sqlite" => LegacyAuditParityProvider.Sqlite,
            _ => throw new PrivateCaseAcceptanceException(
                PrivateCaseAcceptanceFailure.InvalidInput),
        };
}
