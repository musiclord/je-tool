using JET.Domain;

namespace JET.Tests.Infrastructure;

internal enum LegacyAuditParityReportFamily
{
    ValidationSummary,
    ValidationFieldInfo,
    ValidationDifferenceGuidance,
    ValidationCompletenessDifferenceDetail,
    ValidationNullAccountDetail,
    ValidationNullDocumentDetail,
    ValidationNullDescriptionDetail,
    ValidationOutOfRangeApprovalDateDetail,
    ValidationCompletenessReconciliation,
    ValidationUnbalancedGlDetail,
    ValidationBlankPostDateDetail,
    ValidationSourceQuality,

    AccountMappingMain,
    AccountMappingList,

    InfReliability,
    InfAllFields,

    PrescreenSummary,
    PrescreenPostPeriodApprovalDetail,
    PrescreenSuspiciousKeywordsDetail,
    PrescreenUnexpectedAccountPairDetail,
    PrescreenTrailingZerosDetail,
    PrescreenCreatorSummaryDetail,
    PrescreenRareAccountsDetail,
    PrescreenBlankDescriptionDetail,

    CriteriaSummary,
    CriteriaScenario001,
    CriteriaScenario002,
    CriteriaScenario003,
    CriteriaScenario004,
    CriteriaScenario005,
    CriteriaScenario006,
    CriteriaScenario007,
    CriteriaScenario008,
    CriteriaScenario009,
    CriteriaScenario010,

    WorkingPaperCover,
    WorkingPaperIntro,
    WorkingPaperCompleteness,
    WorkingPaperUnbalancedVouchers,
    WorkingPaperCreatorSummary,
    WorkingPaperCompletenessDifference,
    WorkingPaperReliability,
    WorkingPaperRiskConditionSummary,
    WorkingPaperRiskVoucherMatrix,
    WorkingPaperRiskVoucherDetail,
    WorkingPaperPostCloseAdjustments,
    WorkingPaperFieldInfo,
    WorkingPaperCalendarInfo,
    WorkingPaperAccountMapping,
}

internal enum LegacyAuditParityReportMetricKind
{
    SheetCount,
    DataRowCount,
}

internal readonly record struct LegacyAuditParityReportMetricIdentity(
    LegacyReportKind Report,
    LegacyAuditParityReportFamily Family,
    LegacyAuditParityReportMetricKind Kind);

internal readonly record struct LegacyAuditParityReportMetricDifference(
    string MetricId,
    long DifferenceCount);

internal sealed class LegacyAuditParityReportMetricsException(string fieldId)
    : InvalidOperationException($"legacy-audit-parity-report-metrics:{fieldId}")
{
    internal string FieldId { get; } = fieldId;
}

/// <summary>
/// Closed, test-only projection from deidentified physical worksheet fingerprints to
/// logical report families. Continuation pages remain observable through sheet_count,
/// while data_row_count compares the checked family total instead of page partitioning.
/// </summary>
internal static class LegacyAuditParityReportMetrics
{
    private const int MaximumContinuationPart = 999;

    private static readonly IReadOnlyList<FamilyDefinition> Definitions = BuildDefinitions();
    private static readonly IReadOnlyDictionary<LegacyAuditParityReportFamily, FamilyDefinition>
        DefinitionsByFamily = Definitions.ToDictionary(definition => definition.Family);
    private static readonly IReadOnlyDictionary<string, LegacyAuditParityReportMetricIdentity>
        MetricIdentities = BuildMetricIdentities();
    private static readonly IReadOnlyDictionary<LegacyReportKind, IReadOnlyDictionary<string, PhysicalSheet>>
        PhysicalSheetsByReport = Enum.GetValues<LegacyReportKind>().ToDictionary(
            report => report,
            BuildPhysicalSheets);

    internal static IReadOnlyList<string> FixedMetricIds { get; } =
        Array.AsReadOnly(MetricIdentities.Keys.Order(StringComparer.Ordinal).ToArray());

