using System.Collections.ObjectModel;
using System.Text.Json;

namespace JET.Tests.Infrastructure;

internal enum LegacyAuditParityDifferenceCategory
{
    Unclassified = 0,
    JetDefect,
    IntentionalDecision,
    LegacyDefectOrInputCondition,
    LegacyNoCounterpart,
    UserDecisionRequired,
}

internal enum LegacyAuditParityCitation
{
    None = 0,
    JetGuideSection2,
    JetGuideSection3,
    JetGuideSection4,
    JetGuideSection5,
    JetGuideSection6,
    JetGuideSection7,
    ActionContractValidation,
    ActionContractPrescreen,
    ActionContractFilter,
    ActionContractExports,
    LegacyIdeaScript,
    LegacyIdeaModule,
    ParityPlanClassificationRules,
    ParityPlanProviderConsistency,
}

internal enum LegacyAuditParityComparisonId
{
    LegacyInternal,
    LegacyVsSqlite,
    LegacyVsDuckDb,
    LegacyVsSqlServer,
    SqliteVsDuckDb,
    SqliteVsSqlServer,
    DuckDbVsSqlServer,
}

internal enum LegacyAuditParityBasis
{
    None = 0,
    JetRequirementMismatch,
    ProviderInconsistency,
    ApprovedIntentionalDifference,
    LegacyBehaviorDefect,
    InputProfileCondition,
    LegacyNoComparableItem,
    RepositoryEvidenceInsufficient,
}

internal enum LegacyAuditParityHarnessBindingVerification
{
    NotRequired = 0,
    Unverified,
    Verified,
}

internal static class LegacyAuditParityComparisonIds
{
    internal static LegacyAuditParityComparisonId LegacyToProvider(LegacyAuditParityProvider provider) =>
        provider switch
        {
            LegacyAuditParityProvider.Sqlite => LegacyAuditParityComparisonId.LegacyVsSqlite,
            LegacyAuditParityProvider.DuckDb => LegacyAuditParityComparisonId.LegacyVsDuckDb,
            LegacyAuditParityProvider.SqlServer => LegacyAuditParityComparisonId.LegacyVsSqlServer,
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

    internal static LegacyAuditParityComparisonId ProviderPair(
        LegacyAuditParityProvider left,
        LegacyAuditParityProvider right)
    {
        if (!Enum.IsDefined(left) || !Enum.IsDefined(right) || left == right)
        {
            throw new ArgumentException("Provider comparison identity requires two distinct known providers.");
        }

        var first = (int)left < (int)right ? left : right;
        var second = (int)left < (int)right ? right : left;
        return (first, second) switch
        {
            (LegacyAuditParityProvider.Sqlite, LegacyAuditParityProvider.DuckDb) =>
                LegacyAuditParityComparisonId.SqliteVsDuckDb,
            (LegacyAuditParityProvider.Sqlite, LegacyAuditParityProvider.SqlServer) =>
                LegacyAuditParityComparisonId.SqliteVsSqlServer,
            (LegacyAuditParityProvider.DuckDb, LegacyAuditParityProvider.SqlServer) =>
                LegacyAuditParityComparisonId.DuckDbVsSqlServer,
            _ => throw new ArgumentOutOfRangeException(nameof(right)),
        };
    }

    internal static string FixedId(this LegacyAuditParityComparisonId comparison) => comparison switch
    {
        LegacyAuditParityComparisonId.LegacyInternal => "legacy-internal",
        LegacyAuditParityComparisonId.LegacyVsSqlite => "legacy-vs-sqlite",
        LegacyAuditParityComparisonId.LegacyVsDuckDb => "legacy-vs-duckdb",
        LegacyAuditParityComparisonId.LegacyVsSqlServer => "legacy-vs-sqlserver",
        LegacyAuditParityComparisonId.SqliteVsDuckDb => "sqlite-vs-duckdb",
        LegacyAuditParityComparisonId.SqliteVsSqlServer => "sqlite-vs-sqlserver",
        LegacyAuditParityComparisonId.DuckDbVsSqlServer => "duckdb-vs-sqlserver",
        _ => throw new ArgumentOutOfRangeException(nameof(comparison)),
    };
}

internal static class LegacyAuditParityBases
{
    internal static string FixedId(this LegacyAuditParityBasis basis) => basis switch
    {
        LegacyAuditParityBasis.JetRequirementMismatch => "jet-requirement-mismatch",
        LegacyAuditParityBasis.ProviderInconsistency => "provider-inconsistency",
        LegacyAuditParityBasis.ApprovedIntentionalDifference => "approved-intentional-difference",
        LegacyAuditParityBasis.LegacyBehaviorDefect => "legacy-behavior-defect",
        LegacyAuditParityBasis.InputProfileCondition => "input-profile-condition",
        LegacyAuditParityBasis.LegacyNoComparableItem => "legacy-no-comparable-item",
        LegacyAuditParityBasis.RepositoryEvidenceInsufficient => "repository-evidence-insufficient",
        _ => throw new ArgumentOutOfRangeException(nameof(basis)),
    };
}

internal static class LegacyAuditParityMetricIds
{
    internal const string CompletenessDifferenceAccountCount =
        "validation.completeness.diff_account_count";
    internal const string PartASourceRowCount = "validation.completeness.part_a.source_row_count";
    internal const string PartATargetRowCount = "validation.completeness.part_a.target_row_count";
    internal const string PartATotalDebit = "validation.completeness.part_a.total_debit";
    internal const string PartATotalCredit = "validation.completeness.part_a.total_credit";
    internal const string PartARowCountMatch = "validation.completeness.part_a.row_count_match";
    internal const string PartAAmountMatch = "validation.completeness.part_a.amount_match";
    internal const string UnbalancedVoucherCount = "validation.doc_balance.unbalanced_voucher_count";
    internal const string InfSampleSize = "validation.inf.sample_size";
    internal const string InfMemberKeyMultiset = "validation.inf.member_key_multiset";
    internal const string FilterScenarioCount = "filter.scenario_count";
    internal const string WeekendUnionRowCount = "prescreen.weekend_union.row_count";
    internal const string WeekendUnionVoucherCount = "prescreen.weekend_union.voucher_count";
    internal const string CreatorSummaryNotApplicable = "prescreen.creator_summary.not_applicable";
    internal const string RareAccountsNotApplicable = "prescreen.rare_accounts.not_applicable";

    private static readonly IReadOnlySet<string> FixedIds = BuildFixedIds();

    internal static string PrescreenRowCount(LegacyPrescreenRuleId rule) =>
        $"prescreen.{PrescreenStem(rule)}.row_count";

    internal static string PrescreenVoucherCount(LegacyPrescreenRuleId rule) =>
        $"prescreen.{PrescreenStem(rule)}.voucher_count";

    internal static string FilterRowCount(LegacyFilterScenarioId scenario) =>
        scenario.IsValid
            ? $"filter.scenario.{scenario.Ordinal:000}.row_count"
            : throw new ArgumentOutOfRangeException(nameof(scenario));

    internal static string FilterVoucherCount(LegacyFilterScenarioId scenario) =>
        scenario.IsValid
            ? $"filter.scenario.{scenario.Ordinal:000}.voucher_count"
            : throw new ArgumentOutOfRangeException(nameof(scenario));

    internal static string FilterLegacySummaryDetailRowCount(LegacyFilterScenarioId scenario) =>
        scenario.IsValid
            ? $"filter.scenario.{scenario.Ordinal:000}.legacy_summary_detail_row_count"
            : throw new ArgumentOutOfRangeException(nameof(scenario));

    internal static bool IsFilterLegacySummaryDetailRowCount(string metricId) =>
        TryParseFilterMetric(metricId, out var leaf)
        && leaf == "legacy_summary_detail_row_count";

    internal static string ReportSheetList(LegacyReportKind report) =>
        $"report.{ReportStem(report)}.sheet_list";

    internal static string ReportDataRowCounts(LegacyReportKind report) =>
        $"report.{ReportStem(report)}.data_row_counts";

