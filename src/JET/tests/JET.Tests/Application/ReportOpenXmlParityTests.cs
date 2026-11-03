using System.IO.Compression;
using System.Xml.Linq;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// Structural parity guard for the fixed, self-contained six-report journey.
/// Existing report tests remain the business oracle for individual cells and
/// conditional sheets; this class proves a same-source re-export is stable after
/// normalizing package-only volatility.
/// </summary>
public sealed class ReportOpenXmlParityTests(ReportExportFixture fixture)
    : IClassFixture<ReportExportFixture>
{
    private static readonly string[] ExpectedKinds =
    [
        ReportKindValues.AccountMapping,
        ReportKindValues.CriteriaSelectionReport,
        ReportKindValues.InfReport,
        ReportKindValues.PrescreenReport,
        ReportKindValues.ValidationReport,
        ReportKindValues.WorkingPaper
    ];

    [Fact]
    public async Task ReexportingSameRunRevisionAndSelection_PreservesAllSixNormalizedWorkbooks()
    {
        var first = await ExportAllSnapshotsAsync();
        var second = await ExportAllSnapshotsAsync();

        Assert.Equal(ExpectedKinds, first.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(ExpectedKinds, second.Keys.Order(StringComparer.Ordinal).ToArray());

        foreach (var kind in ExpectedKinds)
        {
            var difference = first[kind].DescribeDifference(second[kind]);
            Assert.True(difference is null, $"{kind}: {difference}");
        }
    }

    [Fact]
    public async Task NormalizedSnapshot_DetectsNonVolatilePackagePartMutation()
    {
        var exported = await fixture.ExportValidationReportsAsync();
        var report = exported
            .GetProperty("publishedReports")
            .EnumerateArray()
            .Single(item => string.Equals(
                item.GetProperty("kind").GetString(),
                ReportKindValues.ValidationReport,
                StringComparison.Ordinal));
        var originalPath = fixture.ReportPath(report);
        var mutatedPath = Path.Combine(
            Path.GetDirectoryName(originalPath)!,
            $"phase2-openxml-mutation-{Guid.NewGuid():N}.xlsx");
        File.Copy(originalPath, mutatedPath);

        try
        {
            var expected = NormalizedOpenXmlWorkbookSnapshot.Capture(originalPath);
            using (var archive = ZipFile.Open(mutatedPath, ZipArchiveMode.Update))
            {
                var entry = archive.GetEntry("xl/styles.xml")
                    ?? throw new InvalidDataException(
                        "Validation workbook has no xl/styles.xml package part.");
                XDocument document;
                using (var input = entry.Open())
                {
                    document = XDocument.Load(input, LoadOptions.PreserveWhitespace);
                }

                document.Root!.SetAttributeValue("phase2ParityProbe", "changed");
                using var output = entry.Open();
                output.SetLength(0);
                document.Save(output, SaveOptions.DisableFormatting);
            }

            var actual = NormalizedOpenXmlWorkbookSnapshot.Capture(mutatedPath);
            var difference = expected.DescribeDifference(actual);

            Assert.NotNull(difference);
            Assert.Contains("xl/styles.xml", difference, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(mutatedPath);
        }
    }

    [Fact]
    public async Task NormalizedSnapshot_DoesNotTreatUnqualifiedRIdValueAsRelationshipReference()
    {
        var exported = await fixture.ExportValidationReportsAsync();
        var report = exported
            .GetProperty("publishedReports")
            .EnumerateArray()
            .Single(item => string.Equals(
                item.GetProperty("kind").GetString(),
                ReportKindValues.ValidationReport,
                StringComparison.Ordinal));
        var originalPath = fixture.ReportPath(report);
        var expectedPath = Path.Combine(
            Path.GetDirectoryName(originalPath)!,
            $"phase2-openxml-rid-expected-{Guid.NewGuid():N}.xlsx");
        var actualPath = Path.Combine(
            Path.GetDirectoryName(originalPath)!,
            $"phase2-openxml-rid-actual-{Guid.NewGuid():N}.xlsx");
        File.Copy(originalPath, expectedPath);
        File.Copy(originalPath, actualPath);

        try
        {
            AddEquivalentRelationshipAndLiteralAttribute(
                expectedPath,
                "rIdPhase2LiteralA");
            AddEquivalentRelationshipAndLiteralAttribute(
                actualPath,
                "rIdPhase2LiteralB");

            var expected = NormalizedOpenXmlWorkbookSnapshot.Capture(expectedPath);
            var actual = NormalizedOpenXmlWorkbookSnapshot.Capture(actualPath);
            var difference = expected.DescribeDifference(actual);

            Assert.NotNull(difference);
            Assert.Contains("xl/workbook.xml", difference, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(expectedPath);
            File.Delete(actualPath);
        }
    }

    private static void AddEquivalentRelationshipAndLiteralAttribute(
        string path,
        string relationshipId)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var relationshipsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels")
            ?? throw new InvalidDataException(
                "Validation workbook has no xl/_rels/workbook.xml.rels package part.");
        var relationships = LoadXml(relationshipsEntry);
        var template = relationships.Root?.Elements().FirstOrDefault()
            ?? throw new InvalidDataException(
                "Validation workbook has no workbook relationship to duplicate.");
        var duplicate = new XElement(
            template.Name,
            new XAttribute("Id", relationshipId),
            new XAttribute(
                "Type",
                (string?)template.Attribute("Type")
                    ?? throw new InvalidDataException("Workbook relationship has no Type.")),
            new XAttribute(
                "Target",
                (string?)template.Attribute("Target")
                    ?? throw new InvalidDataException("Workbook relationship has no Target.")));
        var targetMode = template.Attribute("TargetMode");
        if (targetMode is not null)
        {
            duplicate.Add(new XAttribute("TargetMode", targetMode.Value));
        }

        relationships.Root!.Add(duplicate);
        RewriteXml(relationshipsEntry, relationships);

        var workbookEntry = archive.GetEntry("xl/workbook.xml")
            ?? throw new InvalidDataException(
                "Validation workbook has no xl/workbook.xml package part.");
        var workbook = LoadXml(workbookEntry);
        workbook.Root!.SetAttributeValue("phase2LiteralRelationshipId", relationshipId);
        RewriteXml(workbookEntry, workbook);
    }

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        return XDocument.Load(input, LoadOptions.PreserveWhitespace);
    }

    private static void RewriteXml(ZipArchiveEntry entry, XDocument document)
    {
        using var output = entry.Open();
        output.SetLength(0);
        document.Save(output, SaveOptions.DisableFormatting);
    }

    private async Task<IReadOnlyDictionary<string, NormalizedOpenXmlWorkbookSnapshot>>
        ExportAllSnapshotsAsync()
    {
        var result = new Dictionary<string, NormalizedOpenXmlWorkbookSnapshot>(
            StringComparer.Ordinal);

        var validation = await fixture.ExportValidationReportsAsync();
        foreach (var report in validation.GetProperty("publishedReports").EnumerateArray())
        {
            AddSnapshot(result, report.GetProperty("kind").GetString()!, report);
        }

        var prescreen = await fixture.ExportPrescreenReportAsync();
        var prescreenReport = prescreen.GetProperty("publishedReport");
        AddSnapshot(result, ReportKindValues.PrescreenReport, prescreenReport);

        var criteria = await fixture.ExportCriteriaSelectionReportAsync();
        var criteriaReport = criteria.GetProperty("publishedReport");
        AddSnapshot(result, ReportKindValues.CriteriaSelectionReport, criteriaReport);

        var workingPaper = await fixture.ExportWorkingPaperAsync();
        var workingPaperReport = workingPaper.GetProperty("publishedReport");
        AddSnapshot(result, ReportKindValues.WorkingPaper, workingPaperReport);

        // 科目配對範本是工作檔，直接以回傳的完整路徑取快照；重新產生後內容同樣要穩定。
        var templatePath = await fixture.ExportAccountMappingTemplatePathAsync();
        Assert.True(
            result.TryAdd(
                ReportKindValues.AccountMapping,
                NormalizedOpenXmlWorkbookSnapshot.Capture(templatePath)),
            "Duplicate report kind 'accountMapping'.");

        return result;
    }

    private void AddSnapshot(
        IDictionary<string, NormalizedOpenXmlWorkbookSnapshot> snapshots,
        string kind,
        System.Text.Json.JsonElement report)
    {
        var snapshot = NormalizedOpenXmlWorkbookSnapshot.Capture(fixture.ReportPath(report));
        Assert.True(snapshots.TryAdd(kind, snapshot), $"Duplicate report kind '{kind}'.");
    }
}