    internal static IReadOnlyList<LegacyAuditParityReportFamily> FamiliesFor(
        LegacyReportKind report)
    {
        if (!Enum.IsDefined(report))
        {
            throw new ArgumentOutOfRangeException(nameof(report));
        }

        return Array.AsReadOnly(Definitions
            .Where(definition => definition.Report == report)
            .Select(definition => definition.Family)
            .ToArray());
    }

    internal static string SheetCount(LegacyAuditParityReportFamily family) =>
        MetricId(Definition(family), LegacyAuditParityReportMetricKind.SheetCount);

    internal static string DataRowCount(LegacyAuditParityReportFamily family)
        => MetricId(Definition(family), LegacyAuditParityReportMetricKind.DataRowCount);

    internal static string BaseSheetName(LegacyAuditParityReportFamily family) =>
        Definition(family).BaseSheetName;

    internal static string ContinuationSheetName(
        LegacyAuditParityReportFamily family,
        int part,
        bool localizedSuffix)
    {
        var definition = Definition(family);
        if (!definition.AllowsContinuation || part is < 2 or > MaximumContinuationPart)
        {
            throw new ArgumentOutOfRangeException(nameof(part));
        }

        return PhysicalName(definition.BaseSheetName, part, localizedSuffix);
    }

    internal static bool IsFixed(string metricId) =>
        !string.IsNullOrWhiteSpace(metricId) && MetricIdentities.ContainsKey(metricId);

    internal static LegacyObservedCount ObserveDataRowCount(
        LegacyReportKind report,
        LegacyAuditParityReportFamily family,
        LegacyReportObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var definition = Definition(family);
        if (definition.Report != report || !definition.HasDataRows)
        {
            throw new ArgumentException(
                "Logical report data-row observation requires a counted family.",
                nameof(family));
        }
        return Project(report, observation)[family].DataRowCount;
    }

    internal static bool TryGetIdentity(
        string metricId,
        out LegacyAuditParityReportMetricIdentity identity) =>
        MetricIdentities.TryGetValue(metricId, out identity);

    internal static IReadOnlyList<LegacyAuditParityReportMetricDifference> Difference(
        LegacyReportKind report,
        LegacyReportObservation left,
        LegacyReportObservation right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var leftFamilies = Project(report, left);
        var rightFamilies = Project(report, right);
        var differences = new List<LegacyAuditParityReportMetricDifference>();

        foreach (var definition in Definitions.Where(definition => definition.Report == report))
        {
            var leftFamily = leftFamilies[definition.Family];
            var rightFamily = rightFamilies[definition.Family];
            var sheetDifference = AbsoluteDifference(leftFamily.SheetCount, rightFamily.SheetCount);
            if (sheetDifference > 0)
            {
                differences.Add(new LegacyAuditParityReportMetricDifference(
                    SheetCount(definition.Family),
                    sheetDifference));
            }

            var dataMetricId = DataRowCount(definition.Family);
            var rowDifference = AbsoluteDifference(
                leftFamily.DataRowCount.RequireValue(dataMetricId),
                rightFamily.DataRowCount.RequireValue(dataMetricId));
            if (rowDifference > 0)
            {
                differences.Add(new LegacyAuditParityReportMetricDifference(
                    dataMetricId,
                    rowDifference));
            }
        }

        return Array.AsReadOnly(differences.ToArray());
    }