    internal static bool IsFixed(string metricId)
    {
        if (FixedIds.Contains(metricId)
            || LegacyAuditParityReportMetrics.IsFixed(metricId))
        {
            return true;
        }

        return TryParseFilterMetric(metricId, out var leaf)
            && leaf is "row_count" or "voucher_count" or "legacy_summary_detail_row_count";
    }

    private static bool TryParseFilterMetric(string metricId, out string leaf)
    {
        var parts = metricId.Split('.', StringSplitOptions.None);
        leaf = parts.Length == 4 ? parts[3] : string.Empty;
        return parts.Length == 4
            && parts[0] == "filter"
            && parts[1] == "scenario"
            && parts[2].Length == 3
            && int.TryParse(parts[2], out var ordinal)
            && ordinal is >= 1 and <= 999;
    }

    private static IReadOnlySet<string> BuildFixedIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal)
        {
            CompletenessDifferenceAccountCount,
            PartASourceRowCount,
            PartATargetRowCount,
            PartATotalDebit,
            PartATotalCredit,
            PartARowCountMatch,
            PartAAmountMatch,
            UnbalancedVoucherCount,
            InfSampleSize,
            InfMemberKeyMultiset,
            FilterScenarioCount,
            WeekendUnionRowCount,
            WeekendUnionVoucherCount,
            CreatorSummaryNotApplicable,
            RareAccountsNotApplicable,
        };
        foreach (var rule in LegacyPrescreenRuleCatalog.All)
        {
            ids.Add(PrescreenRowCount(rule));
            ids.Add(PrescreenVoucherCount(rule));
        }
        foreach (var metricId in LegacyAuditParityReportMetrics.FixedMetricIds)
        {
            ids.Add(metricId);
        }
        return ids;
    }

    private static string PrescreenStem(LegacyPrescreenRuleId rule) => rule switch
    {
        LegacyPrescreenRuleId.PostPeriodApproval => "post_period_approval",
        LegacyPrescreenRuleId.SuspiciousKeywords => "suspicious_keywords",
        LegacyPrescreenRuleId.UnexpectedAccountPair => "unexpected_account_pair",
        LegacyPrescreenRuleId.TrailingZeros => "trailing_zeros",
        LegacyPrescreenRuleId.WeekendPosting => "weekend_posting",
        LegacyPrescreenRuleId.WeekendApproval => "weekend_approval",
        LegacyPrescreenRuleId.HolidayPosting => "holiday_posting",
        LegacyPrescreenRuleId.HolidayApproval => "holiday_approval",
        LegacyPrescreenRuleId.BlankDescription => "blank_description",
        LegacyPrescreenRuleId.BackdatedPosting => "backdated_posting",
        LegacyPrescreenRuleId.NonAuthorizedPreparer => "non_authorized_preparer",
        LegacyPrescreenRuleId.LowFrequencyPreparer => "low_frequency_preparer",
        LegacyPrescreenRuleId.LowFrequencyAccount => "low_frequency_account",
        _ => throw new ArgumentOutOfRangeException(nameof(rule)),
    };

    private static string ReportStem(LegacyReportKind report) => report switch
    {
        LegacyReportKind.ValidationReport => "validation_report",
        LegacyReportKind.AccountMapping => "account_mapping",
        LegacyReportKind.InfReport => "inf_report",
        LegacyReportKind.PrescreenReport => "prescreen_report",
        LegacyReportKind.CriteriaSelectionReport => "criteria_selection_report",
        LegacyReportKind.WorkingPaper => "working_paper",
        _ => throw new ArgumentOutOfRangeException(nameof(report)),
    };
}

internal static class LegacyPrescreenParityPlan
{
    internal static IReadOnlyList<LegacyPrescreenRuleId> DirectRules { get; } =
    [
        LegacyPrescreenRuleId.PostPeriodApproval,
        LegacyPrescreenRuleId.SuspiciousKeywords,
        LegacyPrescreenRuleId.UnexpectedAccountPair,
        LegacyPrescreenRuleId.TrailingZeros,
        LegacyPrescreenRuleId.BlankDescription,
    ];

    // R5/R6 are aggregate legacy views. Their E/F cells are fixed N/A and are not
    // numeric comparison participants.
    internal static IReadOnlyList<string> LegacyNotApplicableMetricIds { get; } =
    [
        LegacyAuditParityMetricIds.CreatorSummaryNotApplicable,
        LegacyAuditParityMetricIds.RareAccountsNotApplicable,
    ];

    internal static IReadOnlyList<LegacyPrescreenRuleId> LegacyNoCounterpartRules { get; } =
    [
        LegacyPrescreenRuleId.HolidayPosting,
        LegacyPrescreenRuleId.HolidayApproval,
        LegacyPrescreenRuleId.BackdatedPosting,
        LegacyPrescreenRuleId.NonAuthorizedPreparer,
        LegacyPrescreenRuleId.LowFrequencyPreparer,
        LegacyPrescreenRuleId.LowFrequencyAccount,
    ];

    // The legacy Step 3 prescreen summary has no weekend-union oracle. R7 is the
    // blank-description rule; weekend criteria belong to a later workflow step.
    internal static IReadOnlyList<string> LegacyNoCounterpartWeekendMetricIds { get; } =
    [
        LegacyAuditParityMetricIds.WeekendUnionRowCount,
        LegacyAuditParityMetricIds.WeekendUnionVoucherCount,
    ];

    internal static IReadOnlyList<string> LegacyNoCounterpartControlMetricIds { get; } =
    [
        LegacyAuditParityMetricIds.PartASourceRowCount,
        LegacyAuditParityMetricIds.PartARowCountMatch,
        LegacyAuditParityMetricIds.PartAAmountMatch,
    ];

    internal static IEnumerable<string> LegacyNoCounterpartMetricIds =>
        LegacyNoCounterpartControlMetricIds.Concat(
            LegacyNoCounterpartRules.SelectMany(rule => new[]
            {
                LegacyAuditParityMetricIds.PrescreenRowCount(rule),
                LegacyAuditParityMetricIds.PrescreenVoucherCount(rule),
            }))
            .Concat(LegacyNoCounterpartWeekendMetricIds);
}

internal sealed class LegacyAuditParityClassification
{
    internal LegacyAuditParityClassification(
        LegacyParityCase @case,
        string metricId,
        LegacyAuditParityDifferenceCategory category,
        LegacyAuditParityCitation citation,
        LegacyAuditParityBasis basis,
        IEnumerable<LegacyAuditParityMappingProvenanceEntry> mappingDependencies,
        LegacyAuditParityHarnessBindingVerification harnessBindingVerification)
    {
        var provenance = new LegacyAuditParityMappingProvenance(mappingDependencies);
        LegacyAuditParityDifferenceValidator.ValidateClassification(
            @case,
            metricId,
            category,
            citation,
            basis,
            provenance,
            harnessBindingVerification);
        Case = @case;
        MetricId = metricId;
        Category = category;
        Citation = citation;
        Basis = basis;
        MappingDependencies = provenance.Entries;
        HarnessBindingVerification = harnessBindingVerification;
    }

    internal LegacyParityCase Case { get; }

    internal string MetricId { get; }

    internal LegacyAuditParityDifferenceCategory Category { get; }

    internal LegacyAuditParityCitation Citation { get; }

    internal LegacyAuditParityBasis Basis { get; }

    internal IReadOnlyList<LegacyAuditParityMappingProvenanceEntry> MappingDependencies { get; }

    internal LegacyAuditParityHarnessBindingVerification HarnessBindingVerification { get; }

    public override string ToString() =>
        $"{LegacyAuditParitySafeNames.CaseAlias(Case)}|{MetricId}|{Category}|{Citation}|{Basis.FixedId()}|"
        + HarnessBindingVerification;
}

internal sealed class LegacyAuditParityClassificationCatalog
{
    private readonly IReadOnlyDictionary<(LegacyParityCase Case, string MetricId), LegacyAuditParityClassification>
        _classifications;

