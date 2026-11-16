using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;

namespace JET.Tests.Infrastructure;

internal enum PrivateCaseReportComparisonFailure
{
    InvalidInput,
    WorkbookUnavailable,
}

internal sealed class PrivateCaseReportComparisonException : InvalidOperationException
{
    internal PrivateCaseReportComparisonException(PrivateCaseReportComparisonFailure failure)
        : base($"無法比較私人案件底稿（{failure}）。")
    {
        Failure = failure;
    }

    internal PrivateCaseReportComparisonFailure Failure { get; }
}

internal sealed record PrivateCaseReportComparisonSummary(
    LegacyReportKind Kind,
    int ComparedContentDimensionCount,
    int ContentDifferenceCount,
    long ContentDifferenceMagnitude,
    int BlockingContentDifferenceCount,
    long BlockingContentDifferenceMagnitude,
    int AppearanceDifferenceCount,
    int UnclassifiedAppearanceDifferenceCount,
    IReadOnlyList<PrivateCaseContentDifferenceSummary> ContentDifferences,
    IReadOnlyList<PrivateCaseContentDifferenceSummary> BlockingContentDifferences,
    IReadOnlyList<PrivateCaseAppearanceDifferenceSummary> AppearanceDifferences,
    IReadOnlyList<PrivateCaseAppearanceDifferenceSummary> UnclassifiedAppearanceDifferences,
    IReadOnlyList<PrivateCaseContentDifferenceLocationSummary> ContentDifferenceLocations,
    IReadOnlyList<PrivateCaseContentDifferenceLocationSummary> BlockingContentDifferenceLocations,
    IReadOnlyList<PrivateCaseContentFamilyCoverageSummary> ContentFamilyCoverage,
    IReadOnlyList<PrivateCaseBlockingContentColumnDetail> BlockingContentColumnDetails,
    IReadOnlyList<PrivateCaseAppearanceDifferenceDetail> AppearanceDifferenceDetails,
    IReadOnlyList<PrivateCaseAppearanceDifferenceDetail> UnclassifiedAppearanceDifferenceDetails,
    IReadOnlyList<PrivateCaseReportDecisionSummary> AcceptedContentDecisions,
    IReadOnlyList<PrivateCaseReportDecisionSummary> AcceptedAppearanceDecisions)
{
    /// <summary>直接比對頁依清單配對對起來的欄數，以及兩邊各自對不起來的欄數。</summary>
    public IReadOnlyList<PrivateCaseColumnAlignmentSummary> ColumnAlignment { get; init; } = [];

    /// <summary>照範本寫的頁逐格比對的結果。</summary>
    public IReadOnlyList<PrivateCaseResultSheetSummary> ResultSheets { get; init; } = [];

    public bool ContentMatches => BlockingContentDifferenceCount == 0;

    public bool AppearanceMatches => UnclassifiedAppearanceDifferenceCount == 0;

    public bool ResultCellsMatch => ResultSheets.All(static sheet => sheet.Matches);

    public bool Passed => ContentMatches && AppearanceMatches && ResultCellsMatch;

    public override string ToString() => $"private case report comparison ({Kind})";
}

internal sealed record PrivateCaseContentDifferenceSummary(
    LegacyAuditParityContentDimension Dimension,
    int EntryCount,
    long DifferenceCount);

internal sealed record PrivateCaseAppearanceDifferenceSummary(
    string ScopeKind,
    string Property,
    int DifferenceCount);

internal sealed record PrivateCaseContentDifferenceLocationSummary(
    string ScopeKind,
    int ScopePosition,
    LegacyAuditParityContentDimension Dimension,
    long DifferenceCount);

internal sealed record PrivateCaseContentFamilyCoverageSummary(
    int FamilyPosition,
    int SharedColumnCount,
    int ValueMatchedColumnCount,
    int UnmatchedExpectedColumnCount,
    int UnmatchedActualColumnCount,
    int ExpectedRowCount,
    int ActualRowCount,
    int DifferingSharedColumnCount,
    int EstimatedDifferingValueCount);

internal sealed record PrivateCaseBlockingContentColumnDetail(
    int FamilyPosition,
    string ColumnRole,
    int ExpectedValueCount,
    int ActualValueCount,
    int EstimatedDifferingValueCount,
    PrivateCaseScenarioTagValueSummary? ScenarioTagValues)
{
    /// <summary>
    /// 兩邊的值差在哪裡，只有固定的分類：<c>sign-flipped</c> 表示 JET 每個值都是舊版的相反數；沒有可說明的關係時是 null。
    /// </summary>
    internal string? ValueRelation { get; init; }
}

internal sealed record PrivateCaseScenarioTagValueSummary(
    int ExpectedYesCount,
    int ExpectedNoCount,
    int ExpectedOtherCount,
    int ActualYesCount,
    int ActualNoCount,
    int ActualOtherCount);

