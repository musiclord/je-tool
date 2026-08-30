using System.Security.Cryptography;
using System.Text;

namespace JET.Tests.Infrastructure;

internal sealed record PrivateCaseExplicitContentDecision(
    string Scope,
    LegacyAuditParityContentDimension Dimension,
    string DecisionId);

internal sealed record PrivateCaseReportDecisionSummary(
    string DecisionId,
    int GroupCount,
    long DifferenceCount);

/// <summary>
/// 將舊 IDEA 與 JET 的差異分成已裁定與尚待處理兩類。這裡只收錄目前程式及
/// new-je-tool 現行規格已有依據的差異，不以數量白名單放行。
/// </summary>
internal static class PrivateCaseReportDifferencePolicy
{
    internal const string JetStandardSheetSet = "jet-standard-sheet-set";
    internal const string JetStandardNonDirectPage = "jet-standard-non-direct-page";
    internal const string PersistedSourceFieldTypeContent = "persisted-source-field-type-content";
    internal const string IdeaExportColumnCatalog = "idea-export-column-catalog";
    internal const string DetailRowOrdering = "detail-row-ordering";
    internal const string ApprovedCompletenessTotals = "approved-completeness-totals";
    internal const string ApprovedInfSampling = "approved-inf-sampling";
    internal const string ApprovedPrescreenRule = "approved-prescreen-rule";