    private static IReadOnlyDictionary<LegacyAuditParityReportFamily, FamilyObservation> Project(
        LegacyReportKind report,
        LegacyReportObservation observation)
    {
        if (!Enum.IsDefined(report))
        {
            throw new ArgumentOutOfRangeException(nameof(report));
        }

        var physicalCatalog = PhysicalSheetsByReport[report];
        var captured = new Dictionary<LegacyAuditParityReportFamily, List<CapturedSheet>>();
        foreach (var sheet in observation.FingerprintedSheetObservations)
        {
            if (!physicalCatalog.TryGetValue(sheet.Key, out var physical))
            {
                throw Error($"unknown-sheet.{ReportStem(report)}");
            }

            if (!captured.TryGetValue(physical.Family, out var familySheets))
            {
                familySheets = [];
                captured.Add(physical.Family, familySheets);
            }
            familySheets.Add(new CapturedSheet(physical.Part, sheet.Value));
        }

        var result = new Dictionary<LegacyAuditParityReportFamily, FamilyObservation>();
        foreach (var definition in Definitions.Where(definition => definition.Report == report))
        {
            if (!captured.TryGetValue(definition.Family, out var familySheets))
            {
                result.Add(
                    definition.Family,
                    new FamilyObservation(0, LegacyObservedCount.Executed(0)));
                continue;
            }

            var ordered = familySheets.OrderBy(sheet => sheet.Part).ToArray();
            if (ordered[0].Part != 1
                || ordered.Select(sheet => sheet.Part).Distinct().Count() != ordered.Length
                || ordered.Where((sheet, index) => sheet.Part != index + 1).Any())
            {
                throw Error("continuation-sequence");
            }

            if (!definition.HasDataRows)
            {
                var fixedDataMetricId = DataRowCount(definition.Family);
                foreach (var page in ordered)
                {
                    if (page.DataRowCount.RequireValue(fixedDataMetricId) != 0)
                    {
                        throw Error("fixed-zero-data");
                    }
                }
                result.Add(
                    definition.Family,
                    new FamilyObservation(ordered.Length, LegacyObservedCount.Executed(0)));
                continue;
            }

            var dataMetricId = DataRowCount(definition.Family);
            long total = 0;
            foreach (var page in ordered)
            {
                total = checked(total + page.DataRowCount.RequireValue(dataMetricId));
            }
            result.Add(
                definition.Family,
                new FamilyObservation(ordered.Length, LegacyObservedCount.Executed(total)));
        }

        return result;
    }

