namespace JET.Tests.Infrastructure;

internal enum LegacyAuditParityMetricDependencyContext
{
    NumericComparison,
    LegacyNoCounterpartRegistration,
}

/// <summary>
/// Test-only, typed map from a parity metric to the profile mappings used to produce it.
/// The map deliberately resolves against the captured artifact provenance, so amount-mode
/// alternatives that were not present in the run cannot be invented in the registry.
/// </summary>
internal static class LegacyAuditParityMetricDependencies
{
    private static readonly IReadOnlySet<LegacyMappingSlot> GlAmountSlots = new HashSet<LegacyMappingSlot>
    {
        LegacyMappingSlot.GlAmount,
        LegacyMappingSlot.GlDebitAmount,
        LegacyMappingSlot.GlCreditAmount,
        LegacyMappingSlot.GlDebitCreditField,
        LegacyMappingSlot.GlDebitCode,
    };

    private static readonly IReadOnlySet<LegacyMappingSlot> TbAmountSlots = new HashSet<LegacyMappingSlot>
    {
        LegacyMappingSlot.TbAmount,
        LegacyMappingSlot.TbDebitAmount,
        LegacyMappingSlot.TbCreditAmount,
        LegacyMappingSlot.TbOpeningBalance,
        LegacyMappingSlot.TbClosingBalance,
        LegacyMappingSlot.TbOpeningDebit,
        LegacyMappingSlot.TbOpeningCredit,
        LegacyMappingSlot.TbClosingDebit,
        LegacyMappingSlot.TbClosingCredit,
    };

    private static readonly IReadOnlySet<LegacyMappingSlot> AllGlSlots = Enum
        .GetValues<LegacyMappingSlot>()
        .Where(slot => slot.ToString().StartsWith("Gl", StringComparison.Ordinal))
        .ToHashSet();

    internal static IReadOnlyList<LegacyAuditParityMappingProvenanceEntry> Resolve(
        string metricId,
        LegacyAuditParityMappingProvenance captured,
        LegacyAuditParityMetricDependencyContext context =
            LegacyAuditParityMetricDependencyContext.NumericComparison)
    {
        ArgumentNullException.ThrowIfNull(captured);
        if (!LegacyAuditParityMetricIds.IsFixed(metricId) || !Enum.IsDefined(context))
        {
            throw new ArgumentException("Metric dependencies require a fixed parity metric id.", nameof(metricId));
        }

        var slots = Slots(metricId, context);
        return captured.Entries
            .Where(entry => slots.Contains(entry.Slot))
            .OrderBy(entry => entry.Dataset)
            .ThenBy(entry => entry.Slot)
            .ToArray();
    }

    internal static void EnsureExact(
        string metricId,
        LegacyAuditParityMappingProvenance captured,
        IReadOnlyList<LegacyAuditParityMappingProvenanceEntry> declared,
        LegacyAuditParityMetricDependencyContext context =
            LegacyAuditParityMetricDependencyContext.NumericComparison)
    {
        ArgumentNullException.ThrowIfNull(declared);
        var expected = Resolve(metricId, captured, context);
        if (!expected.SequenceEqual(declared))
        {
            throw new InvalidOperationException(
                $"Parity classification mapping dependencies were incomplete: {metricId}.");
        }
    }