    internal const string UnifiedFormalWorkbookFont = "unified-formal-workbook-font";
    internal const string DynamicSafeWidth = "dynamic-safe-width";
    internal const string PersistedSourceFieldTypeAppearance = "persisted-source-field-type-appearance";
    internal const string FinalizedDisplayFormat = "finalized-display-format";
    internal const string StyledCellShape = "styled-cell-shape";
    internal const string DeepCellStyleCovered = "deep-cell-style-covered";
    internal const string Step41Borderless = "step4-1-borderless";
    internal const string Step41ProtectionLayout = "step4-1-protection-layout";

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> DirectPhysicalPages =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["validation-report"] = Set("V_Report 3", "V_Report 4", "V_Report 5"),
            ["inf-report"] = Set("可靠性樣本_所有欄位"),
            ["prescreen-report"] = Set("R1", "R2", "R3", "R4", "R5", "R6", "R7"),
            ["criteria-selection-report"] = Set(
                "#Criteria Select 1",
                "#Criteria Select 2",
                "#Criteria Select 3",
                "#Criteria Select 4",
                "#Criteria Select 5",
                "#Criteria Select 6"),
            ["working-paper"] = Set("step4-1 符合高風險條件傳票明細"),
        };

    private static readonly IReadOnlySet<string> RawSchemaPages = Set(
        "V_Report 3",
        "V_Report 4",
        "可靠性樣本_所有欄位",
        "R1",
        "R2",
        "R3",
        "R4",
        "R7",
        "#Criteria Select 1",
        "#Criteria Select 2",
        "#Criteria Select 3",
        "#Criteria Select 4",
        "#Criteria Select 5",
        "#Criteria Select 6");

    private static readonly IReadOnlySet<string> FixedPrecisionPages =
        Set("V_Report 5", "R5", "R6");

    private static readonly IReadOnlyDictionary<
        (string Scope, LegacyAuditParityContentDimension Dimension, string DecisionId),
        (string ReportSlug, string SheetName)> ExplicitContentDecisions =
        new Dictionary<
            (string, LegacyAuditParityContentDimension, string),
            (string, string)>
        {
            [("validation-v5", LegacyAuditParityContentDimension.RowValues,
                ApprovedCompletenessTotals)] = ("validation-report", "V_Report 5"),
            [("inf-all-fields", LegacyAuditParityContentDimension.RowCount,
                ApprovedInfSampling)] = ("inf-report", "可靠性樣本_所有欄位"),
            [("inf-all-fields", LegacyAuditParityContentDimension.RowValues,
                ApprovedInfSampling)] = ("inf-report", "可靠性樣本_所有欄位"),
            [("prescreen-r3", LegacyAuditParityContentDimension.RowCount,
                ApprovedPrescreenRule)] = ("prescreen-report", "R3"),
            [("prescreen-r4", LegacyAuditParityContentDimension.RowCount,
                ApprovedPrescreenRule)] = ("prescreen-report", "R4"),
        };

    internal static bool IsSupportedExplicitContentDecision(
        PrivateCaseExplicitContentDecision decision) =>
        ExplicitContentDecisions.ContainsKey(
            (decision.Scope, decision.Dimension, decision.DecisionId));

    internal static string? ResolveContent(
        string reportSlug,
        LegacyAuditParityContentDifference difference,
        IReadOnlyList<PrivateCaseExplicitContentDecision> explicitDecisions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportSlug);
        ArgumentNullException.ThrowIfNull(difference);
        ArgumentNullException.ThrowIfNull(explicitDecisions);

        foreach (var decision in explicitDecisions)
        {
            if (!ExplicitContentDecisions.TryGetValue(
                    (decision.Scope, decision.Dimension, decision.DecisionId),
                    out var target))
            {
                continue;
            }
            if (string.Equals(target.ReportSlug, reportSlug, StringComparison.Ordinal)
                && string.Equals(target.SheetName, difference.SheetName, StringComparison.Ordinal)
                && decision.Dimension == difference.Dimension)
            {
                return decision.DecisionId;
            }
        }

        if (difference.Dimension == LegacyAuditParityContentDimension.WorksheetSetAndOrder)
        {
            return JetStandardSheetSet;
        }
        if (!IsDirectPhysicalPage(reportSlug, difference.SheetName))
        {
            return JetStandardNonDirectPage;
        }
        if (difference.Dimension == LegacyAuditParityContentDimension.CellStorage)
        {
            return PersistedSourceFieldTypeContent;
        }
        if (difference.Dimension is LegacyAuditParityContentDimension.ColumnHeaders
            or LegacyAuditParityContentDimension.ColumnValuesUnmatched)
        {
            return IdeaExportColumnCatalog;
        }
        if (difference.Dimension == LegacyAuditParityContentDimension.RowValueSequence)
        {
            return DetailRowOrdering;
        }
        return null;
    }

    internal static string? ResolveAppearance(
        string reportSlug,
        SpreadsheetAppearanceDifferenceGroup group,
        IReadOnlyList<SpreadsheetAppearanceDifferenceGroup> allGroups)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportSlug);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(allGroups);

        if (group.Property.EndsWith("font.name", StringComparison.Ordinal))
        {
            return UnifiedFormalWorkbookFont;
        }
        if (!IsDirectPhysicalPage(reportSlug, group.SheetName))
        {
            return JetStandardNonDirectPage;
        }
        if (group.ScopeKind == "column" && group.Property == "width")
        {
            return DynamicSafeWidth;
        }
        if (RawSchemaPages.Contains(group.SheetName)
            && group.Property.EndsWith("numberFormat.code", StringComparison.Ordinal)
            && group.ScopeKind is "cell" or "column")
        {
            return PersistedSourceFieldTypeAppearance;
        }
        if ((FixedPrecisionPages.Contains(group.SheetName)
                || IsStep41(group.SheetName))
            && group.ScopeKind == "cell"
            && group.Property.EndsWith("numberFormat.code", StringComparison.Ordinal))
        {
            return FinalizedDisplayFormat;
        }
        if (group.ScopeKind == "cellStream" && group.Property == "cellCount")
        {
            return StyledCellShape;
        }
        if (group.ScopeKind == "cellStream"
            && group.Property == "sha256"
            && allGroups.Any(item =>
                item.SheetName == group.SheetName
                && (item.Property.EndsWith("numberFormat.code", StringComparison.Ordinal)
                    || item.Property == "cellCount"
                    || item.Property.EndsWith("protection.locked", StringComparison.Ordinal)
                    || IsStep41BorderDifference(item))))
        {
            return DeepCellStyleCovered;
        }
        if (IsStep41BorderDifference(group))
        {
            return Step41Borderless;
        }
        if (IsStep41(group.SheetName)
            && ((group.ScopeKind == "column"
                    && group.Property == "style.alignment.horizontal")
                || (group.ScopeKind == "cell"
                    && group.Property == "protection.locked")
                || (group.ScopeKind == "row" && group.Property == "height")
                || (group.ScopeKind == "sheetProtection"
                    && group.Property is "objects" or "scenarios" or "sheet")))
        {
            return Step41ProtectionLayout;
        }
        return null;
    }

    internal static bool IsDirectPhysicalPage(string reportSlug, string sheetName) =>
        DirectPhysicalPages.TryGetValue(reportSlug, out var pages)
        && pages.Contains(sheetName);

    internal static string SafeColumnRole(string columnKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnKey);
        var position = ScenarioTagPosition(columnKey);
        return position.HasValue
            ? $"scenario-{position.Value}-tag"
            : "source-or-derived-column";
    }

    internal static int? ScenarioTagPosition(string columnKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnKey);
        for (var position = 1; position <= 10; position++)
        {
            if (string.Equals(
                    columnKey,
                    HashHeader($"C{position}_TAG"),
                    StringComparison.Ordinal))
            {
                return position;
            }
        }
        return null;
    }

    private static bool IsStep41(string sheetName) =>
        string.Equals(
            sheetName,
            "step4-1 符合高風險條件傳票明細",
            StringComparison.Ordinal);

    private static bool IsStep41BorderDifference(SpreadsheetAppearanceDifferenceGroup group) =>
        IsStep41(group.SheetName)
        && group.ScopeKind is "cell" or "row" or "column"
        && (group.Property.StartsWith("border.", StringComparison.Ordinal)
            || group.Property.StartsWith("style.border.", StringComparison.Ordinal));

    private static IReadOnlySet<string> Set(params string[] values) =>
        values.ToHashSet(StringComparer.Ordinal);

    private static string HashHeader(string value) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
}
