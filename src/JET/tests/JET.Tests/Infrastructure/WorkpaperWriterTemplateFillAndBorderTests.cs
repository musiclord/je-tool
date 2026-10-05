using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;
using static JET.Tests.Infrastructure.WorkpaperWriterTestSupport;

namespace JET.Tests.Infrastructure;

/// <summary>底稿寫出器：資料列保留範本底色與框線，並與兩份已追蹤的舊版快照一致。</summary>
public sealed class WorkpaperWriterTemplateFillAndBorderTests
{
    [Fact]
    public async Task Step2To4_DynamicDataCells_PreserveTemplateFillAndBorders()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("filter.commit", MatrixScenarioPayload());
        var outputBytes = await WriteToBytesAsync(host, ctx.ProjectId);
        var templatePath = Path.Combine(
            AppContext.BaseDirectory,
            "Templates",
            "WorkingPaper.xlsx");

        using var template = SpreadsheetDocument.Open(templatePath, false);
        using var output = SpreadsheetDocument.Open(
            new MemoryStream(outputBytes, writable: false),
            false);

        AssertTemplateFillAndBorder(
            template,
            output,
            Step2Sheet,
            ["A53", "F53", "M53", "T53"]);
        AssertTemplateFillAndBorder(
            template,
            output,
            Step3Sheet,
            ["B19", "C19", "D19", "E19"]);
        AssertTemplateFillAndBorder(
            template,
            output,
            Step4Sheet,
            ["A13", "E13", "F13", "O13"]);
    }

    [Fact]
    public async Task Step4_ContinuationFirstDataRow_PreservesTemplateFillAndBorders()
    {
        const int voucherCount = 9;
        const uint continuationRowLimit = 20;
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationMatrixAsync(host, voucherCount);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());

        await using var stream = new MemoryStream();
        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context);
        var writer = (IWorkpaperPlanWriter)BuildWriter(
            host,
            new WorkpaperWriterOptions(continuationRowLimit));
        var stats = await writer.WriteAsync(
            stream,
            context,
            plan,
            CancellationToken.None);
        var continuationSheetName = ExcelWorksheetConstraints.ContinuationSheetName(Step4Sheet, 2);
        var continuationStats = Assert.Single(
            stats.SheetStats,
            stat => string.Equals(
                stat.SheetName,
                continuationSheetName,
                StringComparison.Ordinal));
        Assert.Equal(1L, continuationStats.RowsWritten);

        var templatePath = Path.Combine(
            AppContext.BaseDirectory,
            "Templates",
            "WorkingPaper.xlsx");
        using var template = SpreadsheetDocument.Open(templatePath, false);
        stream.Position = 0;
        using var output = SpreadsheetDocument.Open(stream, false);

        AssertTemplateFillAndBorder(
            template,
            output,
            Step4Sheet,
            ["A13", "E13", "F13", "O13", "P13", "U13"],
            continuationSheetName);
        AssertTemplateCellFormats(
            template,
            output,
            Step4Sheet,
            ["A1", "A5", "B5", "A11", "E11"],
            continuationSheetName);
    }

    [Fact]
    public async Task Step4_FirstPageRowsBeyondTemplateExtent_PreserveTemplateFillAndBorders()
    {
        const int voucherCount = 989;
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationMatrixAsync(host, voucherCount);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());
        var outputBytes = await WriteToBytesAsync(host, projectId);
        var templatePath = Path.Combine(
            AppContext.BaseDirectory,
            "Templates",
            "WorkingPaper.xlsx");

        using var template = SpreadsheetDocument.Open(templatePath, false);
        using var output = SpreadsheetDocument.Open(
            new MemoryStream(outputBytes, writable: false),
            false);
        foreach (var column in new[] { "A", "E", "F", "P", "U" })
        {
            AssertTemplateFillAndBorderAt(
                template,
                output,
                Step4Sheet,
                expectedReference: $"{column}13",
                outputReference: $"{column}1001");
        }
    }

    [Fact]
    public async Task Step2To4_FillAndBorders_MatchBothTrackedLegacyGoldenSnapshots()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("filter.commit", MatrixScenarioPayload());
        var outputBytes = await WriteToBytesAsync(host, ctx.ProjectId);
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"jet-working-paper-appearance-{Guid.NewGuid():N}.xlsx");
        var targetLocations = new HashSet<string>(StringComparer.Ordinal)
        {
            $"{Step2Sheet}!A53",
            $"{Step2Sheet}!F53",
            $"{Step2Sheet}!M53",
            $"{Step2Sheet}!T53",
            $"{Step3Sheet}!B19",
            $"{Step3Sheet}!C19",
            $"{Step3Sheet}!D19",
            $"{Step3Sheet}!E19",
            $"{Step4Sheet}!A13",
            $"{Step4Sheet}!E13",
            $"{Step4Sheet}!F13",
            $"{Step4Sheet}!O13"
        };

        try
        {
            await File.WriteAllBytesAsync(outputPath, outputBytes);
            var actual = SpreadsheetAppearanceFingerprint.Capture(outputPath).Entries
                .Where(entry => targetLocations.Contains(entry.Location)
                    && (entry.Property.StartsWith("fill.", StringComparison.Ordinal)
                        || entry.Property.StartsWith("border.", StringComparison.Ordinal)))
                .ToArray();
            var fixtureDirectory = Path.Combine(
                TestRepositoryPaths.RepositoryRoot,
                SpreadsheetAppearanceSnapshotSchemaGuard.FixtureRelativeDirectory.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            var workingPaperSnapshots = SpreadsheetAppearanceSnapshotSchemaGuard.ExpectedSnapshots
                .Where(identity => identity.Report == LegacyReportKind.WorkingPaper)
                .ToArray();
            Assert.Equal(2, workingPaperSnapshots.Length);

            foreach (var identity in workingPaperSnapshots)
            {
                var golden = SpreadsheetAppearanceSnapshotSchemaGuard.ReadEntries(
                        await File.ReadAllBytesAsync(Path.Combine(fixtureDirectory, identity.FileName)),
                        identity.Report)
                    .Where(entry => targetLocations.Contains(entry.Location)
                        && (entry.Property.StartsWith("fill.", StringComparison.Ordinal)
                            || entry.Property.StartsWith("border.", StringComparison.Ordinal)))
                    .ToArray();
                Assert.NotEmpty(golden);
                Assert.Equal(golden, actual);
            }
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

}