    private static IReadOnlySet<LegacyMappingSlot> Slots(
        string metricId,
        LegacyAuditParityMetricDependencyContext context)
    {
        if (context == LegacyAuditParityMetricDependencyContext.LegacyNoCounterpartRegistration)
        {
            if (!LegacyPrescreenParityPlan.LegacyNoCounterpartMetricIds.Contains(
                    metricId,
                    StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Metric is not a fixed legacy-no-counterpart registration: {metricId}.");
            }

            return Empty();
        }

        if (metricId is LegacyAuditParityMetricIds.PartASourceRowCount
                or LegacyAuditParityMetricIds.PartARowCountMatch
                or LegacyAuditParityMetricIds.PartAAmountMatch
                or LegacyAuditParityMetricIds.FilterScenarioCount
                or LegacyAuditParityMetricIds.CreatorSummaryNotApplicable
                or LegacyAuditParityMetricIds.RareAccountsNotApplicable)
        {
            return Empty();
        }

        if (LegacyAuditParityMetricIds.IsFilterLegacySummaryDetailRowCount(metricId))
        {
            return Empty();
        }

        if (metricId == LegacyAuditParityMetricIds.CompletenessDifferenceAccountCount)
        {
            return Set(
                [
                    LegacyMappingSlot.GlAccountNumber,
                    LegacyMappingSlot.GlPostingDate,
                    LegacyMappingSlot.TbAccountNumber,
                    .. GlAmountSlots,
                    .. TbAmountSlots,
                ]);
        }

        if (metricId is LegacyAuditParityMetricIds.PartATargetRowCount
            or LegacyAuditParityMetricIds.PartATotalDebit
            or LegacyAuditParityMetricIds.PartATotalCredit)
        {
            return Set([LegacyMappingSlot.GlPostingDate, .. GlAmountSlots]);
        }

        if (metricId == LegacyAuditParityMetricIds.UnbalancedVoucherCount)
        {
            return Set(
                [
                    LegacyMappingSlot.GlDocumentNumber,
                    LegacyMappingSlot.GlPostingDate,
                    .. GlAmountSlots,
                ]);
        }

        if (metricId is LegacyAuditParityMetricIds.InfSampleSize
            or LegacyAuditParityMetricIds.InfMemberKeyMultiset)
        {
            return AllGlSlots;
        }

        if (metricId is LegacyAuditParityMetricIds.WeekendUnionRowCount
            or LegacyAuditParityMetricIds.WeekendUnionVoucherCount)
        {
            return Set(
            [
                LegacyMappingSlot.GlDocumentNumber,
                LegacyMappingSlot.GlPostingDate,
                LegacyMappingSlot.GlApprovalDate,
            ]);
        }

        foreach (var rule in LegacyPrescreenRuleCatalog.All)
        {
            if (metricId != LegacyAuditParityMetricIds.PrescreenRowCount(rule)
                && metricId != LegacyAuditParityMetricIds.PrescreenVoucherCount(rule))
            {
                continue;
            }

            return rule switch
            {
                LegacyPrescreenRuleId.PostPeriodApproval => Set(
                [
                    LegacyMappingSlot.GlDocumentNumber,
                    LegacyMappingSlot.GlPostingDate,
                    LegacyMappingSlot.GlApprovalDate,
                ]),
                LegacyPrescreenRuleId.SuspiciousKeywords => Set(
                [
                    LegacyMappingSlot.GlDocumentNumber,
                    LegacyMappingSlot.GlPostingDate,
                    LegacyMappingSlot.GlDescription,
                ]),
                LegacyPrescreenRuleId.UnexpectedAccountPair => Set(
                [
                    LegacyMappingSlot.GlDocumentNumber,
                    LegacyMappingSlot.GlPostingDate,
                    LegacyMappingSlot.GlAccountNumber,
                    .. GlAmountSlots,
                ]),
                LegacyPrescreenRuleId.TrailingZeros => Set(
                [
                    LegacyMappingSlot.GlDocumentNumber,
                    LegacyMappingSlot.GlPostingDate,
                    .. GlAmountSlots,
                ]),
                LegacyPrescreenRuleId.WeekendPosting
                    or LegacyPrescreenRuleId.HolidayPosting => Set(
                [
                    LegacyMappingSlot.GlDocumentNumber,
                    LegacyMappingSlot.GlPostingDate,
                ]),
                LegacyPrescreenRuleId.WeekendApproval
                    or LegacyPrescreenRuleId.HolidayApproval => Set(
                [
                    LegacyMappingSlot.GlDocumentNumber,
                    LegacyMappingSlot.GlPostingDate,
                    LegacyMappingSlot.GlApprovalDate,
                ]),
                LegacyPrescreenRuleId.BlankDescription => Set(
                [
                    LegacyMappingSlot.GlDocumentNumber,
                    LegacyMappingSlot.GlPostingDate,
                    LegacyMappingSlot.GlDescription,
                ]),
                LegacyPrescreenRuleId.BackdatedPosting => Set(
                [
                    LegacyMappingSlot.GlDocumentNumber,
                    LegacyMappingSlot.GlPostingDate,
                    LegacyMappingSlot.GlVoucherDate,
                ]),
                LegacyPrescreenRuleId.NonAuthorizedPreparer
                    or LegacyPrescreenRuleId.LowFrequencyPreparer => Set(
                [
                    LegacyMappingSlot.GlDocumentNumber,
                    LegacyMappingSlot.GlPostingDate,
                    LegacyMappingSlot.GlCreatedBy,
                ]),
                LegacyPrescreenRuleId.LowFrequencyAccount => Set(
                [
                    LegacyMappingSlot.GlDocumentNumber,
                    LegacyMappingSlot.GlPostingDate,
                    LegacyMappingSlot.GlAccountNumber,
                ]),
                _ => throw new ArgumentOutOfRangeException(nameof(metricId)),
            };
        }

        if (metricId.StartsWith("filter.scenario.", StringComparison.Ordinal))
        {
            return AllGlSlots;
        }

        if (LegacyAuditParityReportMetrics.TryGetIdentity(metricId, out var reportMetric))
        {
            return ReportSlots(reportMetric);
        }

        throw new InvalidOperationException($"Parity metric dependency map is missing: {metricId}.");
    }

    private static IReadOnlySet<LegacyMappingSlot> Empty() =>
        new HashSet<LegacyMappingSlot>();

    private static IReadOnlySet<LegacyMappingSlot> Set(IEnumerable<LegacyMappingSlot> slots) =>
        slots.ToHashSet();

    private static IReadOnlySet<LegacyMappingSlot> ReportSlots(
        LegacyAuditParityReportMetricIdentity metric)
    {
        // Report parity observes logical sheet count and data-row count. A field that is
        // merely displayed, sorted, or styled is not a dependency unless it can change one
        // of those values. Fixed single-sheet families therefore have no sheet-count mapping
        // dependency even when their data rows are mapping-derived.
        if (metric.Kind == LegacyAuditParityReportMetricKind.SheetCount
            && IsFixedSheetCount(metric.Family))
        {
            return Empty();
        }

        return metric.Family switch
        {
            LegacyAuditParityReportFamily.ValidationSummary
                or LegacyAuditParityReportFamily.ValidationDifferenceGuidance
                or LegacyAuditParityReportFamily.AccountMappingList
                or LegacyAuditParityReportFamily.PrescreenSummary
                or LegacyAuditParityReportFamily.CriteriaSummary
                or LegacyAuditParityReportFamily.WorkingPaperCover
                or LegacyAuditParityReportFamily.WorkingPaperIntro
                or LegacyAuditParityReportFamily.WorkingPaperRiskConditionSummary
                or LegacyAuditParityReportFamily.WorkingPaperPostCloseAdjustments
                or LegacyAuditParityReportFamily.WorkingPaperCalendarInfo
                or LegacyAuditParityReportFamily.WorkingPaperAccountMapping => Empty(),

            LegacyAuditParityReportFamily.ValidationFieldInfo
                or LegacyAuditParityReportFamily.WorkingPaperFieldInfo =>
                    Set([LegacyMappingSlot.GlLineIdentifier]),

            LegacyAuditParityReportFamily.ValidationCompletenessDifferenceDetail
                or LegacyAuditParityReportFamily.WorkingPaperCompletenessDifference =>
                    CompletenessDifferenceSlots(),

            LegacyAuditParityReportFamily.ValidationNullAccountDetail => Set(
            [
                LegacyMappingSlot.GlPostingDate,
                LegacyMappingSlot.GlAccountNumber,
            ]),
            LegacyAuditParityReportFamily.ValidationNullDocumentDetail => Set(
            [
                LegacyMappingSlot.GlPostingDate,
                LegacyMappingSlot.GlDocumentNumber,
            ]),
            LegacyAuditParityReportFamily.ValidationNullDescriptionDetail => Set(
            [
                LegacyMappingSlot.GlPostingDate,
                LegacyMappingSlot.GlDescription,
            ]),
            LegacyAuditParityReportFamily.ValidationOutOfRangeApprovalDateDetail => Set(
            [
                LegacyMappingSlot.GlPostingDate,
                LegacyMappingSlot.GlApprovalDate,
            ]),
            LegacyAuditParityReportFamily.ValidationCompletenessReconciliation
                or LegacyAuditParityReportFamily.WorkingPaperCompleteness =>
                    CompletenessPopulationSlots(),
            LegacyAuditParityReportFamily.ValidationUnbalancedGlDetail
                or LegacyAuditParityReportFamily.WorkingPaperUnbalancedVouchers =>
                    UnbalancedVoucherSlots(),
            LegacyAuditParityReportFamily.ValidationBlankPostDateDetail
                or LegacyAuditParityReportFamily.ValidationSourceQuality =>
                Set([LegacyMappingSlot.GlPostingDate]),

            LegacyAuditParityReportFamily.AccountMappingMain => Set(
            [
                LegacyMappingSlot.GlPostingDate,
                LegacyMappingSlot.GlAccountNumber,
                LegacyMappingSlot.GlAccountName,
                LegacyMappingSlot.TbAccountNumber,
                LegacyMappingSlot.TbAccountName,
            ]),

            LegacyAuditParityReportFamily.InfReliability
                or LegacyAuditParityReportFamily.InfAllFields
                or LegacyAuditParityReportFamily.WorkingPaperReliability =>
                    Set([LegacyMappingSlot.GlPostingDate]),

            LegacyAuditParityReportFamily.PrescreenPostPeriodApprovalDetail =>
                PrescreenSlots(LegacyPrescreenRuleId.PostPeriodApproval),
            LegacyAuditParityReportFamily.PrescreenSuspiciousKeywordsDetail =>
                PrescreenSlots(LegacyPrescreenRuleId.SuspiciousKeywords),
            LegacyAuditParityReportFamily.PrescreenUnexpectedAccountPairDetail =>
                PrescreenSlots(LegacyPrescreenRuleId.UnexpectedAccountPair),
            LegacyAuditParityReportFamily.PrescreenTrailingZerosDetail =>
                PrescreenSlots(LegacyPrescreenRuleId.TrailingZeros),
            LegacyAuditParityReportFamily.PrescreenCreatorSummaryDetail
                or LegacyAuditParityReportFamily.WorkingPaperCreatorSummary => Set(
            [
                LegacyMappingSlot.GlPostingDate,
                LegacyMappingSlot.GlCreatedBy,
            ]),
            LegacyAuditParityReportFamily.PrescreenRareAccountsDetail => Set(
            [
                LegacyMappingSlot.GlPostingDate,
                LegacyMappingSlot.GlAccountNumber,
            ]),
            LegacyAuditParityReportFamily.PrescreenBlankDescriptionDetail =>
                PrescreenSlots(LegacyPrescreenRuleId.BlankDescription),

            LegacyAuditParityReportFamily.CriteriaScenario001
                or LegacyAuditParityReportFamily.CriteriaScenario002
                or LegacyAuditParityReportFamily.CriteriaScenario003
                or LegacyAuditParityReportFamily.CriteriaScenario004
                or LegacyAuditParityReportFamily.CriteriaScenario005
                or LegacyAuditParityReportFamily.CriteriaScenario006
                or LegacyAuditParityReportFamily.CriteriaScenario007
                or LegacyAuditParityReportFamily.CriteriaScenario008
                or LegacyAuditParityReportFamily.CriteriaScenario009
                or LegacyAuditParityReportFamily.CriteriaScenario010
                or LegacyAuditParityReportFamily.WorkingPaperRiskVoucherMatrix
                or LegacyAuditParityReportFamily.WorkingPaperRiskVoucherDetail =>
                    // Persisted criteria ASTs may read any GL filter field. The observation
                    // artifact stores typed counts rather than free-form AST text, so the
                    // closed dependency domain retains every captured filterable GL slot.
                    FilterScenarioSlots(),

            _ => throw new ArgumentOutOfRangeException(nameof(metric)),
        };
    }

    private static bool IsFixedSheetCount(LegacyAuditParityReportFamily family) => family is
        LegacyAuditParityReportFamily.ValidationSummary
        or LegacyAuditParityReportFamily.ValidationFieldInfo
        or LegacyAuditParityReportFamily.ValidationDifferenceGuidance
        or LegacyAuditParityReportFamily.AccountMappingMain
        or LegacyAuditParityReportFamily.AccountMappingList
        or LegacyAuditParityReportFamily.InfReliability
        or LegacyAuditParityReportFamily.InfAllFields
        or LegacyAuditParityReportFamily.PrescreenSummary
        or LegacyAuditParityReportFamily.CriteriaSummary
        or LegacyAuditParityReportFamily.WorkingPaperCover
        or LegacyAuditParityReportFamily.WorkingPaperIntro
        or LegacyAuditParityReportFamily.WorkingPaperReliability
        or LegacyAuditParityReportFamily.WorkingPaperRiskConditionSummary
        or LegacyAuditParityReportFamily.WorkingPaperPostCloseAdjustments
        or LegacyAuditParityReportFamily.WorkingPaperFieldInfo;

    private static IReadOnlySet<LegacyMappingSlot> CompletenessDifferenceSlots() => Set(
    [
        LegacyMappingSlot.GlPostingDate,
        LegacyMappingSlot.GlAccountNumber,
        LegacyMappingSlot.TbAccountNumber,
        .. GlAmountSlots,
        .. TbAmountSlots,
    ]);

    private static IReadOnlySet<LegacyMappingSlot> CompletenessPopulationSlots() => Set(
    [
        LegacyMappingSlot.GlPostingDate,
        LegacyMappingSlot.GlAccountNumber,
        LegacyMappingSlot.TbAccountNumber,
    ]);

    private static IReadOnlySet<LegacyMappingSlot> UnbalancedVoucherSlots() => Set(
    [
        LegacyMappingSlot.GlDocumentNumber,
        LegacyMappingSlot.GlPostingDate,
        .. GlAmountSlots,
    ]);

    private static IReadOnlySet<LegacyMappingSlot> FilterScenarioSlots() => Set(
    [
        LegacyMappingSlot.GlDocumentNumber,
        LegacyMappingSlot.GlLineIdentifier,
        LegacyMappingSlot.GlPostingDate,
        LegacyMappingSlot.GlApprovalDate,
        LegacyMappingSlot.GlVoucherDate,
        LegacyMappingSlot.GlAccountNumber,
        LegacyMappingSlot.GlAccountName,
        LegacyMappingSlot.GlDescription,
        LegacyMappingSlot.GlSource,
        LegacyMappingSlot.GlCreatedBy,
        LegacyMappingSlot.GlApprovedBy,
        .. GlAmountSlots,
    ]);

    private static IReadOnlySet<LegacyMappingSlot> PrescreenSlots(
        LegacyPrescreenRuleId rule) => rule switch
    {
        LegacyPrescreenRuleId.PostPeriodApproval => Set(
        [
            LegacyMappingSlot.GlPostingDate,
            LegacyMappingSlot.GlApprovalDate,
        ]),
        LegacyPrescreenRuleId.SuspiciousKeywords => Set(
        [
            LegacyMappingSlot.GlPostingDate,
            LegacyMappingSlot.GlDescription,
        ]),
        LegacyPrescreenRuleId.UnexpectedAccountPair => Set(
        [
            LegacyMappingSlot.GlDocumentNumber,
            LegacyMappingSlot.GlPostingDate,
            LegacyMappingSlot.GlAccountNumber,
            .. GlAmountSlots,
        ]),
        LegacyPrescreenRuleId.TrailingZeros => Set(
        [
            LegacyMappingSlot.GlPostingDate,
            .. GlAmountSlots,
        ]),
        LegacyPrescreenRuleId.BlankDescription => Set(
        [
            LegacyMappingSlot.GlPostingDate,
            LegacyMappingSlot.GlDescription,
        ]),
        _ => throw new ArgumentOutOfRangeException(nameof(rule)),
    };
}