    private static IReadOnlyDictionary<string, LegacyAuditParityReportMetricIdentity>
        BuildMetricIdentities()
    {
        var result = new Dictionary<string, LegacyAuditParityReportMetricIdentity>(StringComparer.Ordinal);
        foreach (var definition in Definitions)
        {
            Add(LegacyAuditParityReportMetricKind.SheetCount);
            Add(LegacyAuditParityReportMetricKind.DataRowCount);

            void Add(LegacyAuditParityReportMetricKind kind)
            {
                if (!result.TryAdd(
                        MetricId(definition, kind),
                        new LegacyAuditParityReportMetricIdentity(
                            definition.Report,
                            definition.Family,
                            kind)))
                {
                    throw Error("duplicate-metric-id");
                }
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<string, PhysicalSheet> BuildPhysicalSheets(
        LegacyReportKind report)
    {
        var result = new Dictionary<string, PhysicalSheet>(StringComparer.Ordinal);
        foreach (var definition in Definitions.Where(definition => definition.Report == report))
        {
            Add(definition.BaseSheetName, part: 1);
            if (!definition.AllowsContinuation)
            {
                continue;
            }

            for (var part = 2; part <= MaximumContinuationPart; part++)
            {
                Add(PhysicalName(definition.BaseSheetName, part, localizedSuffix: false), part);
                Add(PhysicalName(definition.BaseSheetName, part, localizedSuffix: true), part);
            }

            void Add(string rawName, int part)
            {
                var fingerprint = LegacyAuditParityFingerprints.SheetName(rawName);
                if (!result.TryAdd(
                        fingerprint,
                        new PhysicalSheet(definition.Family, part)))
                {
                    throw Error("duplicate-sheet-identity");
                }
            }
        }
        return result;
    }

    private static IReadOnlyList<FamilyDefinition> BuildDefinitions()
    {
        var definitions = new List<FamilyDefinition>
        {
            Fixed(
                LegacyReportKind.ValidationReport,
                LegacyAuditParityReportFamily.ValidationSummary,
                "summary",
                "ValidationReport"),
            Counted(
                LegacyReportKind.ValidationReport,
                LegacyAuditParityReportFamily.ValidationFieldInfo,
                "field_info",
                MappingMetadataFormat.WorksheetName),
            Fixed(
                LegacyReportKind.ValidationReport,
                LegacyAuditParityReportFamily.ValidationDifferenceGuidance,
                "difference_guidance",
                "完整性測試出現差異時之指引"),
            Counted(
                LegacyReportKind.ValidationReport,
                LegacyAuditParityReportFamily.ValidationCompletenessDifferenceDetail,
                "completeness_difference_detail",
                WorkpaperSheetCatalog.Step13,
                continuation: true),
            Counted(
                LegacyReportKind.ValidationReport,
                LegacyAuditParityReportFamily.ValidationNullAccountDetail,
                "null_account_detail",
                "V_Report 1",
                continuation: true),
            Counted(
                LegacyReportKind.ValidationReport,
                LegacyAuditParityReportFamily.ValidationNullDocumentDetail,
                "null_document_detail",
                "V_Report 2",
                continuation: true),
            Counted(
                LegacyReportKind.ValidationReport,
                LegacyAuditParityReportFamily.ValidationNullDescriptionDetail,
                "null_description_detail",
                "V_Report 3",
                continuation: true),
            Counted(
                LegacyReportKind.ValidationReport,
                LegacyAuditParityReportFamily.ValidationOutOfRangeApprovalDateDetail,
                "out_of_range_approval_date_detail",
                "V_Report 4",
                continuation: true),
            Counted(
                LegacyReportKind.ValidationReport,
                LegacyAuditParityReportFamily.ValidationCompletenessReconciliation,
                "completeness_reconciliation",
                "V_Report 5",
                continuation: true),
            Counted(
                LegacyReportKind.ValidationReport,
                LegacyAuditParityReportFamily.ValidationUnbalancedGlDetail,
                "unbalanced_gl_detail",
                "V_Report 6",
                continuation: true),
            Counted(
                LegacyReportKind.ValidationReport,
                LegacyAuditParityReportFamily.ValidationBlankPostDateDetail,
                "blank_post_date_detail",
                "V_Report 7",
                continuation: true),
            // Validation v4（jet-guide §7.2）：blank post date 不建 V7，改由條件式
            // `Source_Quality` 揭示來源品質明細。
            Counted(
                LegacyReportKind.ValidationReport,
                LegacyAuditParityReportFamily.ValidationSourceQuality,
                "source_quality",
                "Source_Quality",
                continuation: true),

            Counted(
                LegacyReportKind.AccountMapping,
                LegacyAuditParityReportFamily.AccountMappingMain,
                "account_mapping",
                "AccountMapping"),
            Counted(
                LegacyReportKind.AccountMapping,
                LegacyAuditParityReportFamily.AccountMappingList,
                "category_list",
                "List"),

            Counted(
                LegacyReportKind.InfReport,
                LegacyAuditParityReportFamily.InfReliability,
                "reliability",
                "INF Testing 可靠性測試"),
            Counted(
                LegacyReportKind.InfReport,
                LegacyAuditParityReportFamily.InfAllFields,
                "all_fields",
                "可靠性樣本_所有欄位"),

            Fixed(
                LegacyReportKind.PrescreenReport,
                LegacyAuditParityReportFamily.PrescreenSummary,
                "summary",
                "Pre-screening_Report"),
            Counted(
                LegacyReportKind.PrescreenReport,
                LegacyAuditParityReportFamily.PrescreenPostPeriodApprovalDetail,
                "post_period_approval_detail",
                "R1",
                continuation: true),
            Counted(
                LegacyReportKind.PrescreenReport,
                LegacyAuditParityReportFamily.PrescreenSuspiciousKeywordsDetail,
                "suspicious_keywords_detail",
                "R2",
                continuation: true),
            Counted(
                LegacyReportKind.PrescreenReport,
                LegacyAuditParityReportFamily.PrescreenUnexpectedAccountPairDetail,
                "unexpected_account_pair_detail",
                "R3",
                continuation: true),
            Counted(
                LegacyReportKind.PrescreenReport,
                LegacyAuditParityReportFamily.PrescreenTrailingZerosDetail,
                "trailing_zeros_detail",
                "R4",
                continuation: true),
            Counted(
                LegacyReportKind.PrescreenReport,
                LegacyAuditParityReportFamily.PrescreenCreatorSummaryDetail,
                "creator_summary_detail",
                "R5",
                continuation: true),
            Counted(
                LegacyReportKind.PrescreenReport,
                LegacyAuditParityReportFamily.PrescreenRareAccountsDetail,
                "rare_accounts_detail",
                "R6",
                continuation: true),
            Counted(
                LegacyReportKind.PrescreenReport,
                LegacyAuditParityReportFamily.PrescreenBlankDescriptionDetail,
                "blank_description_detail",
                "R7",
                continuation: true),

            Fixed(
                LegacyReportKind.CriteriaSelectionReport,
                LegacyAuditParityReportFamily.CriteriaSummary,
                "summary",
                "Summary Inforamtion"),

            Fixed(
                LegacyReportKind.WorkingPaper,
                LegacyAuditParityReportFamily.WorkingPaperCover,
                "cover",
                WorkpaperSheetCatalog.Cover),
            Fixed(
                LegacyReportKind.WorkingPaper,
                LegacyAuditParityReportFamily.WorkingPaperIntro,
                "intro",
                WorkpaperSheetCatalog.Intro),
            Counted(
                LegacyReportKind.WorkingPaper,
                LegacyAuditParityReportFamily.WorkingPaperCompleteness,
                "completeness",
                WorkpaperSheetCatalog.Step1,
                continuation: true),
            Counted(
                LegacyReportKind.WorkingPaper,
                LegacyAuditParityReportFamily.WorkingPaperUnbalancedVouchers,
                "unbalanced_vouchers",
                WorkpaperSheetCatalog.Step11,
                continuation: true),
            Counted(
                LegacyReportKind.WorkingPaper,
                LegacyAuditParityReportFamily.WorkingPaperCreatorSummary,
                "creator_summary",
                WorkpaperSheetCatalog.Step12,
                continuation: true),
            Counted(
                LegacyReportKind.WorkingPaper,
                LegacyAuditParityReportFamily.WorkingPaperCompletenessDifference,
                "completeness_difference",
                WorkpaperSheetCatalog.Step13,
                continuation: true),
            Counted(
                LegacyReportKind.WorkingPaper,
                LegacyAuditParityReportFamily.WorkingPaperReliability,
                "reliability",
                WorkpaperSheetCatalog.Step2),
            Counted(
                LegacyReportKind.WorkingPaper,
                LegacyAuditParityReportFamily.WorkingPaperRiskConditionSummary,
                "risk_condition_summary",
                WorkpaperSheetCatalog.Step3),
            Counted(
                LegacyReportKind.WorkingPaper,
                LegacyAuditParityReportFamily.WorkingPaperRiskVoucherMatrix,
                "risk_voucher_matrix",
                WorkpaperSheetCatalog.Step4,
                continuation: true),
            Counted(
                LegacyReportKind.WorkingPaper,
                LegacyAuditParityReportFamily.WorkingPaperRiskVoucherDetail,
                "risk_voucher_detail",
                WorkpaperSheetCatalog.Step41,
                continuation: true),
            Fixed(
                LegacyReportKind.WorkingPaper,
                LegacyAuditParityReportFamily.WorkingPaperPostCloseAdjustments,
                "post_close_adjustments",
                WorkpaperSheetCatalog.Step5),
            Counted(
                LegacyReportKind.WorkingPaper,
                LegacyAuditParityReportFamily.WorkingPaperFieldInfo,
                "field_info",
                WorkpaperSheetCatalog.FieldInfo),
            Counted(
                LegacyReportKind.WorkingPaper,
                LegacyAuditParityReportFamily.WorkingPaperCalendarInfo,
                "calendar_info",
                WorkpaperSheetCatalog.CalendarInfo,
                continuation: true),
            Counted(
                LegacyReportKind.WorkingPaper,
                LegacyAuditParityReportFamily.WorkingPaperAccountMapping,
                "account_mapping",
                WorkpaperSheetCatalog.AccountMapping,
                continuation: true),
        };

        var criteriaFamilies = new[]
        {
            LegacyAuditParityReportFamily.CriteriaScenario001,
            LegacyAuditParityReportFamily.CriteriaScenario002,
            LegacyAuditParityReportFamily.CriteriaScenario003,
            LegacyAuditParityReportFamily.CriteriaScenario004,
            LegacyAuditParityReportFamily.CriteriaScenario005,
            LegacyAuditParityReportFamily.CriteriaScenario006,
            LegacyAuditParityReportFamily.CriteriaScenario007,
            LegacyAuditParityReportFamily.CriteriaScenario008,
            LegacyAuditParityReportFamily.CriteriaScenario009,
            LegacyAuditParityReportFamily.CriteriaScenario010,
        };
        var workingPaperStart = definitions.FindIndex(definition =>
            definition.Family == LegacyAuditParityReportFamily.WorkingPaperCover);
        for (var index = 0; index < criteriaFamilies.Length; index++)
        {
            var ordinal = index + 1;
            definitions.Insert(
                workingPaperStart + index,
                Counted(
                    LegacyReportKind.CriteriaSelectionReport,
                    criteriaFamilies[index],
                    $"scenario.{ordinal:000}",
                    $"#Criteria Select {ordinal}",
                    continuation: true));
        }

        var expected = Enum.GetValues<LegacyAuditParityReportFamily>();
        if (definitions.Count != expected.Length
            || definitions.Select(definition => definition.Family).Distinct().Count() != expected.Length
            || expected.Any(family => definitions.All(definition => definition.Family != family)))
        {
            throw Error("family-catalog");
        }

        return Array.AsReadOnly(definitions.ToArray());
    }

    private static FamilyDefinition Fixed(
        LegacyReportKind report,
        LegacyAuditParityReportFamily family,
        string metricStem,
        string baseSheetName) =>
        new(report, family, metricStem, baseSheetName, HasDataRows: false, AllowsContinuation: false);

    private static FamilyDefinition Counted(
        LegacyReportKind report,
        LegacyAuditParityReportFamily family,
        string metricStem,
        string baseSheetName,
        bool continuation = false) =>
        new(
            report,
            family,
            metricStem,
            baseSheetName,
            HasDataRows: true,
            AllowsContinuation: continuation);

    private static FamilyDefinition Definition(LegacyAuditParityReportFamily family) =>
        DefinitionsByFamily.TryGetValue(family, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(family));

    private static string MetricId(
        FamilyDefinition definition,
        LegacyAuditParityReportMetricKind kind) =>
        $"report.{ReportStem(definition.Report)}.{definition.MetricStem}."
        + (kind == LegacyAuditParityReportMetricKind.SheetCount
            ? "sheet_count"
            : "data_row_count");

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

    private static string PhysicalName(string baseName, int part, bool localizedSuffix) =>
        $"{baseName} ({(localizedSuffix ? "續" : string.Empty)}{part})";

    private static long AbsoluteDifference(long left, long right) =>
        left >= right ? left - right : right - left;

    private static LegacyAuditParityReportMetricsException Error(string fieldId) => new(fieldId);

    private sealed record FamilyDefinition(
        LegacyReportKind Report,
        LegacyAuditParityReportFamily Family,
        string MetricStem,
        string BaseSheetName,
        bool HasDataRows,
        bool AllowsContinuation);

    private readonly record struct PhysicalSheet(
        LegacyAuditParityReportFamily Family,
        int Part);

    private readonly record struct CapturedSheet(
        int Part,
        LegacyObservedCount DataRowCount);

    private readonly record struct FamilyObservation(
        long SheetCount,
        LegacyObservedCount DataRowCount);
}
