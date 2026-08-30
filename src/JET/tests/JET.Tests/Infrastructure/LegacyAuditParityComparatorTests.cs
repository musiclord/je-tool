using System.Text.Json;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class LegacyAuditParityComparatorTests
{
    private const string RawInfSentinel = "SYNTHETIC-RAW-INF-SENTINEL";
    private const string RawInfAlternate = "SYNTHETIC-RAW-INF-ALTERNATE";

    private static readonly LegacyAuditParityMappingProvenance NoMappingProvenance = new([]);
    private static readonly IReadOnlyList<LegacyAuditParityMappingProvenanceEntry> NoMappingDependencies =
        [];

    [Fact]
    public void CompareLegacyToProvider_CoversEveryDimensionWithoutLeakingRawMembers()
    {
        var legacy = LegacyAuditParityObservation.FromLegacy(
            LegacyParityCase.CaseA,
            Metrics(
                variant: false,
                creatorSummaryLegacyApplicability: LegacyMetricApplicability.NotApplicable,
                rareAccountsLegacyApplicability: LegacyMetricApplicability.NotApplicable));
        var provider = LegacyAuditParityObservation.FromProvider(
            LegacyParityCase.CaseA,
            LegacyAuditParityProvider.Sqlite,
            Metrics(variant: true));
        var classifications = new LegacyAuditParityClassificationCatalog(
            ExpectedMismatchMetricIds().Select(metricId => ClassificationForLegacyMetric(
                metricId,
                LegacyAuditParityDifferenceCategory.IntentionalDecision,
                LegacyAuditParityCitation.JetGuideSection4,
                LegacyAuditParityBasis.ApprovedIntentionalDifference)));

        var registry = LegacyAuditParityComparator.CompareLegacyToProvider(
            legacy,
            provider,
            classifications);
        var serialized = registry.Serialize();
        var summary = registry.FormatSummary();

        Assert.Equal(ExpectedMismatchMetricIds().Count, registry.Entries.Count);
        Assert.All(
            registry.Entries,
            entry => Assert.Equal(LegacyAuditParityComparisonId.LegacyVsSqlite, entry.ComparisonId));
        Assert.Contains(
            registry.Entries,
            entry => entry.MetricId == LegacyAuditParityMetricIds.InfMemberKeyMultiset);
        Assert.Equal(
            ExpectedMismatchMetricIds().Count(metricId =>
                metricId.StartsWith("prescreen.", StringComparison.Ordinal)),
            registry.Entries.Count(entry => entry.MetricId.StartsWith("prescreen.", StringComparison.Ordinal)));
        Assert.Equal(
            Enum.GetValues<LegacyReportKind>().Length,
            registry.Entries.Count(entry => entry.MetricId.StartsWith("report.", StringComparison.Ordinal)));
        AssertNoRawSentinels(serialized);
        AssertNoRawSentinels(summary);
        AssertNoRawSentinels(string.Join(Environment.NewLine, registry.Entries));

        using var document = JsonDocument.Parse(serialized);
        Assert.Equal(
            "legacy-audit-parity-difference-registry/v4",
            document.RootElement.GetProperty("schemaVersion").GetString());
        var entries = document.RootElement.GetProperty("differences");
        Assert.Equal(registry.Entries.Count, entries.GetArrayLength());
        Assert.All(entries.EnumerateArray(), entry => Assert.Equal(9, entry.EnumerateObject().Count()));
        Assert.All(
            registry.Entries.Where(entry =>
                entry.Category == LegacyAuditParityDifferenceCategory.LegacyNoCounterpart),
            entry => Assert.Null(entry.DifferenceCount));
    }

    [Fact]
    public void DifferenceRegistryValidator_RejectsFreeIdentityUnclassifiedAndMissingCitation()
    {
        var freeIdentity = Assert.Throws<ArgumentException>(() =>
            LegacyAuditParityDifferenceValidator.ValidateDifference(
                LegacyParityCase.CaseA,
                LegacyAuditParityComparisonId.LegacyVsSqlite,
                $"filter.{RawInfSentinel}",
                1,
                LegacyAuditParityDifferenceCategory.JetDefect,
                LegacyAuditParityCitation.JetGuideSection4,
                LegacyAuditParityBasis.JetRequirementMismatch,
                NoMappingProvenance,
                LegacyAuditParityHarnessBindingVerification.NotRequired));
        var unclassified = Assert.Throws<ArgumentException>(() =>
            LegacyAuditParityDifferenceValidator.ValidateDifference(
                LegacyParityCase.CaseA,
                LegacyAuditParityComparisonId.LegacyVsSqlite,
                LegacyAuditParityMetricIds.InfSampleSize,
                1,
                LegacyAuditParityDifferenceCategory.Unclassified,
                LegacyAuditParityCitation.JetGuideSection4,
                LegacyAuditParityBasis.None,
                NoMappingProvenance,
                LegacyAuditParityHarnessBindingVerification.NotRequired));
        var noCitation = Assert.Throws<ArgumentException>(() =>
            LegacyAuditParityDifferenceValidator.ValidateDifference(
                LegacyParityCase.CaseA,
                LegacyAuditParityComparisonId.LegacyVsSqlite,
                LegacyAuditParityMetricIds.InfSampleSize,
                1,
                LegacyAuditParityDifferenceCategory.JetDefect,
                LegacyAuditParityCitation.None,
                LegacyAuditParityBasis.JetRequirementMismatch,
                NoMappingProvenance,
                LegacyAuditParityHarnessBindingVerification.NotRequired));

        Assert.DoesNotContain(RawInfSentinel, freeIdentity.Message, StringComparison.Ordinal);
        Assert.Contains("unclassified", unclassified.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("citation", noCitation.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompareLegacyToProvider_MissingClassificationFailsClosedWithoutRawObservationValues()
    {
        var legacy = LegacyAuditParityObservation.FromLegacy(
            LegacyParityCase.CaseA,
            Metrics(
                variant: false,
                creatorSummaryLegacyApplicability: LegacyMetricApplicability.NotApplicable,
                rareAccountsLegacyApplicability: LegacyMetricApplicability.NotApplicable));
        var provider = LegacyAuditParityObservation.FromProvider(
            LegacyParityCase.CaseA,
            LegacyAuditParityProvider.DuckDb,
            Metrics(variant: true));

        var error = Assert.Throws<InvalidOperationException>(() =>
            LegacyAuditParityComparator.CompareLegacyToProvider(
                legacy,
                provider,
                new LegacyAuditParityClassificationCatalog([])));

        Assert.Contains("unclassified", error.Message, StringComparison.OrdinalIgnoreCase);
        AssertNoRawSentinels(error.Message);
    }

    [Fact]
    public void CompareProviders_PreservesEachProviderPairAndForcesJetDefectWithoutLeakingInfMembers()
    {
        var sqlite = LegacyAuditParityObservation.FromProvider(
            LegacyParityCase.CaseB,
            LegacyAuditParityProvider.Sqlite,
            Metrics(variant: false));
        var duckDb = LegacyAuditParityObservation.FromProvider(
            LegacyParityCase.CaseB,
            LegacyAuditParityProvider.DuckDb,
            Metrics(variant: true));
        var sqlServer = LegacyAuditParityObservation.FromProvider(
            LegacyParityCase.CaseB,
            LegacyAuditParityProvider.SqlServer,
            Metrics(variant: true));

        var registry = LegacyAuditParityComparator.CompareProviders([sqlite, duckDb, sqlServer]);

        Assert.NotEmpty(registry.Entries);
        Assert.Contains(
            registry.Entries,
            entry => entry.ComparisonId == LegacyAuditParityComparisonId.SqliteVsDuckDb);
        Assert.Contains(
            registry.Entries,
            entry => entry.ComparisonId == LegacyAuditParityComparisonId.SqliteVsSqlServer);
        Assert.DoesNotContain(
            registry.Entries,
            entry => entry.ComparisonId == LegacyAuditParityComparisonId.DuckDbVsSqlServer);
        Assert.All(registry.Entries, entry =>
        {
            Assert.Equal(LegacyAuditParityDifferenceCategory.JetDefect, entry.Category);
            Assert.Equal(LegacyAuditParityCitation.ParityPlanProviderConsistency, entry.Citation);
            Assert.Equal(LegacyAuditParityBasis.ProviderInconsistency, entry.Basis);
            Assert.True(entry.IsProviderMismatch);
        });
        AssertNoRawSentinels(registry.Serialize());
        AssertNoRawSentinels(registry.FormatSummary());
    }

    [Fact]
    public void ClassificationValidator_RejectsBasisThatDoesNotMatchCategory()
    {
        var error = Assert.Throws<ArgumentException>(() => new LegacyAuditParityClassification(
            LegacyParityCase.CaseA,
            LegacyAuditParityMetricIds.InfSampleSize,
            LegacyAuditParityDifferenceCategory.IntentionalDecision,
            LegacyAuditParityCitation.JetGuideSection4,
            LegacyAuditParityBasis.JetRequirementMismatch,
            NoMappingDependencies,
            LegacyAuditParityHarnessBindingVerification.NotRequired));

        Assert.Contains("basis", error.Message, StringComparison.OrdinalIgnoreCase);
        AssertNoRawSentinels(error.Message);
    }

    [Fact]
    public void ClassificationValidator_FallbackDependencyMustBeVerifiedBeforeJetDefect()
    {
        var fallback = new LegacyAuditParityMappingProvenanceEntry(
            LegacyMappingDataset.Gl,
            LegacyMappingSlot.GlApprovalDate,
            LegacyMappingResolutionSource.NormalizedHeaderIdentity);

        var unverified = Assert.Throws<ArgumentException>(() => new LegacyAuditParityClassification(
            LegacyParityCase.CaseA,
            LegacyAuditParityMetricIds.PrescreenRowCount(LegacyPrescreenRuleId.PostPeriodApproval),
            LegacyAuditParityDifferenceCategory.JetDefect,
            LegacyAuditParityCitation.JetGuideSection5,
            LegacyAuditParityBasis.JetRequirementMismatch,
            [fallback],
            LegacyAuditParityHarnessBindingVerification.Unverified));
        var missingState = Assert.Throws<ArgumentException>(() => new LegacyAuditParityClassification(
            LegacyParityCase.CaseA,
            LegacyAuditParityMetricIds.PrescreenRowCount(LegacyPrescreenRuleId.PostPeriodApproval),
            LegacyAuditParityDifferenceCategory.IntentionalDecision,
            LegacyAuditParityCitation.JetGuideSection5,
            LegacyAuditParityBasis.ApprovedIntentionalDifference,
            [fallback],
            LegacyAuditParityHarnessBindingVerification.NotRequired));
        var verified = new LegacyAuditParityClassification(
            LegacyParityCase.CaseA,
            LegacyAuditParityMetricIds.PrescreenRowCount(LegacyPrescreenRuleId.PostPeriodApproval),
            LegacyAuditParityDifferenceCategory.JetDefect,
            LegacyAuditParityCitation.JetGuideSection5,
            LegacyAuditParityBasis.JetRequirementMismatch,
            [fallback],
            LegacyAuditParityHarnessBindingVerification.Verified);
        var nonJetUnverified = new LegacyAuditParityClassification(
            LegacyParityCase.CaseA,
            LegacyAuditParityMetricIds.PrescreenVoucherCount(LegacyPrescreenRuleId.PostPeriodApproval),
            LegacyAuditParityDifferenceCategory.IntentionalDecision,
            LegacyAuditParityCitation.JetGuideSection5,
            LegacyAuditParityBasis.ApprovedIntentionalDifference,
            [fallback],
            LegacyAuditParityHarnessBindingVerification.Unverified);

        Assert.Contains("verified", unverified.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("verification", missingState.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LegacyAuditParityHarnessBindingVerification.Verified,
            verified.HarnessBindingVerification);
        Assert.Single(verified.MappingDependencies);
        Assert.Equal(fallback, verified.MappingDependencies[0]);
        Assert.Equal(LegacyAuditParityHarnessBindingVerification.Unverified,
            nonJetUnverified.HarnessBindingVerification);
    }

    [Fact]
    public void DifferenceRegistry_SerializesOnlyTypedMappingProvenanceAndVerification()
    {
        var dependencies = new[]
        {
            new LegacyAuditParityMappingProvenanceEntry(
                LegacyMappingDataset.Tb,
                LegacyMappingSlot.TbAccountNumber,
                LegacyMappingResolutionSource.WorkingPaperExplicitMapping),
            new LegacyAuditParityMappingProvenanceEntry(
                LegacyMappingDataset.Gl,
                LegacyMappingSlot.GlApprovalDate,
                LegacyMappingResolutionSource.BipartiteUniqueSolution),
        };
        var classification = new LegacyAuditParityClassification(
            LegacyParityCase.CaseA,
            LegacyAuditParityMetricIds.CompletenessDifferenceAccountCount,
            LegacyAuditParityDifferenceCategory.JetDefect,
            LegacyAuditParityCitation.JetGuideSection4,
            LegacyAuditParityBasis.JetRequirementMismatch,
            dependencies,
            LegacyAuditParityHarnessBindingVerification.Verified);
        var difference = LegacyAuditParityDifference.Classified(
            classification,
            LegacyAuditParityComparisonId.LegacyVsSqlite,
            differenceCount: 1);
        var registry = new LegacyAuditParityDifferenceRegistry([difference]);

        Assert.Equal(LegacyAuditParityHarnessBindingVerification.Verified,
            difference.HarnessBindingVerification);
        Assert.Equal(2, difference.MappingDependencies.Count);

        using var document = JsonDocument.Parse(registry.Serialize());
        var jsonDifference = document.RootElement.GetProperty("differences")[0];
        Assert.Equal("Verified", jsonDifference.GetProperty("harnessBindingVerification").GetString());
        var jsonDependencies = jsonDifference.GetProperty("mappingDependencies");
        Assert.Equal(2, jsonDependencies.GetArrayLength());
        Assert.All(jsonDependencies.EnumerateArray(), dependency =>
            Assert.Equal(3, dependency.EnumerateObject().Count()));
        Assert.Equal("Gl", jsonDependencies[0].GetProperty("dataset").GetString());
        Assert.Equal("GlApprovalDate", jsonDependencies[0].GetProperty("slot").GetString());
        Assert.Equal("BipartiteUniqueSolution",
            jsonDependencies[0].GetProperty("resolutionSource").GetString());
        Assert.Equal("Tb", jsonDependencies[1].GetProperty("dataset").GetString());
        Assert.Equal("TbAccountNumber", jsonDependencies[1].GetProperty("slot").GetString());
        Assert.Equal("WorkingPaperExplicitMapping",
            jsonDependencies[1].GetProperty("resolutionSource").GetString());
    }

    [Fact]
    public void DifferenceRegistry_AllFiveCategoriesRecordOnlyTheirLegalBasesAndRejectUnclassified()
    {
        var valid = new Dictionary<LegacyAuditParityDifferenceCategory, LegacyAuditParityBasis[]>
        {
            [LegacyAuditParityDifferenceCategory.JetDefect] =
            [
                LegacyAuditParityBasis.JetRequirementMismatch,
                LegacyAuditParityBasis.ProviderInconsistency,
            ],
            [LegacyAuditParityDifferenceCategory.IntentionalDecision] =
                [LegacyAuditParityBasis.ApprovedIntentionalDifference],
            [LegacyAuditParityDifferenceCategory.LegacyDefectOrInputCondition] =
            [
                LegacyAuditParityBasis.LegacyBehaviorDefect,
                LegacyAuditParityBasis.InputProfileCondition,
            ],
            [LegacyAuditParityDifferenceCategory.LegacyNoCounterpart] =
                [LegacyAuditParityBasis.LegacyNoComparableItem],
            [LegacyAuditParityDifferenceCategory.UserDecisionRequired] =
                [LegacyAuditParityBasis.RepositoryEvidenceInsufficient],
        };
        var categories = Enum.GetValues<LegacyAuditParityDifferenceCategory>()
            .Where(category => category != LegacyAuditParityDifferenceCategory.Unclassified)
            .ToArray();
        Assert.Equal(5, categories.Length);
        Assert.Equal(categories.Order(), valid.Keys.Order());

        var classifications = categories.Select((category, index) =>
            new LegacyAuditParityClassification(
                LegacyParityCase.CaseA,
                CategoryMetricIds[index],
                category,
                CitationFor(category, valid[category][0]),
                valid[category][0],
                NoMappingDependencies,
                LegacyAuditParityHarnessBindingVerification.NotRequired)).ToArray();
        var registry = new LegacyAuditParityDifferenceRegistry(
            classifications.Select(classification =>
                classification.Category == LegacyAuditParityDifferenceCategory.LegacyNoCounterpart
                    ? LegacyAuditParityDifference.NoLegacyCounterpart(
                        classification,
                        LegacyAuditParityComparisonId.LegacyVsSqlite)
                    : LegacyAuditParityDifference.Classified(
                        classification,
                        LegacyAuditParityComparisonId.LegacyVsSqlite,
                        differenceCount: 1)));

        Assert.Equal(5, registry.Entries.Count);
        Assert.Equal(categories.Order(), registry.Entries.Select(entry => entry.Category).Order());
        Assert.All(registry.Entries, entry => Assert.Contains(entry.Basis, valid[entry.Category]));

        foreach (var category in categories)
        {
            foreach (var basis in Enum.GetValues<LegacyAuditParityBasis>()
                         .Where(basis => basis != LegacyAuditParityBasis.None))
            {
                var create = () => new LegacyAuditParityClassification(
                    LegacyParityCase.CaseA,
                    LegacyAuditParityMetricIds.InfSampleSize,
                    category,
                    CitationFor(category, basis),
                    basis,
                    NoMappingDependencies,
                    LegacyAuditParityHarnessBindingVerification.NotRequired);
                if (valid[category].Contains(basis))
                {
                    _ = create();
                }
                else
                {
                    Assert.Throws<ArgumentException>(create);
                }
            }
        }

        Assert.Throws<ArgumentException>(() => new LegacyAuditParityClassification(
            LegacyParityCase.CaseA,
            LegacyAuditParityMetricIds.InfSampleSize,
            LegacyAuditParityDifferenceCategory.Unclassified,
            LegacyAuditParityCitation.ParityPlanClassificationRules,
            LegacyAuditParityBasis.None,
            NoMappingDependencies,
            LegacyAuditParityHarnessBindingVerification.NotRequired));
    }

    [Fact]
    public void LegacyNoCounterpartRules_AreTheIndependentExactSixRuleSet()
    {
        var expected = new HashSet<LegacyPrescreenRuleId>
        {
            LegacyPrescreenRuleId.HolidayPosting,
            LegacyPrescreenRuleId.HolidayApproval,
            LegacyPrescreenRuleId.BackdatedPosting,
            LegacyPrescreenRuleId.NonAuthorizedPreparer,
            LegacyPrescreenRuleId.LowFrequencyPreparer,
            LegacyPrescreenRuleId.LowFrequencyAccount,
        };

        Assert.Equal(6, LegacyPrescreenParityPlan.LegacyNoCounterpartRules.Count);
        Assert.Equal(
            6,
            LegacyPrescreenParityPlan.LegacyNoCounterpartRules.Distinct().Count());
        Assert.True(
            expected.SetEquals(LegacyPrescreenParityPlan.LegacyNoCounterpartRules),
            "Legacy-no-counterpart rules must remain the exact repository-defined six-rule set.");
    }

    [Fact]
    public void LegacyDirectRules_AreR1ThroughR4AndBlankDescription()
    {
        var expected = new HashSet<LegacyPrescreenRuleId>
        {
            LegacyPrescreenRuleId.PostPeriodApproval,
            LegacyPrescreenRuleId.SuspiciousKeywords,
            LegacyPrescreenRuleId.UnexpectedAccountPair,
            LegacyPrescreenRuleId.TrailingZeros,
            LegacyPrescreenRuleId.BlankDescription,
        };

        Assert.Equal(5, LegacyPrescreenParityPlan.DirectRules.Count);
        Assert.True(
            expected.SetEquals(LegacyPrescreenParityPlan.DirectRules),
            "Legacy direct prescreen rules must remain the exact repository-defined set.");
    }

    [Fact]
    public void LegacyNoCounterpartRules_EachRegisterBothRowAndVoucherMetrics()
    {
        var expectedRules = new[]
        {
            LegacyPrescreenRuleId.HolidayPosting,
            LegacyPrescreenRuleId.HolidayApproval,
            LegacyPrescreenRuleId.BackdatedPosting,
            LegacyPrescreenRuleId.NonAuthorizedPreparer,
            LegacyPrescreenRuleId.LowFrequencyPreparer,
            LegacyPrescreenRuleId.LowFrequencyAccount,
        };
        var metricIds = LegacyPrescreenParityPlan.LegacyNoCounterpartMetricIds.ToArray();

        foreach (var rule in expectedRules)
        {
            Assert.Contains(LegacyAuditParityMetricIds.PrescreenRowCount(rule), metricIds);
            Assert.Contains(LegacyAuditParityMetricIds.PrescreenVoucherCount(rule), metricIds);
        }
        Assert.Contains(LegacyAuditParityMetricIds.WeekendUnionRowCount, metricIds);
        Assert.Contains(LegacyAuditParityMetricIds.WeekendUnionVoucherCount, metricIds);
        Assert.Equal(
            LegacyPrescreenParityPlan.LegacyNoCounterpartControlMetricIds.Count
                + expectedRules.Length * 2
                + LegacyPrescreenParityPlan.LegacyNoCounterpartWeekendMetricIds.Count,
            metricIds.Length);
        Assert.Equal(metricIds.Length, metricIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DifferenceRegistry_UserDecisionRequiredBlocksStageCompletionWithoutRawValues()
    {
        var legacy = LegacyAuditParityObservation.FromLegacy(
            LegacyParityCase.CaseA,
            Metrics(
                variant: false,
                creatorSummaryLegacyApplicability: LegacyMetricApplicability.NotApplicable,
                rareAccountsLegacyApplicability: LegacyMetricApplicability.NotApplicable));
        var provider = LegacyAuditParityObservation.FromProvider(
            LegacyParityCase.CaseA,
            LegacyAuditParityProvider.DuckDb,
            Metrics(variant: true));
        var classifications = new LegacyAuditParityClassificationCatalog(
            ExpectedMismatchMetricIds().Select(metricId => metricId == LegacyAuditParityMetricIds.InfSampleSize
                ? ClassificationForLegacyMetric(
                    metricId,
                    LegacyAuditParityDifferenceCategory.UserDecisionRequired,
                    LegacyAuditParityCitation.ParityPlanClassificationRules,
                    LegacyAuditParityBasis.RepositoryEvidenceInsufficient)
                : ClassificationForLegacyMetric(
                    metricId,
                    LegacyAuditParityDifferenceCategory.IntentionalDecision,
                    LegacyAuditParityCitation.JetGuideSection4,
                    LegacyAuditParityBasis.ApprovedIntentionalDifference)));
        var registry = LegacyAuditParityComparator.CompareLegacyToProvider(
            legacy,
            provider,
            classifications);

        var error = Assert.Throws<LegacyAuditParityUserDecisionRequiredException>(
            registry.EnsureStageCanComplete);

        Assert.Contains("blocked", error.Message, StringComparison.OrdinalIgnoreCase);
        AssertNoRawSentinels(error.Message);
        AssertNoRawSentinels(registry.Serialize());
    }

    [Fact]
    public void CompareLegacyToProvider_UnavailablePartAIsTypedNonComparableAndNeverZero()
    {
        var legacy = LegacyAuditParityObservation.FromLegacy(
            LegacyParityCase.CaseA,
            Metrics(
                variant: false,
                unavailablePartA: true,
                creatorSummaryLegacyApplicability: LegacyMetricApplicability.NotApplicable,
                rareAccountsLegacyApplicability: LegacyMetricApplicability.NotApplicable));
        var provider = LegacyAuditParityObservation.FromProvider(
            LegacyParityCase.CaseA,
            LegacyAuditParityProvider.Sqlite,
            Metrics(variant: false));

        var unavailableMetricIds = new[]
        {
            LegacyAuditParityMetricIds.PartATargetRowCount,
            LegacyAuditParityMetricIds.PartATotalDebit,
            LegacyAuditParityMetricIds.PartATotalCredit,
        };
        var classifications = unavailableMetricIds.Select(metricId =>
                ClassificationForLegacyMetric(
                    metricId,
                    LegacyAuditParityDifferenceCategory.LegacyDefectOrInputCondition,
                    LegacyAuditParityCitation.ParityPlanClassificationRules,
                    LegacyAuditParityBasis.InputProfileCondition))
            .Concat(LegacyPrescreenParityPlan.LegacyNoCounterpartMetricIds.Select(metricId =>
                ClassificationForLegacyMetric(
                    metricId,
                    LegacyAuditParityDifferenceCategory.LegacyNoCounterpart,
                    LegacyAuditParityCitation.ParityPlanClassificationRules,
                    LegacyAuditParityBasis.LegacyNoComparableItem)));

        var registry = LegacyAuditParityComparator.CompareLegacyToProvider(
            legacy,
            provider,
            new LegacyAuditParityClassificationCatalog(classifications));

        Assert.All(
            registry.Entries.Where(entry => unavailableMetricIds.Contains(entry.MetricId)),
            entry =>
            {
                Assert.Null(entry.DifferenceCount);
                Assert.Equal(
                    LegacyAuditParityDifferenceCategory.LegacyDefectOrInputCondition,
                    entry.Category);
            });
        Assert.Equal(unavailableMetricIds.Length,
            registry.Entries.Count(entry => unavailableMetricIds.Contains(entry.MetricId)));
        Assert.DoesNotContain(registry.Entries, entry => entry.DifferenceCount == 0);
    }

    [Fact]
    public void CompareLegacyToProvider_BothUnavailablePrescreenCountsAreNotRecordedAsMatches()
    {
        var legacy = LegacyAuditParityObservation.FromLegacy(
            LegacyParityCase.CaseB,
            Metrics(
                variant: false,
                unavailablePrescreen: true,
                creatorSummaryLegacyApplicability: LegacyMetricApplicability.NotApplicable,
                rareAccountsLegacyApplicability: LegacyMetricApplicability.NotApplicable));
        var provider = LegacyAuditParityObservation.FromProvider(
            LegacyParityCase.CaseB,
            LegacyAuditParityProvider.DuckDb,
            Metrics(variant: false, unavailablePrescreen: true));

        var unavailableMetricIds = LegacyPrescreenParityPlan.DirectRules.SelectMany(rule => new[]
            {
                LegacyAuditParityMetricIds.PrescreenRowCount(rule),
                LegacyAuditParityMetricIds.PrescreenVoucherCount(rule),
            })
            .ToArray();
        var classifications = unavailableMetricIds.Select(metricId => new LegacyAuditParityClassification(
                LegacyParityCase.CaseB,
                metricId,
                LegacyAuditParityDifferenceCategory.LegacyDefectOrInputCondition,
                LegacyAuditParityCitation.ParityPlanClassificationRules,
                LegacyAuditParityBasis.InputProfileCondition,
                NoMappingDependencies,
                LegacyAuditParityHarnessBindingVerification.NotRequired))
            .Concat(LegacyPrescreenParityPlan.LegacyNoCounterpartMetricIds.Select(metricId =>
                new LegacyAuditParityClassification(
                    LegacyParityCase.CaseB,
                    metricId,
                    LegacyAuditParityDifferenceCategory.LegacyNoCounterpart,
                    LegacyAuditParityCitation.ParityPlanClassificationRules,
                    LegacyAuditParityBasis.LegacyNoComparableItem,
                    NoMappingDependencies,
                    LegacyAuditParityHarnessBindingVerification.NotRequired)));

        var registry = LegacyAuditParityComparator.CompareLegacyToProvider(
            legacy,
            provider,
            new LegacyAuditParityClassificationCatalog(classifications));

        Assert.Equal(unavailableMetricIds.Length,
            registry.Entries.Count(entry => unavailableMetricIds.Contains(entry.MetricId)));
        Assert.All(
            registry.Entries.Where(entry => unavailableMetricIds.Contains(entry.MetricId)),
            entry => Assert.Null(entry.DifferenceCount));
        Assert.DoesNotContain(registry.Entries, entry => entry.DifferenceCount == 0);
    }

    [Fact]
    public void CompareLegacyToProvider_R5R6AcceptOnlyFixedLegacyNaAndProviderNotExecuted()
    {
        var legacy = LegacyAuditParityObservation.FromLegacy(
            LegacyParityCase.CaseA,
            Metrics(
                variant: false,
                creatorSummaryLegacyApplicability: LegacyMetricApplicability.NotApplicable,
                rareAccountsLegacyApplicability: LegacyMetricApplicability.NotApplicable));
        var provider = LegacyAuditParityObservation.FromProvider(
            LegacyParityCase.CaseA,
            LegacyAuditParityProvider.Sqlite,
            Metrics(variant: false));

        var registry = LegacyAuditParityComparator.CompareLegacyToProvider(
            legacy,
            provider,
            NoCounterpartClassifications(LegacyParityCase.CaseA));

        Assert.Equal(
            LegacyPrescreenParityPlan.LegacyNoCounterpartMetricIds.Count(),
            registry.Entries.Count);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void CompareLegacyToProvider_R5R6ApplicabilityMismatchFailsClosed(
        bool creatorSummary,
        bool invalidLegacySide)
    {
        var legacy = LegacyAuditParityObservation.FromLegacy(
            LegacyParityCase.CaseA,
            Metrics(
                variant: false,
                creatorSummaryLegacyApplicability: creatorSummary && invalidLegacySide
                    ? LegacyMetricApplicability.NotExecuted
                    : LegacyMetricApplicability.NotApplicable,
                rareAccountsLegacyApplicability: !creatorSummary && invalidLegacySide
                    ? LegacyMetricApplicability.NotExecuted
                    : LegacyMetricApplicability.NotApplicable));
        var provider = LegacyAuditParityObservation.FromProvider(
            LegacyParityCase.CaseA,
            LegacyAuditParityProvider.Sqlite,
            Metrics(
                variant: false,
                creatorSummaryLegacyApplicability: creatorSummary && !invalidLegacySide
                    ? LegacyMetricApplicability.NotApplicable
                    : LegacyMetricApplicability.NotExecuted,
                rareAccountsLegacyApplicability: !creatorSummary && !invalidLegacySide
                    ? LegacyMetricApplicability.NotApplicable
                    : LegacyMetricApplicability.NotExecuted));

        var error = Assert.Throws<LegacyAuditParityComparisonCompletenessException>(() =>
            LegacyAuditParityComparator.CompareLegacyToProvider(
                legacy,
                provider,
                new LegacyAuditParityClassificationCatalog([])));

        Assert.Equal(
            creatorSummary
                ? LegacyAuditParityMetricIds.CreatorSummaryNotApplicable
                : LegacyAuditParityMetricIds.RareAccountsNotApplicable,
            error.MetricId);
    }

    [Fact]
    public void DifferenceValidator_NullDifferenceIsRestrictedToTypedNonComparableCategories()
    {
        LegacyAuditParityDifferenceValidator.ValidateDifference(
            LegacyParityCase.CaseA,
            LegacyAuditParityComparisonId.LegacyVsSqlite,
            LegacyAuditParityMetricIds.InfSampleSize,
            null,
            LegacyAuditParityDifferenceCategory.LegacyDefectOrInputCondition,
            LegacyAuditParityCitation.ParityPlanClassificationRules,
            LegacyAuditParityBasis.InputProfileCondition,
            NoMappingProvenance,
            LegacyAuditParityHarnessBindingVerification.NotRequired);
        LegacyAuditParityDifferenceValidator.ValidateDifference(
            LegacyParityCase.CaseA,
            LegacyAuditParityComparisonId.LegacyVsSqlite,
            LegacyAuditParityMetricIds.InfSampleSize,
            null,
            LegacyAuditParityDifferenceCategory.UserDecisionRequired,
            LegacyAuditParityCitation.ParityPlanClassificationRules,
            LegacyAuditParityBasis.RepositoryEvidenceInsufficient,
            NoMappingProvenance,
            LegacyAuditParityHarnessBindingVerification.NotRequired);

        Assert.Throws<ArgumentException>(() => LegacyAuditParityDifferenceValidator.ValidateDifference(
            LegacyParityCase.CaseA,
            LegacyAuditParityComparisonId.LegacyVsSqlite,
            LegacyAuditParityMetricIds.InfSampleSize,
            null,
            LegacyAuditParityDifferenceCategory.JetDefect,
            LegacyAuditParityCitation.JetGuideSection4,
            LegacyAuditParityBasis.JetRequirementMismatch,
            NoMappingProvenance,
            LegacyAuditParityHarnessBindingVerification.NotRequired));
        Assert.Throws<ArgumentException>(() => LegacyAuditParityDifferenceValidator.ValidateDifference(
            LegacyParityCase.CaseA,
            LegacyAuditParityComparisonId.LegacyVsSqlite,
            LegacyAuditParityMetricIds.InfSampleSize,
            null,
            LegacyAuditParityDifferenceCategory.IntentionalDecision,
            LegacyAuditParityCitation.JetGuideSection4,
            LegacyAuditParityBasis.ApprovedIntentionalDifference,
            NoMappingProvenance,
            LegacyAuditParityHarnessBindingVerification.NotRequired));
    }

    [Fact]
    public void CompareProviders_AnyUnavailableMetricFailsClosedEvenWhenBothSidesAreMissing()
    {
        var sqlite = LegacyAuditParityObservation.FromProvider(
            LegacyParityCase.CaseA,
            LegacyAuditParityProvider.Sqlite,
            Metrics(variant: false, unavailablePartA: true, unavailablePrescreen: true));
        var duckDbSharedUnavailable = LegacyAuditParityObservation.FromProvider(
            LegacyParityCase.CaseA,
            LegacyAuditParityProvider.DuckDb,
            Metrics(variant: false, unavailablePartA: true, unavailablePrescreen: true));
        var sharedError = Assert.Throws<LegacyAuditParityComparisonCompletenessException>(() =>
            LegacyAuditParityComparator.CompareProviders([sqlite, duckDbSharedUnavailable]));
        Assert.Equal(LegacyAuditParityMetricIds.PartASourceRowCount, sharedError.MetricId);

        var duckDbAvailable = LegacyAuditParityObservation.FromProvider(
            LegacyParityCase.CaseA,
            LegacyAuditParityProvider.DuckDb,
            Metrics(variant: false));
        var mismatchError = Assert.Throws<LegacyAuditParityComparisonCompletenessException>(() =>
            LegacyAuditParityComparator.CompareProviders([sqlite, duckDbAvailable]));
        Assert.Equal(LegacyAuditParityMetricIds.PartASourceRowCount, mismatchError.MetricId);
    }

    [Fact]
    public void CompareLegacyToProvider_ExecutedUnavailableVoucherKeepsExactRowComparisonIndependent()
    {
        var scenario = LegacyFilterScenarioId.FromOrdinal(1);
        var rowMetricId = LegacyAuditParityMetricIds.FilterRowCount(scenario);
        var voucherMetricId = LegacyAuditParityMetricIds.FilterVoucherCount(scenario);
        var legacy = LegacyAuditParityObservation.FromLegacy(
            LegacyParityCase.CaseA,
            Metrics(
                variant: false,
                filterCounts: new LegacyRowVoucherCounts(
                    LegacyObservedCount.Executed(1),
                    LegacyObservedCount.ExecutedUnavailable),
                creatorSummaryLegacyApplicability: LegacyMetricApplicability.NotApplicable,
                rareAccountsLegacyApplicability: LegacyMetricApplicability.NotApplicable));
        var provider = LegacyAuditParityObservation.FromProvider(
            LegacyParityCase.CaseA,
            LegacyAuditParityProvider.Sqlite,
            Metrics(
                variant: false,
                filterCounts: new LegacyRowVoucherCounts(
                    LegacyObservedCount.Executed(4),
                    LegacyObservedCount.Executed(2))));
        var classifications = new[] { rowMetricId, voucherMetricId }
            .Select(metricId => ClassificationForLegacyMetric(
                metricId,
                LegacyAuditParityDifferenceCategory.LegacyDefectOrInputCondition,
                LegacyAuditParityCitation.ParityPlanClassificationRules,
                LegacyAuditParityBasis.InputProfileCondition))
            .Concat(LegacyPrescreenParityPlan.LegacyNoCounterpartMetricIds.Select(metricId =>
                ClassificationForLegacyMetric(
                    metricId,
                    LegacyAuditParityDifferenceCategory.LegacyNoCounterpart,
                    LegacyAuditParityCitation.ParityPlanClassificationRules,
                    LegacyAuditParityBasis.LegacyNoComparableItem)));

        var registry = LegacyAuditParityComparator.CompareLegacyToProvider(
            legacy,
            provider,
            new LegacyAuditParityClassificationCatalog(classifications));

        var row = Assert.Single(registry.Entries, entry => entry.MetricId == rowMetricId);
        var voucher = Assert.Single(registry.Entries, entry => entry.MetricId == voucherMetricId);
        Assert.Equal(3L, row.DifferenceCount.GetValueOrDefault());
        Assert.Null(voucher.DifferenceCount);
        Assert.Equal(
            LegacyAuditParityDifferenceCategory.LegacyDefectOrInputCondition,
            voucher.Category);
    }

    [Fact]
    public void CompareLegacyToProvider_MissingScenarioParticipantFailsClosedBeforeClassification()
    {
        var scenario = LegacyFilterScenarioId.FromOrdinal(1);
        var legacy = LegacyAuditParityObservation.FromLegacy(
            LegacyParityCase.CaseA,
            Metrics(
                variant: false,
                creatorSummaryLegacyApplicability: LegacyMetricApplicability.NotApplicable,
                rareAccountsLegacyApplicability: LegacyMetricApplicability.NotApplicable));
        var provider = LegacyAuditParityObservation.FromProvider(
            LegacyParityCase.CaseA,
            LegacyAuditParityProvider.Sqlite,
            Metrics(variant: false, includeFilterScenario: false));

        var error = Assert.Throws<LegacyAuditParityComparisonCompletenessException>(() =>
            LegacyAuditParityComparator.CompareLegacyToProvider(
                legacy,
                provider,
                NoCounterpartClassifications(LegacyParityCase.CaseA)));

        Assert.Equal(LegacyAuditParityMetricIds.FilterRowCount(scenario), error.MetricId);
    }

    [Fact]
    public void CompareProviders_ExecutedUnavailableFilterVoucherFailsClosedAtVoucherMetric()
    {
        var scenario = LegacyFilterScenarioId.FromOrdinal(1);
        var sqlite = LegacyAuditParityObservation.FromProvider(
            LegacyParityCase.CaseA,
            LegacyAuditParityProvider.Sqlite,
            Metrics(
                variant: false,
                filterCounts: new LegacyRowVoucherCounts(
                    LegacyObservedCount.Executed(1),
                    LegacyObservedCount.ExecutedUnavailable)));
        var duckDb = LegacyAuditParityObservation.FromProvider(
            LegacyParityCase.CaseA,
            LegacyAuditParityProvider.DuckDb,
            Metrics(variant: false));

        var error = Assert.Throws<LegacyAuditParityComparisonCompletenessException>(() =>
            LegacyAuditParityComparator.CompareProviders([sqlite, duckDb]));

        Assert.Equal(LegacyAuditParityMetricIds.FilterVoucherCount(scenario), error.MetricId);
    }

    [Fact]
    public void ArtifactComparison_RequiresExactMetricDependenciesBeforeClassification()
    {
        var scenario = LegacyFilterScenarioId.FromOrdinal(1);
        var metricId = LegacyAuditParityMetricIds.FilterRowCount(scenario);
        var fallback = new LegacyAuditParityMappingProvenanceEntry(
            LegacyMappingDataset.Gl,
            LegacyMappingSlot.GlDocumentNumber,
            LegacyMappingResolutionSource.NormalizedHeaderIdentity);
        var provenance = new LegacyAuditParityMappingProvenance([fallback]);
        var legacy = new LegacyAuditParityObservationArtifactBundle(
            LegacyAuditParityObservation.FromLegacy(
                LegacyParityCase.CaseA,
                Metrics(
                    variant: false,
                    filterCounts: new LegacyRowVoucherCounts(1, 1),
                    creatorSummaryLegacyApplicability: LegacyMetricApplicability.NotApplicable,
                    rareAccountsLegacyApplicability: LegacyMetricApplicability.NotApplicable)),
            provenance);
        var provider = new LegacyAuditParityObservationArtifactBundle(
            LegacyAuditParityObservation.FromProvider(
                LegacyParityCase.CaseA,
                LegacyAuditParityProvider.Sqlite,
                Metrics(
                    variant: false,
                    filterCounts: new LegacyRowVoucherCounts(2, 1))),
            provenance);
        var noCounterpart = LegacyPrescreenParityPlan.LegacyNoCounterpartMetricIds.Select(id =>
            ClassificationForLegacyMetric(
                id,
                LegacyAuditParityDifferenceCategory.LegacyNoCounterpart,
                LegacyAuditParityCitation.ParityPlanClassificationRules,
                LegacyAuditParityBasis.LegacyNoComparableItem));
        var missingDependency = new LegacyAuditParityClassification(
            LegacyParityCase.CaseA,
            metricId,
            LegacyAuditParityDifferenceCategory.LegacyDefectOrInputCondition,
            LegacyAuditParityCitation.ParityPlanClassificationRules,
            LegacyAuditParityBasis.InputProfileCondition,
            NoMappingDependencies,
            LegacyAuditParityHarnessBindingVerification.NotRequired);

        Assert.Throws<InvalidOperationException>(() =>
            LegacyAuditParityComparator.CompareLegacyToProvider(
                legacy,
                provider,
                new LegacyAuditParityClassificationCatalog(
                    new[] { missingDependency }.Concat(noCounterpart))));

        var verified = new LegacyAuditParityClassification(
            LegacyParityCase.CaseA,
            metricId,
            LegacyAuditParityDifferenceCategory.JetDefect,
            LegacyAuditParityCitation.ActionContractFilter,
            LegacyAuditParityBasis.JetRequirementMismatch,
            [fallback],
            LegacyAuditParityHarnessBindingVerification.Verified);
        var registry = LegacyAuditParityComparator.CompareLegacyToProvider(
            legacy,
            provider,
            new LegacyAuditParityClassificationCatalog(
                new[] { verified }.Concat(noCounterpart)));
        Assert.Contains(registry.Entries, entry => entry.MetricId == metricId);

        Assert.Throws<ArgumentException>(() => new LegacyAuditParityClassification(
            LegacyParityCase.CaseA,
            metricId,
            LegacyAuditParityDifferenceCategory.JetDefect,
            LegacyAuditParityCitation.ActionContractFilter,
            LegacyAuditParityBasis.JetRequirementMismatch,
            [fallback],
            LegacyAuditParityHarnessBindingVerification.Unverified));
    }

    [Fact]
    public void ArtifactProviderMismatch_FallbackRequiresExplicitTypedVerifiedCatalog()
    {
        var scenario = LegacyFilterScenarioId.FromOrdinal(1);
        var metricId = LegacyAuditParityMetricIds.FilterRowCount(scenario);
        var comparisonId = LegacyAuditParityComparisonId.SqliteVsDuckDb;
        var fallback = new LegacyAuditParityMappingProvenanceEntry(
            LegacyMappingDataset.Gl,
            LegacyMappingSlot.GlDocumentNumber,
            LegacyMappingResolutionSource.NormalizedHeaderIdentity);
        var provenance = new LegacyAuditParityMappingProvenance([fallback]);
        var sqlite = new LegacyAuditParityObservationArtifactBundle(
            LegacyAuditParityObservation.FromProvider(
                LegacyParityCase.CaseA,
                LegacyAuditParityProvider.Sqlite,
                Metrics(
                    variant: false,
                    filterCounts: new LegacyRowVoucherCounts(1, 1))),
            provenance);
        var duckDb = new LegacyAuditParityObservationArtifactBundle(
            LegacyAuditParityObservation.FromProvider(
                LegacyParityCase.CaseA,
                LegacyAuditParityProvider.DuckDb,
                Metrics(
                    variant: false,
                    filterCounts: new LegacyRowVoucherCounts(2, 1))),
            provenance);

        var missing = Assert.Throws<LegacyAuditParityHarnessBindingVerificationRequiredException>(() =>
            LegacyAuditParityComparator.CompareProviders(
                [sqlite, duckDb],
                new LegacyAuditParityHarnessBindingVerificationCatalog([])));
        var unverified = Assert.Throws<LegacyAuditParityHarnessBindingVerificationRequiredException>(() =>
            LegacyAuditParityComparator.CompareProviders(
                [sqlite, duckDb],
                new LegacyAuditParityHarnessBindingVerificationCatalog(
                [
                    new LegacyAuditParityHarnessBindingVerificationEntry(
                        LegacyParityCase.CaseA,
                        comparisonId,
                        metricId,
                        LegacyAuditParityHarnessBindingVerification.Unverified),
                ])));
        var verifiedRegistry = LegacyAuditParityComparator.CompareProviders(
            [sqlite, duckDb],
            new LegacyAuditParityHarnessBindingVerificationCatalog(
            [
                new LegacyAuditParityHarnessBindingVerificationEntry(
                    LegacyParityCase.CaseA,
                    comparisonId,
                    metricId,
                    LegacyAuditParityHarnessBindingVerification.Verified),
            ]));

        Assert.Equal(metricId, missing.MetricId);
        Assert.Equal(comparisonId, missing.ComparisonId);
        Assert.Equal(metricId, unverified.MetricId);
        var difference = Assert.Single(verifiedRegistry.Entries);
        Assert.True(difference.IsProviderMismatch);
        Assert.Equal(LegacyAuditParityDifferenceCategory.JetDefect, difference.Category);
        Assert.Equal(LegacyAuditParityCitation.ParityPlanProviderConsistency, difference.Citation);
        Assert.Equal(LegacyAuditParityBasis.ProviderInconsistency, difference.Basis);
        Assert.Equal(LegacyAuditParityHarnessBindingVerification.Verified,
            difference.HarnessBindingVerification);
        Assert.Equal(fallback, Assert.Single(difference.MappingDependencies));
    }

    [Fact]
    public void ArtifactComparison_RejectsDifferentCapturedProvenanceBeforeMetricClassification()
    {
        var metrics = Metrics(variant: false);
        var legacy = new LegacyAuditParityObservationArtifactBundle(
            LegacyAuditParityObservation.FromLegacy(LegacyParityCase.CaseA, metrics),
            new LegacyAuditParityMappingProvenance([]));
        var provider = new LegacyAuditParityObservationArtifactBundle(
            LegacyAuditParityObservation.FromProvider(
                LegacyParityCase.CaseA,
                LegacyAuditParityProvider.Sqlite,
                metrics),
            new LegacyAuditParityMappingProvenance(
            [
                new LegacyAuditParityMappingProvenanceEntry(
                    LegacyMappingDataset.Gl,
                    LegacyMappingSlot.GlDocumentNumber,
                    LegacyMappingResolutionSource.WorkingPaperExplicitMapping),
            ]));

        var error = Assert.Throws<InvalidOperationException>(() =>
            LegacyAuditParityComparator.CompareLegacyToProvider(
                legacy,
                provider,
                new LegacyAuditParityClassificationCatalog([])));

        Assert.Contains("provenance", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReportDifference_AggregatesContinuationRowsButKeepsFamilySheetCountObservable()
    {
        var family = LegacyAuditParityReportFamily.WorkingPaperRiskVoucherMatrix;
        var left = new LegacyReportObservation(
        [
            new KeyValuePair<string, long>(
                LegacyAuditParityReportMetrics.BaseSheetName(family),
                3),
        ]);
        var right = new LegacyReportObservation(
        [
            new KeyValuePair<string, long>(
                LegacyAuditParityReportMetrics.BaseSheetName(family),
                1),
            new KeyValuePair<string, long>(
                LegacyAuditParityReportMetrics.ContinuationSheetName(
                    family,
                    part: 2,
                    localizedSuffix: true),
                2),
        ]);

        var differences = LegacyAuditParityReportMetrics.Difference(
            LegacyReportKind.WorkingPaper,
            left,
            right);

        var difference = Assert.Single(differences);
        Assert.Equal(LegacyAuditParityReportMetrics.SheetCount(family), difference.MetricId);
        Assert.Equal(1, difference.DifferenceCount);
    }

    [Fact]
    public void ReportMetricCatalog_CoversAllSixReportsAndRetiresAggregateCandidates()
    {
        foreach (var report in Enum.GetValues<LegacyReportKind>())
        {
            var families = LegacyAuditParityReportMetrics.FamiliesFor(report);
            Assert.NotEmpty(families);
            Assert.All(
                families,
                family =>
                {
                    Assert.True(LegacyAuditParityMetricIds.IsFixed(
                        LegacyAuditParityReportMetrics.SheetCount(family)));
                    Assert.True(LegacyAuditParityMetricIds.IsFixed(
                        LegacyAuditParityReportMetrics.DataRowCount(family)));
                    var observation = new LegacyReportObservation(
                    [
                        new KeyValuePair<string, long>(
                            LegacyAuditParityReportMetrics.BaseSheetName(family),
                            0),
                    ]);
                    Assert.Empty(LegacyAuditParityReportMetrics.Difference(
                        report,
                        observation,
                        observation));
                });
            Assert.False(LegacyAuditParityMetricIds.IsFixed(
                LegacyAuditParityMetricIds.ReportSheetList(report)));
            Assert.False(LegacyAuditParityMetricIds.IsFixed(
                LegacyAuditParityMetricIds.ReportDataRowCounts(report)));
        }
    }

    private static LegacyAuditParityMetrics Metrics(
        bool variant,
        bool unavailablePartA = false,
        bool unavailablePrescreen = false,
        LegacyRowVoucherCounts? filterCounts = null,
        bool includeFilterScenario = true,
        LegacyMetricApplicability creatorSummaryLegacyApplicability =
            LegacyMetricApplicability.NotExecuted,
        LegacyMetricApplicability rareAccountsLegacyApplicability =
            LegacyMetricApplicability.NotExecuted)
    {
        var completeness = unavailablePartA
            ? new LegacyCompletenessObservation(
                variant ? 3 : 1,
                null,
                null,
                null,
                null,
                partARowCountMatch: !variant,
                partAAmountMatch: !variant)
            : variant
                ? new LegacyCompletenessObservation(3, 12, 11, 101m, 99m, false, false)
                : new LegacyCompletenessObservation(1, 10, 10, 100m, 100m, true, true);
        var infKeys = variant
            ? new[]
            {
                LegacyInfMemberKey.Create(RawInfAlternate, "1"),
                LegacyInfMemberKey.Create(RawInfAlternate, "2"),
                LegacyInfMemberKey.Create(RawInfAlternate, "2"),
            }
            : new[]
            {
                LegacyInfMemberKey.Create(RawInfSentinel, "1"),
                LegacyInfMemberKey.Create(RawInfSentinel, "1"),
            };
        var prescreen = LegacyPrescreenRuleCatalog.All.ToDictionary(
            rule => rule,
            _ => unavailablePrescreen
                ? new LegacyRowVoucherCounts(null, null)
                : variant
                    ? new LegacyRowVoucherCounts(2, 3)
                    : new LegacyRowVoucherCounts(1, 1));
        var scenario = LegacyFilterScenarioId.FromOrdinal(1);
        var filters = new Dictionary<LegacyFilterScenarioId, LegacyRowVoucherCounts>();
        if (includeFilterScenario)
        {
            filters[scenario] = filterCounts ?? (variant
                ? new LegacyRowVoucherCounts(4, 2)
                : new LegacyRowVoucherCounts(1, 1));
        }
        var reports = Enum.GetValues<LegacyReportKind>().ToDictionary(
            report => report,
            report => new LegacyReportObservation(
            [
                new KeyValuePair<string, long>(
                    LegacyAuditParityReportMetrics.BaseSheetName(
                        RepresentativeReportFamily(report)),
                    variant ? 3 : 1),
            ]));
        var weekendUnion = unavailablePrescreen
            ? new LegacyRowVoucherCounts(null, null)
            : variant
                ? new LegacyRowVoucherCounts(5, 3)
                : new LegacyRowVoucherCounts(2, 1);

        return new LegacyAuditParityMetrics(
            completeness,
            unbalancedVoucherCount: variant ? 2 : 0,
            new LegacyInfObservation(infKeys.LongLength, infKeys),
            prescreen,
            filters,
            reports,
            weekendUnion,
            creatorSummaryLegacyApplicability,
            rareAccountsLegacyApplicability);
    }

    private static IReadOnlyList<string> ExpectedMismatchMetricIds()
    {
        var metricIds = new List<string>
        {
            LegacyAuditParityMetricIds.CompletenessDifferenceAccountCount,
            LegacyAuditParityMetricIds.PartATargetRowCount,
            LegacyAuditParityMetricIds.PartATotalDebit,
            LegacyAuditParityMetricIds.PartATotalCredit,
            LegacyAuditParityMetricIds.UnbalancedVoucherCount,
            LegacyAuditParityMetricIds.InfSampleSize,
            LegacyAuditParityMetricIds.InfMemberKeyMultiset,
            LegacyAuditParityMetricIds.FilterRowCount(LegacyFilterScenarioId.FromOrdinal(1)),
            LegacyAuditParityMetricIds.FilterVoucherCount(LegacyFilterScenarioId.FromOrdinal(1)),
        };
        foreach (var rule in LegacyPrescreenParityPlan.DirectRules)
        {
            metricIds.Add(LegacyAuditParityMetricIds.PrescreenRowCount(rule));
            metricIds.Add(LegacyAuditParityMetricIds.PrescreenVoucherCount(rule));
        }
        foreach (var report in Enum.GetValues<LegacyReportKind>())
        {
            metricIds.Add(LegacyAuditParityReportMetrics.DataRowCount(
                RepresentativeReportFamily(report)));
        }
        metricIds.AddRange(LegacyPrescreenParityPlan.LegacyNoCounterpartMetricIds);
        return metricIds;
    }

    private static LegacyAuditParityClassification ClassificationForLegacyMetric(
        string metricId,
        LegacyAuditParityDifferenceCategory category,
        LegacyAuditParityCitation citation,
        LegacyAuditParityBasis basis)
    {
        if (LegacyPrescreenParityPlan.LegacyNoCounterpartMetricIds.Contains(
                metricId,
                StringComparer.Ordinal))
        {
            return new LegacyAuditParityClassification(
                LegacyParityCase.CaseA,
                metricId,
                LegacyAuditParityDifferenceCategory.LegacyNoCounterpart,
                LegacyAuditParityCitation.ParityPlanClassificationRules,
                LegacyAuditParityBasis.LegacyNoComparableItem,
                NoMappingDependencies,
                LegacyAuditParityHarnessBindingVerification.NotRequired);
        }

        return new LegacyAuditParityClassification(
            LegacyParityCase.CaseA,
            metricId,
            category,
            citation,
            basis,
            NoMappingDependencies,
            LegacyAuditParityHarnessBindingVerification.NotRequired);
    }

    private static LegacyAuditParityClassificationCatalog NoCounterpartClassifications(
        LegacyParityCase @case) => new(
        LegacyPrescreenParityPlan.LegacyNoCounterpartMetricIds.Select(metricId =>
            new LegacyAuditParityClassification(
                @case,
                metricId,
                LegacyAuditParityDifferenceCategory.LegacyNoCounterpart,
                LegacyAuditParityCitation.ParityPlanClassificationRules,
                LegacyAuditParityBasis.LegacyNoComparableItem,
                NoMappingDependencies,
                LegacyAuditParityHarnessBindingVerification.NotRequired)));

    private static readonly string[] CategoryMetricIds =
    [
        LegacyAuditParityMetricIds.CompletenessDifferenceAccountCount,
        LegacyAuditParityMetricIds.UnbalancedVoucherCount,
        LegacyAuditParityMetricIds.InfSampleSize,
        LegacyAuditParityMetricIds.PrescreenRowCount(LegacyPrescreenRuleId.HolidayPosting),
        LegacyAuditParityMetricIds.FilterScenarioCount,
    ];

    private static LegacyAuditParityCitation CitationFor(
        LegacyAuditParityDifferenceCategory category,
        LegacyAuditParityBasis basis) => (category, basis) switch
        {
            (LegacyAuditParityDifferenceCategory.JetDefect,
                LegacyAuditParityBasis.ProviderInconsistency) =>
                LegacyAuditParityCitation.ParityPlanProviderConsistency,
            (LegacyAuditParityDifferenceCategory.JetDefect, _) =>
                LegacyAuditParityCitation.JetGuideSection4,
            (LegacyAuditParityDifferenceCategory.IntentionalDecision, _) =>
                LegacyAuditParityCitation.JetGuideSection4,
            (LegacyAuditParityDifferenceCategory.LegacyDefectOrInputCondition,
                LegacyAuditParityBasis.LegacyBehaviorDefect) =>
                LegacyAuditParityCitation.LegacyIdeaScript,
            (LegacyAuditParityDifferenceCategory.LegacyDefectOrInputCondition, _) =>
                LegacyAuditParityCitation.ParityPlanClassificationRules,
            (LegacyAuditParityDifferenceCategory.LegacyNoCounterpart, _) =>
                LegacyAuditParityCitation.ParityPlanClassificationRules,
            (LegacyAuditParityDifferenceCategory.UserDecisionRequired, _) =>
                LegacyAuditParityCitation.ParityPlanClassificationRules,
            _ => LegacyAuditParityCitation.ParityPlanClassificationRules,
        };

    private static void AssertNoRawSentinels(string output)
    {
        Assert.DoesNotContain(RawInfSentinel, output, StringComparison.Ordinal);
        Assert.DoesNotContain(RawInfAlternate, output, StringComparison.Ordinal);
    }

    private static LegacyAuditParityReportFamily RepresentativeReportFamily(
        LegacyReportKind report) => report switch
    {
        LegacyReportKind.ValidationReport =>
            LegacyAuditParityReportFamily.ValidationCompletenessReconciliation,
        LegacyReportKind.AccountMapping =>
            LegacyAuditParityReportFamily.AccountMappingMain,
        LegacyReportKind.InfReport =>
            LegacyAuditParityReportFamily.InfReliability,
        LegacyReportKind.PrescreenReport =>
            LegacyAuditParityReportFamily.PrescreenPostPeriodApprovalDetail,
        LegacyReportKind.CriteriaSelectionReport =>
            LegacyAuditParityReportFamily.CriteriaScenario001,
        LegacyReportKind.WorkingPaper =>
            LegacyAuditParityReportFamily.WorkingPaperCompleteness,
        _ => throw new ArgumentOutOfRangeException(nameof(report)),
    };
}