internal sealed record PrivateCaseAppearanceDifferenceDetail(
    int SheetPosition,
    string ScopeKind,
    string Property,
    string ExpectedValueClass,
    string ActualValueClass,
    int DifferenceCount);

internal sealed class PrivateCaseReportComparisonResult
{
    internal PrivateCaseReportComparisonResult(
        IReadOnlyList<PrivateCaseReportComparisonSummary> reports)
    {
        Reports = reports;
    }

    public int ReportCount => Reports.Count;

    public int FailedReportCount => Reports.Count(static report => !report.Passed);

    public bool Passed => FailedReportCount == 0;

    public IReadOnlyList<PrivateCaseReportComparisonSummary> Reports { get; }

    public override string ToString() =>
        $"private case report comparison ({ReportCount} reports)";
}

/// <summary>
/// Compares only run-owned workbook copies. The returned object contains fixed report identities
/// and aggregate counts; worksheet names, cell locations, values, and file paths stay internal.
/// </summary>
internal static class PrivateCaseReportComparator
{
    private const string InfMainSheet = "INF Testing 可靠性測試";
    private const string InfAllFieldsSheet = "可靠性樣本_所有欄位";

    private static readonly IReadOnlySet<string> ExcludedAppearanceSheets =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ReportWorkbookMetadataFormat.WorksheetName,
        };

    internal static PrivateCaseReportComparisonResult Compare(
        PrivateCaseReportPairSet pairs,
        PrivateCaseAcceptancePolicy policy,
        IReadOnlyList<PrivateCaseExplicitContentDecision> explicitContentDecisions,
        IReadOnlyList<PrivateCaseExplicitAppearanceDecision>? explicitAppearanceDecisions = null,
        IReadOnlyList<PrivateCaseReportColumnAlias>? columnAliases = null,
        string? clientName = null)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(explicitContentDecisions);
        IReadOnlyList<PrivateCaseExplicitAppearanceDecision> appearanceDecisions =
            explicitAppearanceDecisions ?? [];
        IReadOnlyList<PrivateCaseReportColumnAlias> aliases = columnAliases ?? [];
        if (pairs.ReportCount != Enum.GetValues<LegacyReportKind>().Length
            || policy.ReportComparison != PrivateCaseReportComparisonMode.ContentAndAppearance
            || policy.InfVerification != PrivateCaseInfVerificationMode.RulesAndGl
            || policy.CleanupMode != PrivateCaseCleanupMode.AlwaysDelete
            || policy.InfSampleSize != PrivateCaseAcceptancePolicy.RequiredInfSampleSize
            || explicitContentDecisions.Any(static decision =>
                !PrivateCaseReportDifferencePolicy.IsSupportedExplicitContentDecision(decision))
            || explicitContentDecisions.Distinct().Count() != explicitContentDecisions.Count
            || appearanceDecisions.Any(static decision =>
                !PrivateCaseReportDifferencePolicy.IsSupportedExplicitAppearanceDecision(decision))
            || appearanceDecisions.Distinct().Count() != appearanceDecisions.Count)
        {
            throw Error(PrivateCaseReportComparisonFailure.InvalidInput);
        }

        try
        {
            var reports = new List<PrivateCaseReportComparisonSummary>(pairs.ReportCount);
            foreach (var pair in pairs.Pairs)
            {
                var slug = ReportSlug(pair.Kind);
                Func<string, int, int, bool>? contentMask = pair.Kind == LegacyReportKind.InfReport
                    ? (sheetName, row, column) => IsRandomInfContent(
                        sheetName,
                        row,
                        column,
                        policy.InfSampleSize)
                    : null;
                var normalizeManualAuto = slug == "working-paper" && PrivateCaseReportDifferencePolicy.NormalizeManualAuto(explicitContentDecisions);
                var expected = LegacyAuditParityNormalizedWorkbookReader.Read(
                    slug,
                    pair.ExpectedFullPath,
                    excludeJetMetadataSheet: true,
                    contentMask, normalizeLegacyManualAuto: normalizeManualAuto);
                var actual = LegacyAuditParityNormalizedWorkbookReader.Read(
                    slug,
                    pair.ActualFullPath,
                    excludeJetMetadataSheet: true,
                    contentMask, preserveManualAutoBlanks: normalizeManualAuto);
                var alignment = PrivateCaseReportColumnAlignment.Apply(slug, expected, actual, aliases);
                expected = alignment.Expected;
                actual = alignment.Actual;
                var content = LegacyAuditParityNormalizedContentComparator.Compare(expected, actual);
                if (normalizeManualAuto) content = PrivateCaseReportDifferencePolicy.RequireExactManualAuto(expected, actual, content);
                var creditTotalSignFlippedFamilies = CreditTotalSignFlippedFamilies(
                    slug,
                    pair,
                    content,
                    alignment.AlignedRoles,
                    aliases);
                var classifiedContent = content.Differences
                    .Select(difference => (
                        Difference: difference,
                        DecisionId: PrivateCaseReportDifferencePolicy.ResolveContent(
                            slug,
                            difference,
                            explicitContentDecisions,
                            creditTotalSignFlippedFamilies)))
                    .ToArray();
                var blockingContent = classifiedContent
                    .Where(static item => item.DecisionId is null)
                    .Select(static item => item.Difference)
                    .ToArray();

                var expectedAppearance = SpreadsheetAppearanceFingerprint.Capture(
                    pair.ExpectedFullPath,
                    ExcludedAppearanceSheets);
                var actualAppearance = SpreadsheetAppearanceFingerprint.Capture(
                    pair.ActualFullPath,
                    ExcludedAppearanceSheets);
                var expectedSheetPositions = PositionMap(
                    expected.Sheets.Select(static sheet => sheet.SheetName));
                var actualSheetPositions = PositionMap(
                    actual.Sheets.Select(static sheet => sheet.SheetName));
                var expectedFamilyPositions = PositionMap(
                    expected.DirectFamilies.Select(static family => family.BaseSheetName));
                var actualFamilyPositions = PositionMap(
                    actual.DirectFamilies.Select(static family => family.BaseSheetName));
                var contentDifferences = content.Differences
                    .GroupBy(static difference => difference.Dimension)
                    .OrderBy(static group => group.Key)
                    .Select(static group => new PrivateCaseContentDifferenceSummary(
                        group.Key,
                        group.Count(),
                        group.Sum(static difference => difference.DifferenceCount)))
                    .ToArray();
                var blockingContentDifferences = blockingContent
                    .GroupBy(static difference => difference.Dimension)
                    .OrderBy(static group => group.Key)
                    .Select(static group => new PrivateCaseContentDifferenceSummary(
                        group.Key,
                        group.Count(),
                        group.Sum(static difference => difference.DifferenceCount)))
                    .ToArray();
                var contentDifferenceLocations = content.Differences
                    .Select(difference => new PrivateCaseContentDifferenceLocationSummary(
                        ContentScopeKind(difference),
                        ContentScopePosition(
                            difference,
                            expectedSheetPositions,
                            actualSheetPositions,
                            expectedFamilyPositions,
                            actualFamilyPositions),
                        difference.Dimension,
                        difference.DifferenceCount))
                    .ToArray();
                var blockingContentDifferenceLocations = blockingContent
                    .Select(difference => new PrivateCaseContentDifferenceLocationSummary(
                        ContentScopeKind(difference),
                        ContentScopePosition(
                            difference,
                            expectedSheetPositions,
                            actualSheetPositions,
                            expectedFamilyPositions,
                            actualFamilyPositions),
                        difference.Dimension,
                        difference.DifferenceCount))
                    .ToArray();
                var acceptedContentDecisions = classifiedContent
                    .Where(static item => item.DecisionId is not null)
                    .GroupBy(static item => item.DecisionId!, StringComparer.Ordinal)
                    .OrderBy(static group => group.Key, StringComparer.Ordinal)
                    .Select(static group => new PrivateCaseReportDecisionSummary(
                        group.Key,
                        group.Count(),
                        group.Sum(static item => item.Difference.DifferenceCount)))
                    .ToArray();
                var contentFamilyCoverage = content.FamilyCoverage
                    .Select(static (family, index) => new PrivateCaseContentFamilyCoverageSummary(
                        index + 1,
                        family.SharedColumnCount,
                        family.ValueMatchedColumnCount,
                        family.UnmatchedExpectedColumnCount,
                        family.UnmatchedActualColumnCount,
                        family.ExpectedRowCount,
                        family.ActualRowCount,
                        family.DifferingSharedColumns.Count,
                        family.DifferingSharedColumns.Sum(
                            static column => column.EstimatedDifferingValues)))
                    .Where(static family => family.ValueMatchedColumnCount > 0
                        || family.UnmatchedExpectedColumnCount > 0
                        || family.UnmatchedActualColumnCount > 0
                        || family.ExpectedRowCount != family.ActualRowCount
                        || family.DifferingSharedColumnCount > 0)
                    .ToArray();
                var rowValueBlockingFamilies = blockingContent
                    .Where(static difference =>
                        difference.Dimension == LegacyAuditParityContentDimension.RowValues)
                    .Select(static difference => difference.SheetName)
                    .ToHashSet(StringComparer.Ordinal);
                var blockingContentColumnDetails = content.FamilyCoverage
                    .SelectMany((family, index) =>
                        rowValueBlockingFamilies.Contains(family.FamilyName)
                            ? family.DifferingSharedColumns.Select(column =>
                                CreateBlockingContentColumnDetail(
                                    pair,
                                    family,
                                    index + 1,
                                    column,
                                    alignment.AlignedRoles.GetValueOrDefault(
                                        (family.FamilyName, column.ColumnKey))) with
                                {
                                    ValueRelation = ValueRelation(expected, actual, family.FamilyName, column.ColumnKey),
                                })
                            : [])
                    .ToArray();
                var rawAppearanceDifferences = expectedAppearance
                    .DescribeDifferenceGroups(actualAppearance);
                var classifiedAppearance = rawAppearanceDifferences
                    .Select(difference => (
                        Difference: difference,
                        DecisionId: PrivateCaseReportDifferencePolicy.ResolveAppearance(
                            slug,
                            difference,
                            rawAppearanceDifferences,
                            appearanceDecisions)))
                    .ToArray();
                var unclassifiedAppearance = classifiedAppearance
                    .Where(static item => item.DecisionId is null)
                    .Select(static item => item.Difference)
                    .ToArray();
                var appearanceDifferences = rawAppearanceDifferences
                    .GroupBy(static difference => (
                        difference.ScopeKind,
                        difference.Property))
                    .OrderByDescending(static group => group.Sum(static difference => difference.Count))
                    .ThenBy(static group => group.Key.ScopeKind, StringComparer.Ordinal)
                    .ThenBy(static group => group.Key.Property, StringComparer.Ordinal)
                    .Select(static group => new PrivateCaseAppearanceDifferenceSummary(
                        group.Key.ScopeKind,
                        group.Key.Property,
                        group.Sum(static difference => difference.Count)))
                    .ToArray();
                var unclassifiedAppearanceDifferences = unclassifiedAppearance
                    .GroupBy(static difference => (
                        difference.ScopeKind,
                        difference.Property))
                    .OrderByDescending(static group => group.Sum(static difference => difference.Count))
                    .ThenBy(static group => group.Key.ScopeKind, StringComparer.Ordinal)
                    .ThenBy(static group => group.Key.Property, StringComparer.Ordinal)
                    .Select(static group => new PrivateCaseAppearanceDifferenceSummary(
                        group.Key.ScopeKind,
                        group.Key.Property,
                        group.Sum(static difference => difference.Count)))
                    .ToArray();
                var appearanceDifferenceDetails = rawAppearanceDifferences
                    .Select(difference => new PrivateCaseAppearanceDifferenceDetail(
                        PositionOf(
                            difference.SheetName,
                            expectedSheetPositions,
                            actualSheetPositions),
                        difference.ScopeKind,
                        difference.Property,
                        SafeAppearanceValue(difference.Property, difference.ExpectedValue),
                        SafeAppearanceValue(difference.Property, difference.ActualValue),
                        difference.Count))
                    .ToArray();
                var unclassifiedAppearanceDifferenceDetails = unclassifiedAppearance
                    .Select(difference => new PrivateCaseAppearanceDifferenceDetail(
                        PositionOf(
                            difference.SheetName,
                            expectedSheetPositions,
                            actualSheetPositions),
                        difference.ScopeKind,
                        difference.Property,
                        SafeAppearanceValue(difference.Property, difference.ExpectedValue),
                        SafeAppearanceValue(difference.Property, difference.ActualValue),
                        difference.Count))
                    .ToArray();
                var acceptedAppearanceDecisions = classifiedAppearance
                    .Where(static item => item.DecisionId is not null)
                    .GroupBy(static item => item.DecisionId!, StringComparer.Ordinal)
                    .OrderBy(static group => group.Key, StringComparer.Ordinal)
                    .Select(static group => new PrivateCaseReportDecisionSummary(
                        group.Key,
                        group.Count(),
                        group.Sum(static item => (long)item.Difference.Count)))
                    .ToArray();

                reports.Add(new PrivateCaseReportComparisonSummary(
                    pair.Kind,
                    content.ComparedDimensionCount,
                    content.Differences.Count,
                    content.Differences.Sum(static difference => difference.DifferenceCount),
                    blockingContent.Length,
                    blockingContent.Sum(static difference => difference.DifferenceCount),
                    expectedAppearance.CountDifferences(actualAppearance),
                    unclassifiedAppearance.Sum(static difference => difference.Count),
                    contentDifferences,
                    blockingContentDifferences,
                    appearanceDifferences,
                    unclassifiedAppearanceDifferences,
                    contentDifferenceLocations,
                    blockingContentDifferenceLocations,
                    contentFamilyCoverage,
                    blockingContentColumnDetails,
                    appearanceDifferenceDetails,
                    unclassifiedAppearanceDifferenceDetails,
                    acceptedContentDecisions,
                    acceptedAppearanceDecisions)
                {
                    ColumnAlignment = alignment.Families,
                    ResultSheets = PrivateCaseResultCellComparison.Compare(
                        slug,
                        pair.ExpectedFullPath,
                        pair.ActualFullPath,
                        contentMask,
                        clientName),
                });
            }

            return new PrivateCaseReportComparisonResult(reports);
        }
        catch (PrivateCaseReportComparisonException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or FileFormatException
            or UnauthorizedAccessException
            or InvalidDataException
            or OpenXmlPackageException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            throw Error(PrivateCaseReportComparisonFailure.WorkbookUnavailable);
        }
    }

    private static bool IsRandomInfContent(
        string sheetName,
        int row,
        int column,
        int sampleSize)
    {
        if (string.Equals(sheetName, InfMainSheet, StringComparison.Ordinal))
        {
            return row is >= 53
                && row <= 52 + sampleSize
                && column is >= 2 and <= 12;
        }

        return string.Equals(sheetName, InfAllFieldsSheet, StringComparison.Ordinal)
            && row >= 2;
    }

    private static IReadOnlyDictionary<string, int> PositionMap(IEnumerable<string> names) =>
        names
            .Select(static (name, index) => (name, position: index + 1))
            .GroupBy(static item => item.name, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().position,
                StringComparer.Ordinal);

    private static string ContentScopeKind(LegacyAuditParityContentDifference difference) =>
        difference.SheetName == "*"
            ? "workbook"
            : difference.Dimension is LegacyAuditParityContentDimension.ColumnHeaders
                or LegacyAuditParityContentDimension.ColumnValuesUnmatched
                or LegacyAuditParityContentDimension.RowValues
                or LegacyAuditParityContentDimension.RowValueSequence
                    ? "family"
                    : "sheet";

    private static int ContentScopePosition(
        LegacyAuditParityContentDifference difference,
        IReadOnlyDictionary<string, int> expectedSheetPositions,
        IReadOnlyDictionary<string, int> actualSheetPositions,
        IReadOnlyDictionary<string, int> expectedFamilyPositions,
        IReadOnlyDictionary<string, int> actualFamilyPositions)
    {
        if (difference.SheetName == "*")
        {
            return 0;
        }
        return ContentScopeKind(difference) == "family"
            ? PositionOf(
                difference.SheetName,
                expectedFamilyPositions,
                actualFamilyPositions)
            : PositionOf(
                difference.SheetName,
                expectedSheetPositions,
                actualSheetPositions);
    }

    private static int PositionOf(
        string name,
        IReadOnlyDictionary<string, int> expected,
        IReadOnlyDictionary<string, int> actual)
    {
        if (expected.TryGetValue(name, out var expectedPosition))
        {
            return expectedPosition;
        }
        return actual.GetValueOrDefault(name, -1);
    }

    // 只說明差異的種類，不放行：JET 每個值都是舊版的相反數時標成 sign-flipped。
    private static string? ValueRelation(
        LegacyAuditParityNormalizedWorkbook expected,
        LegacyAuditParityNormalizedWorkbook actual,
        string familyName,
        string columnKey)
    {
        var left = expected.DirectFamilies
            .FirstOrDefault(family => family.BaseSheetName == familyName)?
            .Columns.FirstOrDefault(column => column.Key == columnKey);
        var right = actual.DirectFamilies
            .FirstOrDefault(family => family.BaseSheetName == familyName)?
            .Columns.FirstOrDefault(column => column.Key == columnKey);
        return left is not null
            && right is not null
            && left.MultisetDigest != right.MultisetDigest
            && left.MultisetDigest == right.NegatedMultisetDigest
                ? "sign-flipped"
                : null;
    }

    // 預篩選彙總頁 R5、R6 的貸方合計，JET 寫正數、舊版寫負數（裁定 2026-11-16-07）。
    // 一頁只有貸方合計那一欄的值不同，而且逐列核對每一列都只差貸方合計的正負號時，才列入放行。
    private static IReadOnlySet<string> CreditTotalSignFlippedFamilies(
        string reportSlug,
        PrivateCaseReportPair pair,
        LegacyAuditParityContentComparisonResult content,
        IReadOnlyDictionary<(string FamilyName, string ColumnKey), string> alignedRoles,
        IReadOnlyList<PrivateCaseReportColumnAlias> aliases)
    {
        var families = new HashSet<string>(StringComparer.Ordinal);
        if (reportSlug != "prescreen-report")
        {
            return families;
        }
        foreach (var family in content.FamilyCoverage)
        {
            if (family.FamilyName is "R5" or "R6"
                && OnlyTheCreditTotalDiffers(family, alignedRoles)
                && EveryRowDiffersOnlyByTheCreditTotalSign(pair, family.FamilyName, aliases))
            {
                families.Add(family.FamilyName);
            }
        }
        return families;
    }

    private static bool OnlyTheCreditTotalDiffers(
        LegacyAuditParityContentFamilyCoverage family,
        IReadOnlyDictionary<(string FamilyName, string ColumnKey), string> alignedRoles) =>
        family.DifferingSharedColumns is [var column]
        && alignedRoles.GetValueOrDefault((family.FamilyName, column.ColumnKey))
            == PrivateCaseReportColumnAlignment.CreditTotalRole;

    /// <summary>
    /// 逐列核對彙總頁：JET 的欄名照清單的欄位對照換成舊版的欄名，貸方合計取相反數，再把每一列兩邊都有的欄組成一筆；
    /// 兩邊的列不看順序要完全相同。只比整欄的值看不出兩列的值互換，所以要重讀兩邊的列。
    /// </summary>
    private static bool EveryRowDiffersOnlyByTheCreditTotalSign(
        PrivateCaseReportPair pair,
        string sheetName,
        IReadOnlyList<PrivateCaseReportColumnAlias> aliases)
    {
        var legacy = ReadSummaryRows(pair.ExpectedFullPath, sheetName);
        var jet = ReadSummaryRows(pair.ActualFullPath, sheetName);
        if (legacy is null || jet is null)
        {
            return false;
        }

        var sheetAliases = aliases
            .Where(alias => alias.ReportSlug == "prescreen-report" && alias.SheetName == sheetName)
            .ToArray();
        var legacyHeaders = legacy.Headers.ToHashSet(StringComparer.Ordinal);
        var jetHeaders = jet.Headers.ToHashSet(StringComparer.Ordinal);
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var alias in sheetAliases)
        {
            // 和欄位對齊相同：JET 的欄名舊版也有時本來就會比；舊版的欄名只對給一個 JET 欄。
            if (!jetHeaders.Contains(alias.JetHeader) || legacyHeaders.Contains(alias.JetHeader))
            {
                continue;
            }
            var legacyHeader = alias.LegacyHeaders.FirstOrDefault(candidate =>
                legacyHeaders.Contains(candidate)
                && !jetHeaders.Contains(candidate)
                && !renames.ContainsValue(candidate));
            if (legacyHeader is not null)
            {
                renames.TryAdd(alias.JetHeader, legacyHeader);
            }
        }

        var creditHeader = sheetAliases
            .FirstOrDefault(static alias => alias.Role == PrivateCaseReportColumnAlignment.CreditTotalRole)?
            .JetHeader;
        if (creditHeader is null || !renames.ContainsKey(creditHeader))
        {
            return false;
        }

        var sharedHeaders = jet.Headers
            .Select(header => renames.GetValueOrDefault(header, header))
            .Where(legacyHeaders.Contains)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var legacyRows = legacy.Rows
            .Select(row => SummaryRowKey(row, sharedHeaders))
            .Order(StringComparer.Ordinal);
        var jetRows = jet.Rows
            .Select(row => row.ToDictionary(
                cell => renames.GetValueOrDefault(cell.Key, cell.Key),
                cell => cell.Key == creditHeader ? NegatedNumber(cell.Value) : cell.Value,
                StringComparer.Ordinal))
            .Select(row => SummaryRowKey(row, sharedHeaders))
            .Order(StringComparer.Ordinal);
        return legacyRows.SequenceEqual(jetRows, StringComparer.Ordinal);
    }

    private sealed record SummaryRows(
        IReadOnlyList<string> Headers,
        IReadOnlyList<IReadOnlyDictionary<string, string>> Rows);

    // 讀一張彙總頁和它的續頁：第 1 列是欄名，第 2 列起每一列是欄名對值。值的正規化和直接比對頁相同，0 和空白不算值。
    private static SummaryRows? ReadSummaryRows(string workbookPath, string baseSheetName)
    {
        using var document = SpreadsheetDocument.Open(workbookPath, false);
        var workbookPart = document.WorkbookPart;
        if (workbookPart?.Workbook?.Sheets is null)
        {
            return null;
        }
        var shared = workbookPart.SharedStringTablePart?.SharedStringTable?
            .Elements<SharedStringItem>()
            .Select(item => string.Concat(item.Descendants<Text>().Select(static text => text.Text)))
            .ToArray() ?? [];
        List<string>? headers = null;
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var sheet in workbookPart.Workbook.Sheets.Elements<Sheet>())
        {
            var sheetName = sheet.Name?.Value;
            if (sheetName is null
                || (!string.Equals(sheetName, baseSheetName, StringComparison.Ordinal)
                    && !sheetName.StartsWith(baseSheetName + " (", StringComparison.Ordinal))
                || sheet.Id?.Value is not { } relationshipId
                || !workbookPart.TryGetPartById(relationshipId, out var part)
                || part is not WorksheetPart worksheetPart)
            {
                continue;
            }

            var columnHeaders = new Dictionary<int, string>();
            using var reader = new OpenXmlPartReader(worksheetPart);
            while (reader.Read())
            {
                if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
                {
                    continue;
                }
                var row = (Row)reader.LoadCurrentElement()!;
                var rowIndex = (int)(row.RowIndex?.Value ?? 0);
                if (rowIndex == 1)
                {
                    foreach (var cell in row.Elements<Cell>())
                    {
                        if (ParseColumn(cell.CellReference?.Value) is { } column)
                        {
                            columnHeaders[column] = ReadCellText(cell, shared).Trim();
                        }
                    }
                    headers ??= columnHeaders.OrderBy(static item => item.Key).Select(static item => item.Value).ToList();
                    continue;
                }
                if (rowIndex < 2)
                {
                    continue;
                }
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var cell in row.Elements<Cell>())
                {
                    if (ParseColumn(cell.CellReference?.Value) is { } column
                        && columnHeaders.TryGetValue(column, out var header)
                        && SummaryValue(cell, shared) is { } value)
                    {
                        values[header] = value;
                    }
                }
                if (values.Count > 0)
                {
                    rows.Add(values);
                }
            }
        }
        return headers is null ? null : new SummaryRows(headers, rows);
    }

    private static string? SummaryValue(Cell cell, IReadOnlyList<string> shared)
    {
        var text = ReadCellText(cell, shared);
        var dataType = cell.DataType?.Value;
        var isText = dataType == CellValues.SharedString
            || dataType == CellValues.InlineString
            || dataType == CellValues.String;
        var value = isText
            ? LegacyAuditParityNormalizedWorkbookReader.NormalizeSemanticValue("text", text).Trim()
            : decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number.ToString("G29", CultureInfo.InvariantCulture)
                : text.Trim();
        return value is "" or "0" ? null : value;
    }

    // 不是數字的貸方合計沒有相反數，換成舊版不會有的值，讓這一列對不上。
    private const string NotANumber = "\u0000not-a-number";

    private static string NegatedNumber(string value) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? (-number).ToString("G29", CultureInfo.InvariantCulture)
            : NotANumber;

    private static string SummaryRowKey(IReadOnlyDictionary<string, string> row, IReadOnlyList<string> headers) =>
        string.Join('\u001e', headers.Select(header => header + '\u001f' + row.GetValueOrDefault(header)));

    private static PrivateCaseBlockingContentColumnDetail CreateBlockingContentColumnDetail(
        PrivateCaseReportPair pair,
        LegacyAuditParityContentFamilyCoverage family,
        int familyPosition,
        LegacyAuditParityContentColumnMismatch column,
        string? alignedRole)
    {
        var scenarioPosition = PrivateCaseReportDifferencePolicy.ScenarioTagPosition(
            column.ColumnKey);
        PrivateCaseScenarioTagValueSummary? tagValues = null;
        if (scenarioPosition.HasValue)
        {
            var expected = ReadScenarioTagCounts(
                pair.ExpectedFullPath,
                family.FamilyName,
                scenarioPosition.Value);
            var actual = ReadScenarioTagCounts(
                pair.ActualFullPath,
                family.FamilyName,
                scenarioPosition.Value);
            if (expected is not null && actual is not null)
            {
                tagValues = new PrivateCaseScenarioTagValueSummary(
                    expected.YesCount,
                    expected.NoCount,
                    expected.OtherCount,
                    actual.YesCount,
                    actual.NoCount,
                    actual.OtherCount);
            }
        }

        return new PrivateCaseBlockingContentColumnDetail(
            familyPosition,
            alignedRole ?? PrivateCaseReportDifferencePolicy.SafeColumnRole(column.ColumnKey),
            column.ExpectedValueCount,
            column.ActualValueCount,
            column.EstimatedDifferingValues,
            tagValues);
    }

    private sealed record ScenarioTagCounts(int YesCount, int NoCount, int OtherCount);

    private static ScenarioTagCounts? ReadScenarioTagCounts(
        string workbookPath,
        string baseSheetName,
        int scenarioPosition)
    {
        using var document = SpreadsheetDocument.Open(workbookPath, false);
        var workbookPart = document.WorkbookPart;
        if (workbookPart?.Workbook?.Sheets is null)
        {
            return null;
        }
        var shared = workbookPart.SharedStringTablePart?.SharedStringTable?
            .Elements<SharedStringItem>()
            .Select(item => string.Concat(
                item.Descendants<Text>().Select(static text => text.Text)))
            .ToArray() ?? [];
        var expectedHeader = $"C{scenarioPosition}_TAG";
        var yes = 0;
        var no = 0;
        var other = 0;
        var found = false;

        foreach (var sheet in workbookPart.Workbook.Sheets.Elements<Sheet>())
        {
            var sheetName = sheet.Name?.Value;
            if (sheetName is null
                || (!string.Equals(sheetName, baseSheetName, StringComparison.Ordinal)
                    && !sheetName.StartsWith(baseSheetName + " (", StringComparison.Ordinal))
                || sheet.Id?.Value is not { } relationshipId
                || !workbookPart.TryGetPartById(relationshipId, out var part)
                || part is not WorksheetPart worksheetPart)
            {
                continue;
            }

            int? tagColumn = null;
            using var reader = new OpenXmlPartReader(worksheetPart);
            while (reader.Read())
            {
                if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
                {
                    continue;
                }
                var row = (Row)reader.LoadCurrentElement()!;
                var rowIndex = (int)(row.RowIndex?.Value ?? 0);
                if (rowIndex == 5)
                {
                    foreach (var cell in row.Elements<Cell>())
                    {
                        if (string.Equals(
                                ReadCellText(cell, shared).Trim(),
                                expectedHeader,
                                StringComparison.Ordinal))
                        {
                            tagColumn = ParseColumn(cell.CellReference?.Value);
                            found = tagColumn.HasValue;
                            break;
                        }
                    }
                    continue;
                }
                if (rowIndex <= 5 || !tagColumn.HasValue)
                {
                    continue;
                }

                var value = row.Elements<Cell>()
                    .Where(cell => ParseColumn(cell.CellReference?.Value) == tagColumn.Value)
                    .Select(cell => ReadCellText(cell, shared).Trim())
                    .FirstOrDefault(static value => value.Length > 0);
                if (value is null)
                {
                    continue;
                }
                if (string.Equals(value, "Y", StringComparison.OrdinalIgnoreCase))
                {
                    yes++;
                }
                else if (string.Equals(value, "N", StringComparison.OrdinalIgnoreCase))
                {
                    no++;
                }
                else
                {
                    other++;
                }
            }
        }

        return found ? new ScenarioTagCounts(yes, no, other) : null;
    }

    private static string ReadCellText(Cell cell, IReadOnlyList<string> shared)
    {
        var raw = cell.CellValue?.InnerText ?? string.Empty;
        var dataType = cell.DataType?.Value;
        if (dataType == CellValues.SharedString
            && int.TryParse(raw, out var index)
            && index >= 0
            && index < shared.Count)
        {
            return shared[index];
        }
        if (dataType == CellValues.InlineString)
        {
            return cell.InlineString is null
                ? string.Empty
                : string.Concat(
                    cell.InlineString.Descendants<Text>()
                        .Select(static text => text.Text));
        }
        return raw;
    }

    private static int? ParseColumn(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }
        var value = 0;
        foreach (var character in reference)
        {
            if (!char.IsAsciiLetter(character))
            {
                break;
            }
            value = checked(value * 26 + char.ToUpperInvariant(character) - 'A' + 1);
        }
        return value == 0 ? null : value;
    }

    internal static string SafeAppearanceValue(string property, string value)
    {
        if (property.EndsWith("numberFormat.code", StringComparison.Ordinal))
        {
            if (string.Equals(value, "General", StringComparison.OrdinalIgnoreCase))
            {
                return "general";
            }
            if (value == "@")
            {
                return "text";
            }
            var lowered = value.ToLowerInvariant();
            if (lowered.Contains('%'))
            {
                return "percentage";
            }
            if ((lowered.Contains('y') || lowered.Contains('d'))
                && lowered.Contains('m'))
            {
                return "date-time";
            }
            if (lowered.Contains('0')
                || lowered.Contains('#')
                || lowered.Contains('?'))
            {
                return "numeric";
            }
            return "other";
        }

        if (property.EndsWith("font.name", StringComparison.Ordinal))
        {
            return value switch
            {
                "Arial" => "arial",
                "Aptos" or "Aptos Narrow" => "aptos",
                "Calibri" => "calibri",
                "Microsoft JhengHei" or "微軟正黑體" => "microsoft-jhenghei",
                "PMingLiU" or "新細明體" => "pmingliu",
                "Tahoma" => "tahoma",
                "Times New Roman" => "times-new-roman",
                _ => "other",
            };
        }

        return IsSafeAppearanceScalar(value) ? value : "other";
    }

    private static bool IsSafeAppearanceScalar(string value) =>
        value.Length is > 0 and <= 32
        && value.All(static character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '-' or '_' or '#');

    private static string ReportSlug(LegacyReportKind kind) => kind switch
    {
        LegacyReportKind.ValidationReport => "validation-report",
        LegacyReportKind.AccountMapping => "account-mapping",
        LegacyReportKind.InfReport => "inf-report",
        LegacyReportKind.PrescreenReport => "prescreen-report",
        LegacyReportKind.CriteriaSelectionReport => "criteria-selection-report",
        LegacyReportKind.WorkingPaper => "working-paper",
        _ => throw Error(PrivateCaseReportComparisonFailure.InvalidInput),
    };

    private static PrivateCaseReportComparisonException Error(
        PrivateCaseReportComparisonFailure failure) => new(failure);
}