    internal LegacyAuditParityClassificationCatalog(IEnumerable<LegacyAuditParityClassification> classifications)
    {
        ArgumentNullException.ThrowIfNull(classifications);
        var captured = new Dictionary<(LegacyParityCase, string), LegacyAuditParityClassification>();
        foreach (var classification in classifications)
        {
            ArgumentNullException.ThrowIfNull(classification);
            if (!captured.TryAdd((classification.Case, classification.MetricId), classification))
            {
                throw new ArgumentException("Parity classification catalog contains a duplicate fixed identity.");
            }
        }
        _classifications = new ReadOnlyDictionary<
            (LegacyParityCase Case, string MetricId),
            LegacyAuditParityClassification>(captured);
    }

    internal LegacyAuditParityClassification Resolve(LegacyParityCase @case, string metricId) =>
        _classifications.TryGetValue((@case, metricId), out var classification)
            ? classification
            : throw new InvalidOperationException(
                $"Parity mismatch is unclassified: {LegacyAuditParitySafeNames.CaseAlias(@case)}|{metricId}.");
}

internal sealed record LegacyAuditParityHarnessBindingVerificationEntry(
    LegacyParityCase Case,
    LegacyAuditParityComparisonId ComparisonId,
    string MetricId,
    LegacyAuditParityHarnessBindingVerification Verification);

internal sealed class LegacyAuditParityHarnessBindingVerificationCatalog
{
    private readonly IReadOnlyDictionary<
        (LegacyParityCase Case, LegacyAuditParityComparisonId ComparisonId, string MetricId),
        LegacyAuditParityHarnessBindingVerification> _verifications;

    internal LegacyAuditParityHarnessBindingVerificationCatalog(
        IEnumerable<LegacyAuditParityHarnessBindingVerificationEntry> verifications)
    {
        ArgumentNullException.ThrowIfNull(verifications);
        var captured = new Dictionary<
            (LegacyParityCase, LegacyAuditParityComparisonId, string),
            LegacyAuditParityHarnessBindingVerification>();
        foreach (var verification in verifications)
        {
            ArgumentNullException.ThrowIfNull(verification);
            if (!Enum.IsDefined(verification.Case)
                || !IsProviderComparison(verification.ComparisonId)
                || string.IsNullOrWhiteSpace(verification.MetricId)
                || !LegacyAuditParityMetricIds.IsFixed(verification.MetricId)
                || verification.Verification is not LegacyAuditParityHarnessBindingVerification.Unverified
                    and not LegacyAuditParityHarnessBindingVerification.Verified)
            {
                throw new ArgumentException(
                    "Harness-binding verification catalog requires a fixed provider-comparison identity.",
                    nameof(verifications));
            }
            if (!captured.TryAdd(
                    (verification.Case, verification.ComparisonId, verification.MetricId),
                    verification.Verification))
            {
                throw new ArgumentException(
                    "Harness-binding verification catalog contains a duplicate fixed identity.",
                    nameof(verifications));
            }
        }

        _verifications = new ReadOnlyDictionary<
            (LegacyParityCase Case, LegacyAuditParityComparisonId ComparisonId, string MetricId),
            LegacyAuditParityHarnessBindingVerification>(captured);
    }

    internal static LegacyAuditParityHarnessBindingVerificationCatalog Empty { get; } = new([]);

    internal LegacyAuditParityHarnessBindingVerification RequireVerified(
        LegacyParityCase @case,
        LegacyAuditParityComparisonId comparisonId,
        string metricId)
    {
        if (!_verifications.TryGetValue((@case, comparisonId, metricId), out var verification)
            || verification != LegacyAuditParityHarnessBindingVerification.Verified)
        {
            throw new LegacyAuditParityHarnessBindingVerificationRequiredException(
                @case,
                comparisonId,
                metricId);
        }

        return verification;
    }

    private static bool IsProviderComparison(LegacyAuditParityComparisonId comparisonId) =>
        comparisonId is LegacyAuditParityComparisonId.SqliteVsDuckDb
            or LegacyAuditParityComparisonId.SqliteVsSqlServer
            or LegacyAuditParityComparisonId.DuckDbVsSqlServer;
}

internal sealed class LegacyAuditParityDifference
{
    private LegacyAuditParityDifference(
        LegacyParityCase @case,
        LegacyAuditParityComparisonId comparisonId,
        string metricId,
        long? differenceCount,
        LegacyAuditParityDifferenceCategory category,
        LegacyAuditParityCitation citation,
        LegacyAuditParityBasis basis,
        IEnumerable<LegacyAuditParityMappingProvenanceEntry> mappingDependencies,
        LegacyAuditParityHarnessBindingVerification harnessBindingVerification,
        bool providerMismatch)
    {
        var provenance = new LegacyAuditParityMappingProvenance(mappingDependencies);
        LegacyAuditParityDifferenceValidator.ValidateDifference(
            @case,
            comparisonId,
            metricId,
            differenceCount,
            category,
            citation,
            basis,
            provenance,
            harnessBindingVerification);
        if (providerMismatch
            && (category != LegacyAuditParityDifferenceCategory.JetDefect
                || citation != LegacyAuditParityCitation.ParityPlanProviderConsistency
                || basis != LegacyAuditParityBasis.ProviderInconsistency))
        {
            throw new InvalidOperationException(
                "Provider mismatch must use the fixed JET-defect provider classification.");
        }

        Case = @case;
        ComparisonId = comparisonId;
        MetricId = metricId;
        DifferenceCount = differenceCount;
        Category = category;
        Citation = citation;
        Basis = basis;
        MappingDependencies = provenance.Entries;
        HarnessBindingVerification = harnessBindingVerification;
        IsProviderMismatch = providerMismatch;
    }

    internal LegacyParityCase Case { get; }

    internal LegacyAuditParityComparisonId ComparisonId { get; }

    internal string MetricId { get; }

    internal long? DifferenceCount { get; }

    internal LegacyAuditParityDifferenceCategory Category { get; }

    internal LegacyAuditParityCitation Citation { get; }

    internal LegacyAuditParityBasis Basis { get; }

    internal IReadOnlyList<LegacyAuditParityMappingProvenanceEntry> MappingDependencies { get; }

    internal LegacyAuditParityHarnessBindingVerification HarnessBindingVerification { get; }

    internal bool IsProviderMismatch { get; }

    internal static LegacyAuditParityDifference Classified(
        LegacyAuditParityClassification classification,
        LegacyAuditParityComparisonId comparisonId,
        long? differenceCount) => new(
            classification.Case,
            comparisonId,
            classification.MetricId,
            differenceCount,
            classification.Category,
            classification.Citation,
            classification.Basis,
            classification.MappingDependencies,
            classification.HarnessBindingVerification,
            providerMismatch: false);

    internal static LegacyAuditParityDifference ProviderMismatch(
        LegacyParityCase @case,
        LegacyAuditParityComparisonId comparisonId,
        string metricId,
        long differenceCount,
        IEnumerable<LegacyAuditParityMappingProvenanceEntry>? mappingDependencies = null,
        LegacyAuditParityHarnessBindingVerification harnessBindingVerification =
            LegacyAuditParityHarnessBindingVerification.NotRequired) => new(
            @case,
            comparisonId,
            metricId,
            differenceCount,
            LegacyAuditParityDifferenceCategory.JetDefect,
            LegacyAuditParityCitation.ParityPlanProviderConsistency,
            LegacyAuditParityBasis.ProviderInconsistency,
            mappingDependencies ?? [],
            harnessBindingVerification,
            providerMismatch: true);

