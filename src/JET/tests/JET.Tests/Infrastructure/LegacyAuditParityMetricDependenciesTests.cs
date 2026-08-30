using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class LegacyAuditParityMetricDependenciesTests
{
    private static readonly IReadOnlyDictionary<LegacyPrescreenRuleId, LegacyMappingSlot[]> ProviderRuleSlots =
        new Dictionary<LegacyPrescreenRuleId, LegacyMappingSlot[]>
        {
            [LegacyPrescreenRuleId.PostPeriodApproval] =
            [
                LegacyMappingSlot.GlDocumentNumber,
                LegacyMappingSlot.GlPostingDate,
                LegacyMappingSlot.GlApprovalDate,
            ],
            [LegacyPrescreenRuleId.SuspiciousKeywords] =
            [
                LegacyMappingSlot.GlDocumentNumber,
                LegacyMappingSlot.GlPostingDate,
                LegacyMappingSlot.GlDescription,
            ],
            [LegacyPrescreenRuleId.UnexpectedAccountPair] =
            [
                LegacyMappingSlot.GlDocumentNumber,
                LegacyMappingSlot.GlPostingDate,
                LegacyMappingSlot.GlAccountNumber,
                LegacyMappingSlot.GlAmount,
                LegacyMappingSlot.GlDebitAmount,
                LegacyMappingSlot.GlCreditAmount,
                LegacyMappingSlot.GlDebitCreditField,
                LegacyMappingSlot.GlDebitCode,
            ],
            [LegacyPrescreenRuleId.TrailingZeros] =
            [
                LegacyMappingSlot.GlDocumentNumber,
                LegacyMappingSlot.GlPostingDate,
                LegacyMappingSlot.GlAmount,
                LegacyMappingSlot.GlDebitAmount,
                LegacyMappingSlot.GlCreditAmount,
                LegacyMappingSlot.GlDebitCreditField,
                LegacyMappingSlot.GlDebitCode,
            ],
            [LegacyPrescreenRuleId.WeekendPosting] =
            [
                LegacyMappingSlot.GlDocumentNumber,
                LegacyMappingSlot.GlPostingDate,
            ],
            [LegacyPrescreenRuleId.WeekendApproval] =
            [
                LegacyMappingSlot.GlDocumentNumber,
                LegacyMappingSlot.GlPostingDate,
                LegacyMappingSlot.GlApprovalDate,
            ],
            [LegacyPrescreenRuleId.HolidayPosting] =
            [
                LegacyMappingSlot.GlDocumentNumber,
                LegacyMappingSlot.GlPostingDate,
            ],
            [LegacyPrescreenRuleId.HolidayApproval] =
            [
                LegacyMappingSlot.GlDocumentNumber,
                LegacyMappingSlot.GlPostingDate,
                LegacyMappingSlot.GlApprovalDate,
            ],
            [LegacyPrescreenRuleId.BlankDescription] =
            [
                LegacyMappingSlot.GlDocumentNumber,
                LegacyMappingSlot.GlPostingDate,
                LegacyMappingSlot.GlDescription,
            ],
            [LegacyPrescreenRuleId.BackdatedPosting] =
            [
                LegacyMappingSlot.GlDocumentNumber,
                LegacyMappingSlot.GlPostingDate,
                LegacyMappingSlot.GlVoucherDate,
            ],
            [LegacyPrescreenRuleId.NonAuthorizedPreparer] =
            [
                LegacyMappingSlot.GlDocumentNumber,
                LegacyMappingSlot.GlPostingDate,
                LegacyMappingSlot.GlCreatedBy,
            ],
            [LegacyPrescreenRuleId.LowFrequencyPreparer] =
            [
                LegacyMappingSlot.GlDocumentNumber,
                LegacyMappingSlot.GlPostingDate,
                LegacyMappingSlot.GlCreatedBy,
            ],
            [LegacyPrescreenRuleId.LowFrequencyAccount] =
            [
                LegacyMappingSlot.GlDocumentNumber,
                LegacyMappingSlot.GlPostingDate,
                LegacyMappingSlot.GlAccountNumber,
            ],
        };

    [Fact]
    public void ProviderPrescreenMetrics_ResolveExactSlotsForAllThirteenCanonicalRules()
    {
        var captured = CompleteSyntheticProvenance();

        Assert.True(
            ProviderRuleSlots.Keys.ToHashSet().SetEquals(LegacyPrescreenRuleCatalog.All),
            "Provider dependency oracle must cover exactly the thirteen canonical prescreen rules.");
        foreach (var pair in ProviderRuleSlots)
        {
            foreach (var metricId in new[]
                     {
                         LegacyAuditParityMetricIds.PrescreenRowCount(pair.Key),
                         LegacyAuditParityMetricIds.PrescreenVoucherCount(pair.Key),
                     })
            {
                Assert.Equal(
                    ExpectedEntries(captured, pair.Value),
                    LegacyAuditParityMetricDependencies.Resolve(metricId, captured));
            }
        }
    }

    [Fact]
    public void LegacyNoCounterpartRegistration_IsEmptyButProviderNumericDependenciesKeepFallbacks()
    {
        var captured = CompleteSyntheticProvenance();
        foreach (var rule in ExactLegacyNoCounterpartRules())
        {
            foreach (var metricId in new[]
                     {
                         LegacyAuditParityMetricIds.PrescreenRowCount(rule),
                         LegacyAuditParityMetricIds.PrescreenVoucherCount(rule),
                     })
            {
                Assert.Empty(LegacyAuditParityMetricDependencies.Resolve(
                    metricId,
                    captured,
                    LegacyAuditParityMetricDependencyContext.LegacyNoCounterpartRegistration));
                var numeric = LegacyAuditParityMetricDependencies.Resolve(metricId, captured);
                Assert.Equal(ExpectedEntries(captured, ProviderRuleSlots[rule]), numeric);
                Assert.Contains(numeric, entry =>
                    entry.ResolutionSource != LegacyMappingResolutionSource.WorkingPaperExplicitMapping);
            }
        }
    }

    [Fact]
    public void LegacyNoCounterpartWeekendUnion_IsStructuralButProviderDependenciesRemainNumeric()
    {
        var captured = CompleteSyntheticProvenance();
        foreach (var metricId in LegacyPrescreenParityPlan.LegacyNoCounterpartWeekendMetricIds)
        {
            Assert.Empty(LegacyAuditParityMetricDependencies.Resolve(
                metricId,
                captured,
                LegacyAuditParityMetricDependencyContext.LegacyNoCounterpartRegistration));
            Assert.NotEmpty(LegacyAuditParityMetricDependencies.Resolve(metricId, captured));
        }
    }

    [Fact]
    public void AccountMappingReportMetrics_SeparateFixedSheetAndFamilySpecificRowDependencies()
    {
        var captured = CompleteSyntheticProvenance();
        var expectedSlots = new[]
        {
            LegacyMappingSlot.GlPostingDate,
            LegacyMappingSlot.GlAccountNumber,
            LegacyMappingSlot.GlAccountName,
            LegacyMappingSlot.TbAccountNumber,
            LegacyMappingSlot.TbAccountName,
        };
        var expected = ExpectedEntries(captured, expectedSlots);

        Assert.Empty(LegacyAuditParityMetricDependencies.Resolve(
            LegacyAuditParityReportMetrics.SheetCount(
                LegacyAuditParityReportFamily.AccountMappingMain),
            captured));
        Assert.Equal(
            expected,
            LegacyAuditParityMetricDependencies.Resolve(
                LegacyAuditParityReportMetrics.DataRowCount(
                    LegacyAuditParityReportFamily.AccountMappingMain),
                captured));
        Assert.Empty(LegacyAuditParityMetricDependencies.Resolve(
            LegacyAuditParityReportMetrics.DataRowCount(
                LegacyAuditParityReportFamily.AccountMappingList),
            captured));
    }

    [Fact]
    public void ReportMetrics_ResolveFamilyAndMetricKindSpecificDependenciesAcrossAllSixReports()
    {
        var captured = CompleteSyntheticProvenance();

        AssertBoth(
            LegacyAuditParityReportFamily.ValidationNullDescriptionDetail,
            LegacyMappingSlot.GlPostingDate,
            LegacyMappingSlot.GlDescription);
        Assert.Empty(Resolve(
            LegacyAuditParityReportFamily.AccountMappingList,
            LegacyAuditParityReportMetricKind.DataRowCount));
        Assert.Empty(Resolve(
            LegacyAuditParityReportFamily.InfReliability,
            LegacyAuditParityReportMetricKind.SheetCount));
        AssertRows(
            LegacyAuditParityReportFamily.InfReliability,
            LegacyMappingSlot.GlPostingDate);
        AssertBoth(
            LegacyAuditParityReportFamily.PrescreenSuspiciousKeywordsDetail,
            LegacyMappingSlot.GlPostingDate,
            LegacyMappingSlot.GlDescription);
        AssertBoth(
            LegacyAuditParityReportFamily.CriteriaScenario001,
            Enum.GetValues<LegacyMappingSlot>()
                .Where(slot => slot.ToString().StartsWith("Gl", StringComparison.Ordinal)
                    && slot != LegacyMappingSlot.GlManual)
                .ToArray());
        Assert.Empty(Resolve(
            LegacyAuditParityReportFamily.WorkingPaperCalendarInfo,
            LegacyAuditParityReportMetricKind.DataRowCount));
        Assert.Empty(Resolve(
            LegacyAuditParityReportFamily.WorkingPaperFieldInfo,
            LegacyAuditParityReportMetricKind.SheetCount));
        AssertRows(
            LegacyAuditParityReportFamily.WorkingPaperFieldInfo,
            LegacyMappingSlot.GlLineIdentifier);

        void AssertBoth(
            LegacyAuditParityReportFamily family,
            params LegacyMappingSlot[] slots)
        {
            var expected = ExpectedEntries(captured, slots);
            Assert.Equal(expected, Resolve(family, LegacyAuditParityReportMetricKind.SheetCount));
            Assert.Equal(expected, Resolve(family, LegacyAuditParityReportMetricKind.DataRowCount));
        }

        void AssertRows(
            LegacyAuditParityReportFamily family,
            params LegacyMappingSlot[] slots) => Assert.Equal(
                ExpectedEntries(captured, slots),
                Resolve(family, LegacyAuditParityReportMetricKind.DataRowCount));

        IReadOnlyList<LegacyAuditParityMappingProvenanceEntry> Resolve(
            LegacyAuditParityReportFamily family,
            LegacyAuditParityReportMetricKind kind) =>
            LegacyAuditParityMetricDependencies.Resolve(
                kind == LegacyAuditParityReportMetricKind.SheetCount
                    ? LegacyAuditParityReportMetrics.SheetCount(family)
                    : LegacyAuditParityReportMetrics.DataRowCount(family),
                captured);
    }

    [Fact]
    public void ReportMetricCatalog_EveryFixedIdentityHasAClosedDependencyProjection()
    {
        var captured = CompleteSyntheticProvenance();

        Assert.All(
            LegacyAuditParityReportMetrics.FixedMetricIds,
            metricId => Assert.All(
                LegacyAuditParityMetricDependencies.Resolve(metricId, captured),
                dependency => Assert.Contains(dependency, captured.Entries)));
    }

    [Fact]
    public void ValidationReportMetrics_DistinguishPopulationRowsFromDifferenceAndFieldInfoRows()
    {
        var captured = CompleteSyntheticProvenance();
        var population = ExpectedEntries(captured,
        [
            LegacyMappingSlot.GlPostingDate,
            LegacyMappingSlot.GlAccountNumber,
            LegacyMappingSlot.TbAccountNumber,
        ]);
        var difference = ExpectedEntries(captured,
        [
            LegacyMappingSlot.GlPostingDate,
            LegacyMappingSlot.GlAccountNumber,
            LegacyMappingSlot.GlAmount,
            LegacyMappingSlot.GlDebitAmount,
            LegacyMappingSlot.GlCreditAmount,
            LegacyMappingSlot.GlDebitCreditField,
            LegacyMappingSlot.GlDebitCode,
            LegacyMappingSlot.TbAccountNumber,
            LegacyMappingSlot.TbAmount,
            LegacyMappingSlot.TbDebitAmount,
            LegacyMappingSlot.TbCreditAmount,
            LegacyMappingSlot.TbOpeningBalance,
            LegacyMappingSlot.TbClosingBalance,
            LegacyMappingSlot.TbOpeningDebit,
            LegacyMappingSlot.TbOpeningCredit,
            LegacyMappingSlot.TbClosingDebit,
            LegacyMappingSlot.TbClosingCredit,
        ]);

        Assert.Equal(
            population,
            LegacyAuditParityMetricDependencies.Resolve(
                LegacyAuditParityReportMetrics.DataRowCount(
                    LegacyAuditParityReportFamily.ValidationCompletenessReconciliation),
                captured));
        Assert.Equal(
            difference,
            LegacyAuditParityMetricDependencies.Resolve(
                LegacyAuditParityReportMetrics.DataRowCount(
                    LegacyAuditParityReportFamily.ValidationCompletenessDifferenceDetail),
                captured));
        Assert.Equal(
            ExpectedEntries(captured, [LegacyMappingSlot.GlLineIdentifier]),
            LegacyAuditParityMetricDependencies.Resolve(
                LegacyAuditParityReportMetrics.DataRowCount(
                    LegacyAuditParityReportFamily.ValidationFieldInfo),
                captured));
    }

    private static LegacyAuditParityMappingProvenance CompleteSyntheticProvenance() => new(
        Enum.GetValues<LegacyMappingSlot>().Select(slot =>
            new LegacyAuditParityMappingProvenanceEntry(
                slot.ToString().StartsWith("Gl", StringComparison.Ordinal)
                    ? LegacyMappingDataset.Gl
                    : LegacyMappingDataset.Tb,
                slot,
                slot == LegacyMappingSlot.GlPostingDate
                    ? LegacyMappingResolutionSource.NormalizedHeaderIdentity
                    : LegacyMappingResolutionSource.WorkingPaperExplicitMapping)));

    private static IReadOnlyList<LegacyAuditParityMappingProvenanceEntry> ExpectedEntries(
        LegacyAuditParityMappingProvenance captured,
        IEnumerable<LegacyMappingSlot> slots)
    {
        var expectedSlots = slots.ToHashSet();
        return captured.Entries.Where(entry => expectedSlots.Contains(entry.Slot)).ToArray();
    }

    private static IReadOnlyList<LegacyPrescreenRuleId> ExactLegacyNoCounterpartRules() =>
    [
        LegacyPrescreenRuleId.HolidayPosting,
        LegacyPrescreenRuleId.HolidayApproval,
        LegacyPrescreenRuleId.BackdatedPosting,
        LegacyPrescreenRuleId.NonAuthorizedPreparer,
        LegacyPrescreenRuleId.LowFrequencyPreparer,
        LegacyPrescreenRuleId.LowFrequencyAccount,
    ];
}
