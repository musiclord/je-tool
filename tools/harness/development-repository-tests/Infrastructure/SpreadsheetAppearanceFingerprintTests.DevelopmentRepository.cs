using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml;
using JET.Tests.TestInfrastructure;
using SpreadsheetColor = DocumentFormat.OpenXml.Spreadsheet.Color;
using SpreadsheetFont = DocumentFormat.OpenXml.Spreadsheet.Font;
using System.Text.RegularExpressions;
using System.Text;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed partial class SpreadsheetAppearanceFingerprintTests
{
    private static readonly string[] ReportTemplateNames =
    [
        "AccountMapping.xlsx",
        "CriteriaSelectionReport.xlsx",
        "INFReport.xlsx",
        "PrescreeningReport.xlsx",
        "ValidationReport.xlsx",
        "WorkingPaper.xlsx"
    ];

    [Fact]
    [Trait(TestProfileTraits.Key, TestProfileTraits.DevelopmentRepository)]
    public void SixReportTemplates_ProduceByteIdenticalJsonOnRepeatedCapture()
    {
        var dataRoot = Path.Combine(TestRepositoryPaths.RepositoryRoot, "data");

        foreach (var templateName in ReportTemplateNames)
        {
            var path = Path.Combine(dataRoot, templateName);
            Assert.True(File.Exists(path), $"Missing report template: data/{templateName}");

            var first = SpreadsheetAppearanceFingerprint.Capture(path);
            var second = SpreadsheetAppearanceFingerprint.Capture(path);

            Assert.NotEmpty(first.Entries);
            Assert.Equal(first.JsonBytes, second.JsonBytes);
            Assert.Null(first.DescribeFirstDifference(second));
        }
    }
}
