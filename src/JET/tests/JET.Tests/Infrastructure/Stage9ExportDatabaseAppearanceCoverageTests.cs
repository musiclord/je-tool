using ClosedXML.Excel;
using JET.Domain;
using JET.Tests.Application;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class Stage9ExportDatabaseAppearanceCoverageTests
{
    private static readonly string[] DirectPhysicalOraclePages =
    [
        Page("ValidationReport", "V_Report 3"),
        Page("ValidationReport", "V_Report 4"),
        Page("ValidationReport", "V_Report 5"),
        Page("INF_Report", "可靠性樣本_所有欄位"),
        Page("Pre-screeningReport", "R1"),
        Page("Pre-screeningReport", "R2"),
        Page("Pre-screeningReport", "R3"),
        Page("Pre-screeningReport", "R4"),
        Page("Pre-screeningReport", "R5"),
        Page("Pre-screeningReport", "R6"),
        Page("Pre-screeningReport", "R7"),
        Page("CriteriaSelectionReport", "#Criteria Select 1"),
        Page("CriteriaSelectionReport", "#Criteria Select 2"),
        Page("CriteriaSelectionReport", "#Criteria Select 3"),
        Page("CriteriaSelectionReport", "#Criteria Select 4"),
        Page("CriteriaSelectionReport", "#Criteria Select 5"),
        Page("CriteriaSelectionReport", "#Criteria Select 6"),
        Page("WorkingPaper", "step4-1 符合高風險條件傳票明細"),
    ];

    private static readonly string[] FamilyProjectionPages =
    [
        Page("ValidationReport", "V_Report 1"),
        Page("ValidationReport", "V_Report 2"),
        Page("ValidationReport", "V_Report 6"),
        Page("CriteriaSelectionReport", "#Criteria Select 7"),
        Page("CriteriaSelectionReport", "#Criteria Select 8"),
        Page("CriteriaSelectionReport", "#Criteria Select 9"),
        Page("CriteriaSelectionReport", "#Criteria Select 10"),
    ];

    internal static readonly string[] SummaryOnlyNoSheetPages =
    [
        Page("Pre-screeningReport", "A2"),
        Page("Pre-screeningReport", "A3"),
        Page("Pre-screeningReport", "A4"),
    ];

    private static readonly IReadOnlyDictionary<string, string[]> DirectPhysicalOraclePagesByCase =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["case-A"] =
            [
                Page("ValidationReport", "V_Report 3"),
                Page("ValidationReport", "V_Report 4"),
                Page("ValidationReport", "V_Report 5"),
                Page("INF_Report", "可靠性樣本_所有欄位"),
                Page("Pre-screeningReport", "R1"),
                Page("Pre-screeningReport", "R2"),
                Page("Pre-screeningReport", "R3"),
                Page("Pre-screeningReport", "R4"),
                Page("Pre-screeningReport", "R5"),
                Page("Pre-screeningReport", "R6"),
                Page("Pre-screeningReport", "R7"),
                Page("CriteriaSelectionReport", "#Criteria Select 1"),
                Page("CriteriaSelectionReport", "#Criteria Select 2"),
                Page("CriteriaSelectionReport", "#Criteria Select 3"),
                Page("CriteriaSelectionReport", "#Criteria Select 4"),
                Page("CriteriaSelectionReport", "#Criteria Select 5"),
                Page("CriteriaSelectionReport", "#Criteria Select 6"),
                Page("WorkingPaper", "step4-1 符合高風險條件傳票明細"),
            ],
            ["case-B"] =
            [
                Page("ValidationReport", "V_Report 5"),
                Page("INF_Report", "可靠性樣本_所有欄位"),
                Page("Pre-screeningReport", "R2"),
                Page("Pre-screeningReport", "R4"),
                Page("Pre-screeningReport", "R5"),
                Page("Pre-screeningReport", "R6"),
                Page("CriteriaSelectionReport", "#Criteria Select 1"),
                Page("CriteriaSelectionReport", "#Criteria Select 2"),
                Page("WorkingPaper", "step4-1 符合高風險條件傳票明細"),
            ],
        };

    [Fact]
    public void RegistryBasePages_AreExactlyPartitionedByTheStage9OraclePolicy()
    {
        var registryPages = LegacyAppearanceRegistry.Load().ScriptUnspecifiedSheets
            .Select(item => Page(item.Report, item.Worksheet))
            .ToArray();
        var classifiedPages = DirectPhysicalOraclePages
            .Concat(FamilyProjectionPages)
            .Concat(SummaryOnlyNoSheetPages)
            .ToArray();

        Assert.Equal(28, registryPages.Length);
        Assert.Equal(28, registryPages.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(18, DirectPhysicalOraclePages.Length);
        Assert.Equal(7, FamilyProjectionPages.Length);
        Assert.Equal(3, SummaryOnlyNoSheetPages.Length);
        Assert.Equal(28, classifiedPages.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            registryPages.Order(StringComparer.Ordinal),
            classifiedPages.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TrackedCaseAliases_HaveTheExactDirectPhysicalOracleCoverage()
    {
        SpreadsheetAppearanceSnapshotSchemaGuard.ValidateRepositoryInventory(
            TestRepositoryPaths.RepositoryRoot);

        Assert.Equal(
            new[] { "case-A", "case-B" },
            DirectPhysicalOraclePagesByCase.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(18, DirectPhysicalOraclePagesByCase["case-A"].Length);
        Assert.Equal(9, DirectPhysicalOraclePagesByCase["case-B"].Length);
        Assert.Equal(
            DirectPhysicalOraclePages.Order(StringComparer.Ordinal),
            DirectPhysicalOraclePagesByCase.Values
                .SelectMany(pages => pages)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
        foreach (var (caseAlias, expectedPages) in DirectPhysicalOraclePagesByCase)
        {
            Assert.Equal(
                expectedPages.Order(StringComparer.Ordinal),
                TrackedBasePages(caseAlias).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void TrackedCases_ConfirmTheConditionalStep13PageWasNotPhysicallyObserved()
    {
        SpreadsheetAppearanceSnapshotSchemaGuard.ValidateRepositoryInventory(
            TestRepositoryPaths.RepositoryRoot);

        foreach (var caseAlias in new[] { "case-A", "case-B" })
        {
            var sheets = TrackedFingerprint(caseAlias, LegacyReportKind.WorkingPaper)
                .Entries
                .Select(entry => SheetName(entry.Location))
                .Where(name => name is not null)
                .Select(name => name!)
                .ToHashSet(StringComparer.Ordinal);
            Assert.DoesNotContain(WorkpaperSheetCatalog.Step13, sheets);
        }
    }

    internal static string WorksheetName(string page)
    {
        var separator = page.IndexOf('|');
        if (separator <= 0 || separator == page.Length - 1)
        {
            throw new InvalidDataException("Stage-9 base-page identity is invalid.");
        }
        return page[(separator + 1)..];
    }

    private static IReadOnlyList<string> TrackedBasePages(string caseAlias)
    {
        var sheetsByReport = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var report in LegacyAppearanceRegistry.Load().ScriptUnspecifiedSheets
                     .Select(item => item.Report)
                     .Distinct(StringComparer.Ordinal))
        {
            var kind = ReportKind(report);
            var fingerprint = TrackedFingerprint(caseAlias, kind);
            sheetsByReport.Add(
                report,
                fingerprint.Entries
                    .Select(entry => SheetName(entry.Location))
                    .Where(name => name is not null)
                    .Select(name => name!)
                    .ToHashSet(StringComparer.Ordinal));
        }

        return LegacyAppearanceRegistry.Load().ScriptUnspecifiedSheets
            .Where(item => sheetsByReport[item.Report].Contains(item.Worksheet))
            .Select(item => Page(item.Report, item.Worksheet))
            .ToArray();
    }

    private static SpreadsheetAppearanceFingerprint TrackedFingerprint(
        string caseAlias,
        LegacyReportKind kind)
    {
        var identity = Assert.Single(
            SpreadsheetAppearanceSnapshotSchemaGuard.ExpectedSnapshots,
            item => item.Report == kind
                && item.FileName.StartsWith(caseAlias + "-", StringComparison.Ordinal));
        var fixtureDirectory = Path.Combine(
            TestRepositoryPaths.RepositoryRoot,
            SpreadsheetAppearanceSnapshotSchemaGuard.FixtureRelativeDirectory.Replace(
                '/',
                Path.DirectorySeparatorChar));
        return SpreadsheetAppearanceFingerprint.LoadJson(
            File.ReadAllBytes(Path.Combine(fixtureDirectory, identity.FileName)),
            kind);
    }

    private static LegacyReportKind ReportKind(string report) => report switch
    {
        "ValidationReport" => LegacyReportKind.ValidationReport,
        "INF_Report" => LegacyReportKind.InfReport,
        "Pre-screeningReport" => LegacyReportKind.PrescreenReport,
        "CriteriaSelectionReport" => LegacyReportKind.CriteriaSelectionReport,
        "WorkingPaper" => LegacyReportKind.WorkingPaper,
        _ => throw new InvalidDataException("Stage-9 registry report is outside the closed catalog."),
    };

    private static string? SheetName(string location)
    {
        if (string.Equals(location, "$workbook", StringComparison.Ordinal))
        {
            return null;
        }
        var separator = location.IndexOf('!');
        if (separator <= 0)
        {
            throw new InvalidDataException("Stage-9 snapshot location is invalid.");
        }
        return location[..separator];
    }

    private static string Page(string report, string worksheet) => $"{report}|{worksheet}";
}

public sealed class Stage9PrescreenSummaryOnlyOutcomeTests(ReportArtifactExportFixture fixture)
    : IClassFixture<ReportArtifactExportFixture>
{
    [Fact]
    public async Task CustomPrescreenVariants_RemainSummaryOnlyAndNeverCreateWorksheets()
    {
        var response = await fixture.ExportPrescreenReportAsync();
        var path = fixture.ArtifactPath(response.GetProperty("artifact"));
        using var workbook = new XLWorkbook(path);
        var summaryOnlyNames = Stage9ExportDatabaseAppearanceCoverageTests
            .SummaryOnlyNoSheetPages
            .Select(Stage9ExportDatabaseAppearanceCoverageTests.WorksheetName)
            .ToArray();

        Assert.Equal(
            summaryOnlyNames,
            workbook.Worksheet("Pre-screening_Report")
                .Range("B14:B16")
                .Cells()
                .Select(cell => cell.GetString())
                .ToArray());
        Assert.All(
            summaryOnlyNames,
            worksheet => Assert.DoesNotContain(
                worksheet,
                workbook.Worksheets.Select(sheet => sheet.Name)));
        Assert.All(
            workbook.Worksheet("Pre-screening_Report").Range("E14:F16").Cells(),
            cell => Assert.Equal("N/A", cell.GetString()));
    }
}
