using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using JET.Tests.TestInfrastructure;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace JET.Tests.Application;

public sealed partial class ReportExportTests
{
    private static readonly string[] EmbeddedTemplateNames =
    [
        "AccountMapping.xlsx",
        "CriteriaSelectionReport.xlsx",
        "INFReport.xlsx",
        "PrescreeningReport.xlsx",
        "ValidationReport.xlsx",
        "WorkingPaper.xlsx",
        "Holiday2025TW.xlsx",
        "MakeUpDay2025TW.xlsx"
    ];

    private static readonly HashSet<string> TemplateHygieneExceptions =
    [
        "INFReport.xlsx",
        "WorkingPaper.xlsx"
    ];

    [Fact]
    [Trait(TestProfileTraits.Key, TestProfileTraits.DevelopmentRepository)]
    public void RuntimeTemplateAssets_MatchSourceAnchors_ExceptClosedHygienePair()
    {
        var sourceDirectory = Path.Combine(TestRepositoryPaths.RepositoryRoot, "data");
        var embeddedDirectory = Path.Combine(TestRepositoryPaths.SolutionRoot, "JET", "Templates");

        Assert.Equal(2, TemplateHygieneExceptions.Count);
        Assert.All(EmbeddedTemplateNames.Where(fileName => !TemplateHygieneExceptions.Contains(fileName)), fileName =>
        {
            var sourcePath = Path.Combine(sourceDirectory, fileName);
            var embeddedPath = Path.Combine(embeddedDirectory, fileName);
            Assert.True(File.Exists(sourcePath), $"Missing repository template source: data/{fileName}");
            Assert.True(
                File.Exists(embeddedPath),
                $"Missing embedded template asset: JET/Templates/{fileName}");
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(embeddedPath))));
        });
    }
}