    internal static LegacyAuditParityDifference NoLegacyCounterpart(
        LegacyAuditParityClassification classification,
        LegacyAuditParityComparisonId comparisonId)
    {
        ArgumentNullException.ThrowIfNull(classification);
        if (classification.Category != LegacyAuditParityDifferenceCategory.LegacyNoCounterpart
            || !LegacyPrescreenParityPlan.LegacyNoCounterpartMetricIds.Contains(
                classification.MetricId,
                StringComparer.Ordinal)
            || classification.MappingDependencies.Count != 0
            || classification.HarnessBindingVerification
                != LegacyAuditParityHarnessBindingVerification.NotRequired)
        {
            throw new ArgumentException(
                "Legacy-no-counterpart registration requires its fixed structural classification.",
                nameof(classification));
        }

        return new LegacyAuditParityDifference(
            classification.Case,
            comparisonId,
            classification.MetricId,
            differenceCount: null,
            classification.Category,
            classification.Citation,
            classification.Basis,
            classification.MappingDependencies,
            classification.HarnessBindingVerification,
            providerMismatch: false);
    }

    public override string ToString() =>
        $"{LegacyAuditParitySafeNames.CaseAlias(Case)}|{ComparisonId.FixedId()}|{MetricId}|"
        + $"{DifferenceCount?.ToString() ?? "not-comparable"}|{Category}|"
        + $"{Citation.RepositoryPath()}|{Basis.FixedId()}|{HarnessBindingVerification}";
}

internal sealed class LegacyAuditParityDifferenceRegistry
{
    private readonly IReadOnlyList<LegacyAuditParityDifference> _entries;

    internal LegacyAuditParityDifferenceRegistry(IEnumerable<LegacyAuditParityDifference> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var captured = entries
            .OrderBy(entry => entry.Case)
            .ThenBy(entry => entry.ComparisonId)
            .ThenBy(entry => entry.MetricId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Category)
            .ToArray();
        var identities = new HashSet<(LegacyParityCase, LegacyAuditParityComparisonId, string)>();
        foreach (var entry in captured)
        {
            ArgumentNullException.ThrowIfNull(entry);
            var provenance = new LegacyAuditParityMappingProvenance(entry.MappingDependencies);
            LegacyAuditParityDifferenceValidator.ValidateDifference(
                entry.Case,
                entry.ComparisonId,
                entry.MetricId,
                entry.DifferenceCount,
                entry.Category,
                entry.Citation,
                entry.Basis,
                provenance,
                entry.HarnessBindingVerification);
            if (entry.IsProviderMismatch
                && (entry.Category != LegacyAuditParityDifferenceCategory.JetDefect
                    || entry.Basis != LegacyAuditParityBasis.ProviderInconsistency))
            {
                throw new InvalidOperationException("Provider mismatch classification was not a JET defect.");
            }
            if (!identities.Add((entry.Case, entry.ComparisonId, entry.MetricId)))
            {
                throw new InvalidOperationException("Parity difference registry contains a duplicate fixed identity.");
            }
        }
        _entries = Array.AsReadOnly(captured);
    }

    internal IReadOnlyList<LegacyAuditParityDifference> Entries => _entries;

    internal void EnsureStageCanComplete()
    {
        var userDecisionCount = _entries.Count(
            entry => entry.Category == LegacyAuditParityDifferenceCategory.UserDecisionRequired);
        if (userDecisionCount > _entries.Count / 2)
        {
            throw new LegacyAuditParityWiringSuspectedException();
        }
        if (userDecisionCount > 0)
        {
            throw new LegacyAuditParityUserDecisionRequiredException();
        }
    }

    internal string Serialize() => JsonSerializer.Serialize(
        new
        {
            schemaVersion = "legacy-audit-parity-difference-registry/v4",
            differences = _entries.Select(entry => new
            {
                caseAlias = LegacyAuditParitySafeNames.CaseAlias(entry.Case),
                comparisonId = entry.ComparisonId.FixedId(),
                metricId = entry.MetricId,
                differenceCount = entry.DifferenceCount,
                category = entry.Category.ToString(),
                citation = entry.Citation.RepositoryPath(),
                basis = entry.Basis.FixedId(),
                mappingDependencies = entry.MappingDependencies.Select(dependency => new
                {
                    dataset = dependency.Dataset.ToString(),
                    slot = dependency.Slot.ToString(),
                    resolutionSource = dependency.ResolutionSource.ToString(),
                }),
                harnessBindingVerification = entry.HarnessBindingVerification.ToString(),
            }),
        },
        new JsonSerializerOptions { WriteIndented = true });

    internal string FormatSummary() => _entries.Count == 0
        ? "legacy audit parity: no mismatches"
        : string.Join(Environment.NewLine, _entries.Select(entry => entry.ToString()));

    public override string ToString() => FormatSummary();
}

internal sealed class LegacyAuditParityWiringSuspectedException : InvalidOperationException
{
    internal LegacyAuditParityWiringSuspectedException()
        : base("More than half of legacy parity registry entries require a user decision; comparison wiring is suspect.")
    {
    }
}

internal sealed class LegacyAuditParityHarnessBindingVerificationRequiredException : InvalidOperationException
{
    internal LegacyAuditParityHarnessBindingVerificationRequiredException(
        LegacyParityCase @case,
        LegacyAuditParityComparisonId comparisonId,
        string metricId)
        : base(
            "Provider parity mismatch depends on fallback mapping and requires explicit typed verification: "
            + $"{LegacyAuditParitySafeNames.CaseAlias(@case)}|{comparisonId.FixedId()}|{metricId}.")
    {
        Case = @case;
        ComparisonId = comparisonId;
        MetricId = metricId;
    }

    internal LegacyParityCase Case { get; }

    internal LegacyAuditParityComparisonId ComparisonId { get; }

    internal string MetricId { get; }
}

internal sealed class LegacyAuditParityUserDecisionRequiredException : InvalidOperationException
{
    internal LegacyAuditParityUserDecisionRequiredException()
        : base("Legacy audit parity contains user-decision-required differences; stage completion is blocked.")
    {
    }
}

internal sealed class LegacyAuditParityComparisonCompletenessException : InvalidOperationException
{
    internal LegacyAuditParityComparisonCompletenessException(string metricId)
        : base($"Legacy audit parity comparison metric is unavailable: {ValidateMetricId(metricId)}.")
    {
        MetricId = metricId;
    }

    internal string MetricId { get; }

    private static string ValidateMetricId(string metricId) =>
        LegacyAuditParityMetricIds.IsFixed(metricId)
            ? metricId
            : throw new ArgumentException("Comparison completeness requires a fixed metric id.", nameof(metricId));
}

internal static class LegacyAuditParityComparator
{
    internal static LegacyAuditParityDifferenceRegistry CompareLegacyInternal(
        LegacyAuditParityObservationArtifactBundle legacy,
        LegacyAuditParityClassificationCatalog classifications)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(classifications);
        if (!legacy.Observation.IsLegacy)
        {
            throw new ArgumentException("Legacy-internal comparison requires a legacy observation.");
        }

