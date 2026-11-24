using ClosedXML.Excel;
using JET.Tests.Application;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class ReportPageAppearanceCoverageTests
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

    internal static readonly string[] NotWrittenPages =
    [
        Page("Pre-screeningReport", "A2"),
        Page("Pre-screeningReport", "A3"),
        Page("Pre-screeningReport", "A4"),
    ];

    [Fact]
    public void RegistryBasePages_AreExactlyPartitionedByOraclePolicy()
    {
        var registryPages = LegacyAppearanceRegistry.Load().ScriptUnspecifiedSheets
            .Select(item => Page(item.Report, item.Worksheet))
            .ToArray();
        var classifiedPages = DirectPhysicalOraclePages
            .Concat(FamilyProjectionPages)
            .Concat(NotWrittenPages)
            .ToArray();

        Assert.Equal(28, registryPages.Length);
        Assert.Equal(28, registryPages.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(18, DirectPhysicalOraclePages.Length);
        Assert.Equal(7, FamilyProjectionPages.Length);
        Assert.Equal(3, NotWrittenPages.Length);
        Assert.Equal(28, classifiedPages.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            registryPages.Order(StringComparer.Ordinal),
            classifiedPages.Order(StringComparer.Ordinal));
    }

    internal static string WorksheetName(string page)
    {
        var separator = page.IndexOf('|');
        if (separator <= 0 || separator == page.Length - 1)
        {
            throw new InvalidDataException("Base-page identity is invalid.");
        }
        return page[(separator + 1)..];
    }

    private static string Page(string report, string worksheet) => $"{report}|{worksheet}";
}

public sealed class PrescreenCustomRuleNotWrittenTests(ReportExportFixture fixture)
    : IClassFixture<ReportExportFixture>
{
    // 舊版的自訂特定摘要、自訂科目借貸組合與自訂特定尾數，JET 的預篩選報告都不寫：摘要頁沒有這三列，也沒有同名的工作表。
    [Fact]
    public async Task CustomPrescreenVariants_AreNeitherSummaryRowsNorWorksheets()
    {
        var response = await fixture.ExportPrescreenReportAsync();
        var path = fixture.ReportPath(response.GetProperty("publishedReport"));
        using var workbook = new XLWorkbook(path);
        var notWrittenNames = ReportPageAppearanceCoverageTests
            .NotWrittenPages
            .Select(ReportPageAppearanceCoverageTests.WorksheetName)
            .ToArray();
        var summary = workbook.Worksheet("Pre-screening_Report");

        Assert.Equal(new[] { "A2", "A3", "A4" }, notWrittenNames);
        Assert.All(
            notWrittenNames,
            name => Assert.DoesNotContain(
                name,
                summary.CellsUsed().Select(cell => cell.GetString())));
        Assert.All(
            notWrittenNames,
            worksheet => Assert.DoesNotContain(
                worksheet,
                workbook.Worksheets.Select(sheet => sheet.Name)));
        Assert.Equal(14, summary.LastRowUsed()!.RowNumber());
        Assert.Equal("# 分錄無摘要描述(即空白摘要)", summary.Cell("B14").GetString());
    }
}