        var differences = MeasureLegacyInternal(legacy.Observation.Metrics)
            .Select(measurement =>
            {
                var classification = classifications.Resolve(
                    legacy.Observation.Case,
                    measurement.MetricId);
                LegacyAuditParityMetricDependencies.EnsureExact(
                    measurement.MetricId,
                    legacy.MappingProvenance,
                    classification.MappingDependencies);
                return LegacyAuditParityDifference.Classified(
                    classification,
                    LegacyAuditParityComparisonId.LegacyInternal,
                    measurement.DifferenceCount);
            });
        return new LegacyAuditParityDifferenceRegistry(differences);
    }

    internal static LegacyAuditParityDifferenceRegistry CompareLegacyToProvider(
        LegacyAuditParityObservation legacy,
        LegacyAuditParityObservation provider,
        LegacyAuditParityClassificationCatalog classifications) =>
        CompareLegacyToProviderCore(legacy, provider, classifications, capturedProvenance: null);

    internal static LegacyAuditParityDifferenceRegistry CompareLegacyToProvider(
        LegacyAuditParityObservationArtifactBundle legacy,
        LegacyAuditParityObservationArtifactBundle provider,
        LegacyAuditParityClassificationCatalog classifications)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(classifications);
        if (!legacy.MappingProvenance.Entries.SequenceEqual(provider.MappingProvenance.Entries))
        {
            throw new InvalidOperationException(
                "Legacy/provider observation artifacts did not capture identical typed mapping provenance.");
        }

        return CompareLegacyToProviderCore(
            legacy.Observation,
            provider.Observation,
            classifications,
            legacy.MappingProvenance);
    }

    private static LegacyAuditParityDifferenceRegistry CompareLegacyToProviderCore(
        LegacyAuditParityObservation legacy,
        LegacyAuditParityObservation provider,
        LegacyAuditParityClassificationCatalog classifications,
        LegacyAuditParityMappingProvenance? capturedProvenance)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(classifications);
        if (!legacy.IsLegacy || provider.IsLegacy || legacy.Case != provider.Case)
        {
            throw new ArgumentException(
                "Legacy comparison requires one legacy and one provider observation for the same case.");
        }

        EnsureLegacyAggregateApplicability(legacy.Metrics, provider.Metrics);
        var comparisonId = LegacyAuditParityComparisonIds.LegacyToProvider(provider.Provider!.Value);
        var differences = MeasureLegacyComparable(legacy.Metrics, provider.Metrics)
            .Select(measurement =>
            {
                var classification = classifications.Resolve(legacy.Case, measurement.MetricId);
                if (capturedProvenance is not null)
                {
                    LegacyAuditParityMetricDependencies.EnsureExact(
                        measurement.MetricId,
                        capturedProvenance,
                        classification.MappingDependencies);
                }
                return LegacyAuditParityDifference.Classified(
                    classification,
                    comparisonId,
                    measurement.DifferenceCount);
            })
            .Concat(LegacyPrescreenParityPlan.LegacyNoCounterpartMetricIds.Select(metricId =>
            {
                var classification = classifications.Resolve(legacy.Case, metricId);
                if (capturedProvenance is not null)
                {
                    LegacyAuditParityMetricDependencies.EnsureExact(
                        metricId,
                        capturedProvenance,
                        classification.MappingDependencies,
                        LegacyAuditParityMetricDependencyContext.LegacyNoCounterpartRegistration);
                }
                return LegacyAuditParityDifference.NoLegacyCounterpart(classification, comparisonId);
            }));
        return new LegacyAuditParityDifferenceRegistry(differences);
    }

    private static void EnsureLegacyAggregateApplicability(
        LegacyAuditParityMetrics legacy,
        LegacyAuditParityMetrics provider)
    {
        EnsureLegacyAggregateApplicability(
            LegacyAuditParityMetricIds.CreatorSummaryNotApplicable,
            legacy.CreatorSummaryLegacyApplicability,
            provider.CreatorSummaryLegacyApplicability);
        EnsureLegacyAggregateApplicability(
            LegacyAuditParityMetricIds.RareAccountsNotApplicable,
            legacy.RareAccountsLegacyApplicability,
            provider.RareAccountsLegacyApplicability);
    }

    private static void EnsureLegacyAggregateApplicability(
        string metricId,
        LegacyMetricApplicability legacy,
        LegacyMetricApplicability provider)
    {
        if (legacy != LegacyMetricApplicability.NotApplicable
            || provider != LegacyMetricApplicability.NotExecuted)
        {
            throw new LegacyAuditParityComparisonCompletenessException(metricId);
        }
    }

    internal static LegacyAuditParityDifferenceRegistry CompareProviders(
        IReadOnlyList<LegacyAuditParityObservation> providers) =>
        CompareProvidersCore(
            providers,
            capturedProvenance: null,
            harnessBindingVerifications: null);

    internal static LegacyAuditParityDifferenceRegistry CompareProviders(
        IReadOnlyList<LegacyAuditParityObservationArtifactBundle> providers) =>
        CompareProviders(
            providers,
            LegacyAuditParityHarnessBindingVerificationCatalog.Empty);

    internal static LegacyAuditParityDifferenceRegistry CompareProviders(
        IReadOnlyList<LegacyAuditParityObservationArtifactBundle> providers,
        LegacyAuditParityHarnessBindingVerificationCatalog harnessBindingVerifications)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(harnessBindingVerifications);
        if (providers.Count == 0 || providers.Any(provider => provider is null))
        {
            throw new ArgumentException("Provider artifact comparison requires typed observations.", nameof(providers));
        }
        var capturedProvenance = providers[0].MappingProvenance;
        if (providers.Skip(1).Any(provider =>
                !provider.MappingProvenance.Entries.SequenceEqual(capturedProvenance.Entries)))
        {
            throw new InvalidOperationException(
                "Provider observation artifacts did not capture identical typed mapping provenance.");
        }
        return CompareProvidersCore(
            providers.Select(provider => provider.Observation).ToArray(),
            capturedProvenance,
            harnessBindingVerifications);
    }

    private static LegacyAuditParityDifferenceRegistry CompareProvidersCore(
        IReadOnlyList<LegacyAuditParityObservation> providers,
        LegacyAuditParityMappingProvenance? capturedProvenance,
        LegacyAuditParityHarnessBindingVerificationCatalog? harnessBindingVerifications)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (providers.Count < 2
            || providers.Any(observation => observation.IsLegacy)
            || providers.Select(observation => observation.Case).Distinct().Count() != 1
            || providers.Select(observation => observation.Provider).Distinct().Count() != providers.Count)
        {
            throw new ArgumentException(
                "Provider comparison requires at least two unique providers for one case.",
                nameof(providers));
        }

        var ordered = providers.OrderBy(observation => observation.Provider).ToArray();
        var differences = new List<LegacyAuditParityDifference>();
        for (var left = 0; left < ordered.Length; left++)
        {
            for (var right = left + 1; right < ordered.Length; right++)
            {
                var comparisonId = LegacyAuditParityComparisonIds.ProviderPair(
                    ordered[left].Provider!.Value,
                    ordered[right].Provider!.Value);
                EnsureProviderComparisonAvailable(ordered[left].Metrics, ordered[right].Metrics);
                foreach (var measurement in MeasureProviders(ordered[left].Metrics, ordered[right].Metrics))
                {
                    var dependencies = capturedProvenance is null
                        ? []
                        : LegacyAuditParityMetricDependencies.Resolve(
                            measurement.MetricId,
                            capturedProvenance);
                    var usesFallback = dependencies.Any(dependency =>
                        dependency.ResolutionSource
                            != LegacyMappingResolutionSource.WorkingPaperExplicitMapping);
                    var verification = usesFallback
                        ? (harnessBindingVerifications
                            ?? throw new InvalidOperationException(
                                "Artifact provider comparison requires typed harness-binding verification."))
                            .RequireVerified(
                                ordered[left].Case,
                                comparisonId,
                                measurement.MetricId)
                        : LegacyAuditParityHarnessBindingVerification.NotRequired;
                    differences.Add(LegacyAuditParityDifference.ProviderMismatch(
                        ordered[left].Case,
                        comparisonId,
                        measurement.MetricId,
                        measurement.DifferenceCount!.Value,
                        dependencies,
                        verification));
                }
            }
        }

        return new LegacyAuditParityDifferenceRegistry(differences);
    }

    private static void EnsureProviderComparisonAvailable(
        LegacyAuditParityMetrics left,
        LegacyAuditParityMetrics right)
    {
        RequireAvailable(
            LegacyAuditParityMetricIds.PartASourceRowCount,
            left.Completeness.PartASourceRowCount,
            right.Completeness.PartASourceRowCount);
        RequireAvailable(
            LegacyAuditParityMetricIds.PartATargetRowCount,
            left.Completeness.PartATargetRowCount,
            right.Completeness.PartATargetRowCount);
        RequireAvailable(
            LegacyAuditParityMetricIds.PartATotalDebit,
            left.Completeness.PartATotalDebit,
            right.Completeness.PartATotalDebit);
        RequireAvailable(
            LegacyAuditParityMetricIds.PartATotalCredit,
            left.Completeness.PartATotalCredit,
            right.Completeness.PartATotalCredit);
        RequireAvailable(
            LegacyAuditParityMetricIds.PartARowCountMatch,
            left.Completeness.PartARowCountMatch,
            right.Completeness.PartARowCountMatch);
        RequireAvailable(
            LegacyAuditParityMetricIds.PartAAmountMatch,
            left.Completeness.PartAAmountMatch,
            right.Completeness.PartAAmountMatch);
        foreach (var rule in LegacyPrescreenRuleCatalog.All)
        {
            RequireRuleAvailable(
                LegacyAuditParityMetricIds.PrescreenRowCount(rule),
                LegacyAuditParityMetricIds.PrescreenVoucherCount(rule),
                left.PrescreenRules[rule],
                right.PrescreenRules[rule]);
        }
        RequireRuleAvailable(
            LegacyAuditParityMetricIds.WeekendUnionRowCount,
            LegacyAuditParityMetricIds.WeekendUnionVoucherCount,
            left.WeekendUnion,
            right.WeekendUnion);
    }

    private static void RequireAvailable(
        string metricId,
        LegacyObservedCount left,
        LegacyObservedCount right)
    {
        _ = left.RequireValue(metricId);
        _ = right.RequireValue(metricId);
    }

    private static void RequireRuleAvailable(
        string rowMetricId,
        string voucherMetricId,
        LegacyRowVoucherCounts left,
        LegacyRowVoucherCounts right)
    {
        if (left.Applicability == LegacyMetricApplicability.NotExecuted
            || right.Applicability == LegacyMetricApplicability.NotExecuted)
        {
            throw new LegacyAuditParityComparisonCompletenessException(rowMetricId);
        }
        if (left.Applicability == LegacyMetricApplicability.Applicable)
        {
            _ = left.RowCount.RequireValue(rowMetricId);
            _ = left.VoucherCount.RequireValue(voucherMetricId);
        }
        if (right.Applicability == LegacyMetricApplicability.Applicable)
        {
            _ = right.RowCount.RequireValue(rowMetricId);
            _ = right.VoucherCount.RequireValue(voucherMetricId);
        }
    }

    private static void RequireAvailable(
        string metricId,
        LegacyObservedAmountFingerprint left,
        LegacyObservedAmountFingerprint right)
    {
        _ = left.RequireFingerprint(metricId);
        _ = right.RequireFingerprint(metricId);
    }

    private static void RequireAvailable(
        string metricId,
        LegacyObservedBoolean left,
        LegacyObservedBoolean right)
    {
        _ = left.RequireValue(metricId);
        _ = right.RequireValue(metricId);
    }

    private static void RequireAvailable(string metricId, object? left, object? right)
    {
        if (left is null || right is null)
        {
            throw new LegacyAuditParityComparisonCompletenessException(metricId);
        }
    }

    private static IEnumerable<Measurement> MeasureLegacyComparable(
        LegacyAuditParityMetrics left,
        LegacyAuditParityMetrics right)
    {
        foreach (var measurement in MeasureShared(left, right))
        {
            yield return measurement;
        }

        foreach (var rule in LegacyPrescreenParityPlan.DirectRules)
        {
            foreach (var measurement in MeasureRule(left, right, rule, failOnUnavailable: false))
            {
                yield return measurement;
            }
        }

        foreach (var measurement in MeasureFiltersAndReports(
                     left,
                     right,
                     failOnUnavailable: false))
        {
            yield return measurement;
        }
    }

    private static IEnumerable<Measurement> MeasureLegacyInternal(
        LegacyAuditParityMetrics legacy)
    {
        foreach (var (scenario, counts) in legacy.FilterScenarios.OrderBy(pair => pair.Key.Ordinal))
        {
            var metricId = LegacyAuditParityMetricIds.FilterLegacySummaryDetailRowCount(scenario);
            var summary = counts.RowCount.RequireValue(metricId);
            var detail = legacy.Reports[LegacyReportKind.CriteriaSelectionReport]
                .DataRowsForCriteriaScenario(scenario)
                .RequireValue(metricId);
            var difference = AbsoluteDifference(summary, detail);
            if (difference > 0)
            {
                yield return new Measurement(metricId, difference);
            }
        }
    }

    private static IEnumerable<Measurement> MeasureProviders(
        LegacyAuditParityMetrics left,
        LegacyAuditParityMetrics right)
    {
        foreach (var measurement in MeasureShared(left, right))
        {
            yield return measurement;
        }
        foreach (var measurement in new[]
                 {
                     Count(LegacyAuditParityMetricIds.PartASourceRowCount,
                         left.Completeness.PartASourceRowCount,
                         right.Completeness.PartASourceRowCount),
                     Flag(LegacyAuditParityMetricIds.PartARowCountMatch,
                         left.Completeness.PartARowCountMatch.RequireValue(
                             LegacyAuditParityMetricIds.PartARowCountMatch)
                         == right.Completeness.PartARowCountMatch.RequireValue(
                             LegacyAuditParityMetricIds.PartARowCountMatch)),
                     Flag(LegacyAuditParityMetricIds.PartAAmountMatch,
                         left.Completeness.PartAAmountMatch.RequireValue(
                             LegacyAuditParityMetricIds.PartAAmountMatch)
                         == right.Completeness.PartAAmountMatch.RequireValue(
                             LegacyAuditParityMetricIds.PartAAmountMatch)),
                 })
        {
            if (measurement.DifferenceCount > 0)
            {
                yield return measurement;
            }
        }
        foreach (var rule in LegacyPrescreenRuleCatalog.All)
        {
            foreach (var measurement in MeasureRule(left, right, rule, failOnUnavailable: true))
            {
                yield return measurement;
            }
        }
        foreach (var measurement in MeasureCounts(
                     LegacyAuditParityMetricIds.WeekendUnionRowCount,
                     LegacyAuditParityMetricIds.WeekendUnionVoucherCount,
                     left.WeekendUnion,
                     right.WeekendUnion,
                     failOnUnavailable: true))
        {
            if (measurement.DifferenceCount > 0)
            {
                yield return measurement;
            }
        }
        foreach (var measurement in MeasureFiltersAndReports(
                     left,
                     right,
                     failOnUnavailable: true))
        {
            yield return measurement;
        }
    }

    private static IEnumerable<Measurement> MeasureShared(
        LegacyAuditParityMetrics left,
        LegacyAuditParityMetrics right)
    {
        var leftCompleteness = left.Completeness;
        var rightCompleteness = right.Completeness;
        foreach (var measurement in new[]
                 {
                     Count(LegacyAuditParityMetricIds.CompletenessDifferenceAccountCount,
                         leftCompleteness.DifferenceAccountCount, rightCompleteness.DifferenceAccountCount),
                     Count(LegacyAuditParityMetricIds.PartATargetRowCount,
                          leftCompleteness.PartATargetRowCount, rightCompleteness.PartATargetRowCount),
                     Amount(LegacyAuditParityMetricIds.PartATotalDebit,
                         leftCompleteness.PartATotalDebit, rightCompleteness.PartATotalDebit),
                     Amount(LegacyAuditParityMetricIds.PartATotalCredit,
                         leftCompleteness.PartATotalCredit, rightCompleteness.PartATotalCredit),
                     Count(LegacyAuditParityMetricIds.UnbalancedVoucherCount,
                         left.UnbalancedVoucherCount, right.UnbalancedVoucherCount),
                     Count(LegacyAuditParityMetricIds.InfSampleSize,
                         left.Inf.ReportedSampleSize, right.Inf.ReportedSampleSize),
                     new Measurement(
                         LegacyAuditParityMetricIds.InfMemberKeyMultiset,
                         left.Inf.MemberMultisetDifference(right.Inf)),
                 })
        {
            if (measurement.DifferenceCount is null or > 0)
            {
                yield return measurement;
            }
        }
    }

    private static IEnumerable<Measurement> MeasureRule(
        LegacyAuditParityMetrics left,
        LegacyAuditParityMetrics right,
        LegacyPrescreenRuleId rule,
        bool failOnUnavailable)
    {
        var leftCounts = left.PrescreenRules[rule];
        var rightCounts = right.PrescreenRules[rule];
        foreach (var measurement in MeasureCounts(
                     LegacyAuditParityMetricIds.PrescreenRowCount(rule),
                     LegacyAuditParityMetricIds.PrescreenVoucherCount(rule),
                     leftCounts,
                     rightCounts,
                     failOnUnavailable))
        {
            if (measurement.DifferenceCount is null or > 0)
            {
                yield return measurement;
            }
        }
    }

    private static IEnumerable<Measurement> MeasureCounts(
        string rowMetricId,
        string voucherMetricId,
        LegacyRowVoucherCounts left,
        LegacyRowVoucherCounts right,
        bool failOnUnavailable)
    {
        if (failOnUnavailable)
        {
            RequireRuleAvailable(rowMetricId, voucherMetricId, left, right);
        }
        else if (left.Applicability == LegacyMetricApplicability.NotExecuted
                 || right.Applicability == LegacyMetricApplicability.NotExecuted)
        {
            yield return new Measurement(rowMetricId, null);
            yield return new Measurement(voucherMetricId, null);
            yield break;
        }
        if (left.Applicability != right.Applicability)
        {
            yield return new Measurement(rowMetricId, 1);
            yield return new Measurement(voucherMetricId, 1);
            yield break;
        }
        if (left.Applicability == LegacyMetricApplicability.NotApplicable)
        {
            yield break;
        }

        yield return Count(rowMetricId, left.RowCount, right.RowCount, failOnUnavailable);
        yield return Count(voucherMetricId, left.VoucherCount, right.VoucherCount, failOnUnavailable);
    }

    private static IEnumerable<Measurement> MeasureFiltersAndReports(
        LegacyAuditParityMetrics left,
        LegacyAuditParityMetrics right,
        bool failOnUnavailable)
    {
        var leftScenarioIds = left.FilterScenarios.Keys.ToHashSet();
        var rightScenarioIds = right.FilterScenarios.Keys.ToHashSet();
        if (!leftScenarioIds.SetEquals(rightScenarioIds))
        {
            var missingScenario = leftScenarioIds
                .Concat(rightScenarioIds)
                .Distinct()
                .OrderBy(scenario => scenario.Ordinal)
                .First(scenario => !leftScenarioIds.Contains(scenario)
                    || !rightScenarioIds.Contains(scenario));
            throw new LegacyAuditParityComparisonCompletenessException(
                LegacyAuditParityMetricIds.FilterRowCount(missingScenario));
        }
        var scenarios = leftScenarioIds.OrderBy(scenario => scenario.Ordinal);
        foreach (var scenario in scenarios)
        {
            var hasLeft = left.FilterScenarios.TryGetValue(scenario, out var leftCounts);
            var hasRight = right.FilterScenarios.TryGetValue(scenario, out var rightCounts);
            if (!hasLeft || !hasRight)
            {
                throw new LegacyAuditParityComparisonCompletenessException(
                    LegacyAuditParityMetricIds.FilterRowCount(scenario));
            }
            leftCounts ??= new LegacyRowVoucherCounts(
                LegacyObservedCount.NotExecuted,
                LegacyObservedCount.NotExecuted);
            rightCounts ??= new LegacyRowVoucherCounts(
                LegacyObservedCount.NotExecuted,
                LegacyObservedCount.NotExecuted);
            foreach (var measurement in MeasureCounts(
                         LegacyAuditParityMetricIds.FilterRowCount(scenario),
                         LegacyAuditParityMetricIds.FilterVoucherCount(scenario),
                         leftCounts,
                         rightCounts,
                         failOnUnavailable))
            {
                if (measurement.DifferenceCount is null or > 0)
                {
                    yield return measurement;
                }
            }
        }

        foreach (var report in Enum.GetValues<LegacyReportKind>())
        {
            foreach (var difference in LegacyAuditParityReportMetrics.Difference(
                         report,
                         left.Reports[report],
                         right.Reports[report]))
            {
                yield return new Measurement(
                    difference.MetricId,
                    difference.DifferenceCount);
            }
        }
    }

    private static Measurement Count(
        string metricId,
        LegacyObservedCount left,
        LegacyObservedCount right) =>
        !left.HasExactValue || !right.HasExactValue
            ? new Measurement(metricId, null)
            : new Measurement(
                metricId,
                AbsoluteDifference(left.Value!.Value, right.Value!.Value));

    private static Measurement Count(
        string metricId,
        LegacyObservedCount left,
        LegacyObservedCount right,
        bool failOnUnavailable)
    {
        if (failOnUnavailable)
        {
            _ = left.RequireValue(metricId);
            _ = right.RequireValue(metricId);
        }
        return Count(metricId, left, right);
    }

    private static Measurement Amount(
        string metricId,
        LegacyObservedAmountFingerprint left,
        LegacyObservedAmountFingerprint right) =>
        !left.WasExecuted || !right.WasExecuted
            ? new Measurement(metricId, null)
            : Flag(
                metricId,
                string.Equals(left.Fingerprint, right.Fingerprint, StringComparison.Ordinal));

    private static Measurement Count(string metricId, long left, long right) =>
        new(metricId, AbsoluteDifference(left, right));

    private static Measurement Flag(string metricId, bool equal) =>
        new(metricId, equal ? 0 : 1);

    private static long AbsoluteDifference(long left, long right) =>
        left >= right ? left - right : right - left;

    private readonly record struct Measurement(string MetricId, long? DifferenceCount);
}

internal static class LegacyAuditParityDifferenceValidator
{
    internal static void ValidateClassification(
        LegacyParityCase @case,
        string metricId,
        LegacyAuditParityDifferenceCategory category,
        LegacyAuditParityCitation citation,
        LegacyAuditParityBasis basis,
        LegacyAuditParityMappingProvenance mappingDependencies,
        LegacyAuditParityHarnessBindingVerification harnessBindingVerification)
    {
        ValidateIdentityAndClassification(@case, metricId, category, citation, basis);
        ValidateHarnessBinding(
            category,
            mappingDependencies,
            harnessBindingVerification);
    }

    internal static void ValidateDifference(
        LegacyParityCase @case,
        LegacyAuditParityComparisonId comparisonId,
        string metricId,
        long? differenceCount,
        LegacyAuditParityDifferenceCategory category,
        LegacyAuditParityCitation citation,
        LegacyAuditParityBasis basis,
        LegacyAuditParityMappingProvenance mappingDependencies,
        LegacyAuditParityHarnessBindingVerification harnessBindingVerification)
    {
        ValidateIdentityAndClassification(@case, metricId, category, citation, basis);
        ValidateHarnessBinding(
            category,
            mappingDependencies,
            harnessBindingVerification);
        if (!Enum.IsDefined(comparisonId))
        {
            throw new ArgumentOutOfRangeException(nameof(comparisonId));
        }
        if (category == LegacyAuditParityDifferenceCategory.LegacyNoCounterpart)
        {
            if (differenceCount is not null)
            {
                throw new ArgumentException(
                    "Legacy-no-counterpart entries cannot invent a numeric difference.",
                    nameof(differenceCount));
            }
        }
        else if (differenceCount is null)
        {
            if (category is not LegacyAuditParityDifferenceCategory.LegacyDefectOrInputCondition
                and not LegacyAuditParityDifferenceCategory.UserDecisionRequired)
            {
                throw new ArgumentException(
                    "Only a typed legacy/input limitation or unresolved repository evidence may be non-comparable.",
                    nameof(differenceCount));
            }
        }
        else if (differenceCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(differenceCount),
                "Difference count must be positive.");
        }
    }

    private static void ValidateHarnessBinding(
        LegacyAuditParityDifferenceCategory category,
        LegacyAuditParityMappingProvenance mappingDependencies,
        LegacyAuditParityHarnessBindingVerification harnessBindingVerification)
    {
        ArgumentNullException.ThrowIfNull(mappingDependencies);
        if (!Enum.IsDefined(harnessBindingVerification))
        {
            throw new ArgumentOutOfRangeException(nameof(harnessBindingVerification));
        }

        if (!mappingDependencies.UsesHeaderBindingFallback)
        {
            if (harnessBindingVerification != LegacyAuditParityHarnessBindingVerification.NotRequired)
            {
                throw new ArgumentException(
                    "Harness-binding verification must be not-required when no fallback dependency exists.",
                    nameof(harnessBindingVerification));
            }
            return;
        }

        if (harnessBindingVerification == LegacyAuditParityHarnessBindingVerification.NotRequired)
        {
            throw new ArgumentException(
                "A header-binding fallback dependency requires a typed verification state.",
                nameof(harnessBindingVerification));
        }
        if (category == LegacyAuditParityDifferenceCategory.JetDefect
            && harnessBindingVerification != LegacyAuditParityHarnessBindingVerification.Verified)
        {
            throw new ArgumentException(
                "A JET-defect classification requires verified harness binding for every fallback dependency.",
                nameof(harnessBindingVerification));
        }
    }

    private static void ValidateIdentityAndClassification(
        LegacyParityCase @case,
        string metricId,
        LegacyAuditParityDifferenceCategory category,
        LegacyAuditParityCitation citation,
        LegacyAuditParityBasis basis)
    {
        if (!Enum.IsDefined(@case))
        {
            throw new ArgumentOutOfRangeException(nameof(@case));
        }
        if (string.IsNullOrWhiteSpace(metricId) || !LegacyAuditParityMetricIds.IsFixed(metricId))
        {
            throw new ArgumentException(
                "Difference identity must be a fixed parity metric id.",
                nameof(metricId));
        }
        if (category == LegacyAuditParityDifferenceCategory.Unclassified
            || !Enum.IsDefined(category))
        {
            throw new ArgumentException("Parity differences cannot remain unclassified.", nameof(category));
        }
        if (citation == LegacyAuditParityCitation.None || !Enum.IsDefined(citation))
        {
            throw new ArgumentException(
                "Parity differences require a fixed repository citation.",
                nameof(citation));
        }

        var validBasis = category switch
        {
            LegacyAuditParityDifferenceCategory.JetDefect =>
                basis is LegacyAuditParityBasis.JetRequirementMismatch
                    or LegacyAuditParityBasis.ProviderInconsistency,
            LegacyAuditParityDifferenceCategory.IntentionalDecision =>
                basis == LegacyAuditParityBasis.ApprovedIntentionalDifference,
            LegacyAuditParityDifferenceCategory.LegacyDefectOrInputCondition =>
                basis is LegacyAuditParityBasis.LegacyBehaviorDefect
                    or LegacyAuditParityBasis.InputProfileCondition,
            LegacyAuditParityDifferenceCategory.LegacyNoCounterpart =>
                basis == LegacyAuditParityBasis.LegacyNoComparableItem,
            LegacyAuditParityDifferenceCategory.UserDecisionRequired =>
                basis == LegacyAuditParityBasis.RepositoryEvidenceInsufficient,
            _ => false,
        };
        if (!validBasis)
        {
            throw new ArgumentException(
                "Parity difference basis must match its classification category.",
                nameof(basis));
        }

        if (!CitationMatches(category, basis, citation))
        {
            throw new ArgumentException(
                "Parity difference citation must match its classification category and basis.",
                nameof(citation));
        }
    }

    private static bool CitationMatches(
        LegacyAuditParityDifferenceCategory category,
        LegacyAuditParityBasis basis,
        LegacyAuditParityCitation citation) => (category, basis) switch
        {
            (LegacyAuditParityDifferenceCategory.JetDefect,
                LegacyAuditParityBasis.ProviderInconsistency) =>
                citation == LegacyAuditParityCitation.ParityPlanProviderConsistency,
            (LegacyAuditParityDifferenceCategory.JetDefect,
                LegacyAuditParityBasis.JetRequirementMismatch) =>
                citation is not LegacyAuditParityCitation.ParityPlanProviderConsistency,
            (LegacyAuditParityDifferenceCategory.IntentionalDecision,
                LegacyAuditParityBasis.ApprovedIntentionalDifference) =>
                citation is LegacyAuditParityCitation.JetGuideSection2
                    or LegacyAuditParityCitation.JetGuideSection3
                    or LegacyAuditParityCitation.JetGuideSection4
                    or LegacyAuditParityCitation.JetGuideSection5
                    or LegacyAuditParityCitation.JetGuideSection6
                    or LegacyAuditParityCitation.JetGuideSection7
                    or LegacyAuditParityCitation.ParityPlanClassificationRules,
            (LegacyAuditParityDifferenceCategory.LegacyDefectOrInputCondition,
                LegacyAuditParityBasis.LegacyBehaviorDefect) =>
                citation is LegacyAuditParityCitation.LegacyIdeaScript
                    or LegacyAuditParityCitation.LegacyIdeaModule,
            (LegacyAuditParityDifferenceCategory.LegacyDefectOrInputCondition,
                LegacyAuditParityBasis.InputProfileCondition) =>
                citation is LegacyAuditParityCitation.JetGuideSection2
                    or LegacyAuditParityCitation.JetGuideSection3
                    or LegacyAuditParityCitation.JetGuideSection4
                    or LegacyAuditParityCitation.JetGuideSection5
                    or LegacyAuditParityCitation.JetGuideSection6
                    or LegacyAuditParityCitation.JetGuideSection7
                    or LegacyAuditParityCitation.LegacyIdeaScript
                    or LegacyAuditParityCitation.LegacyIdeaModule
                    or LegacyAuditParityCitation.ParityPlanClassificationRules,
            (LegacyAuditParityDifferenceCategory.LegacyNoCounterpart,
                LegacyAuditParityBasis.LegacyNoComparableItem) =>
                citation == LegacyAuditParityCitation.ParityPlanClassificationRules,
            (LegacyAuditParityDifferenceCategory.UserDecisionRequired,
                LegacyAuditParityBasis.RepositoryEvidenceInsufficient) =>
                citation == LegacyAuditParityCitation.ParityPlanClassificationRules,
            _ => false,
        };

    internal static string RepositoryPath(this LegacyAuditParityCitation citation) => citation switch
    {
        LegacyAuditParityCitation.JetGuideSection2 => "docs/jet-guide.md §2",
        LegacyAuditParityCitation.JetGuideSection3 => "docs/jet-guide.md §3",
        LegacyAuditParityCitation.JetGuideSection4 => "docs/jet-guide.md §4",
        LegacyAuditParityCitation.JetGuideSection5 => "docs/jet-guide.md §5",
        LegacyAuditParityCitation.JetGuideSection6 => "docs/jet-guide.md §6",
        LegacyAuditParityCitation.JetGuideSection7 => "docs/jet-guide.md §7",
        LegacyAuditParityCitation.ActionContractValidation => "docs/action-contract-manifest.md § Validation",
        LegacyAuditParityCitation.ActionContractPrescreen => "docs/action-contract-manifest.md § Prescreen",
        LegacyAuditParityCitation.ActionContractFilter => "docs/action-contract-manifest.md § Filter",
        LegacyAuditParityCitation.ActionContractExports => "docs/action-contract-manifest.md § Export",
        LegacyAuditParityCitation.LegacyIdeaScript => "legacy/idea-script.bas",
        LegacyAuditParityCitation.LegacyIdeaModule => "legacy/idea-tool.bas",
        LegacyAuditParityCitation.ParityPlanClassificationRules =>
            "docs/jet-guide.md §17 Legacy parity difference classification (five categories)",
        LegacyAuditParityCitation.ParityPlanProviderConsistency =>
            "docs/jet-guide.md §17 provider consistency",
        _ => throw new ArgumentOutOfRangeException(nameof(citation)),
    };
}
