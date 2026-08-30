using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class PrivateCaseExpectedReportSetTests
{
    [Fact]
    public void Create_CompleteSyntheticReports_CopiesSafeNamesAndCleansOnlyOwnedFiles()
    {
        using var fixture = new SyntheticFixture();
        string[] copiedPaths;

        using (var workspace = PrivateCaseInputWorkspace.Create(fixture.OwnedRoot))
        {
            var result = PrivateCaseExpectedReportSet.Create(
                fixture.Manifest,
                fixture.SourceRoot,
                workspace);

            Assert.Equal(6, result.ReportCount);
            Assert.Equal(Enum.GetValues<LegacyReportKind>(), result.ReportKinds);
            Assert.Equal(
                Enumerable.Range(1, 6).Select(index => $"expected-report-{index:000}.xlsx"),
                result.Reports.Select(report => report.FileName));
            Assert.All(result.Reports, report => Assert.True(File.Exists(report.FullPath)));
            for (var index = 0; index < result.Reports.Count; index++)
            {
                Assert.Equal(fixture.ReportBytes[index], File.ReadAllBytes(result.Reports[index].FullPath));
            }

            var serialized = JsonSerializer.Serialize(result);
            var serializedReport = JsonSerializer.Serialize(result.Reports[0]);
            using var setDocument = JsonDocument.Parse(serialized);
            using var reportDocument = JsonDocument.Parse(serializedReport);
            Assert.Equal(2, setDocument.RootElement.EnumerateObject().Count());
            Assert.Single(reportDocument.RootElement.EnumerateObject());
            Assert.DoesNotContain(fixture.SourceRoot, serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(workspace.Path, serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(result.Reports[0].FullPath, serializedReport, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("private case expected report set", result.ToString(), StringComparison.Ordinal);
            Assert.StartsWith("private case expected report", result.Reports[0].ToString(), StringComparison.Ordinal);
            copiedPaths = result.Reports.Select(report => report.FullPath).ToArray();
        }

        Assert.All(copiedPaths, path => Assert.False(File.Exists(path)));
        Assert.All(fixture.SourcePaths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void Create_MissingPrivateReport_FailsWithoutEchoingThePath()
    {
        using var fixture = new SyntheticFixture();
        File.Delete(fixture.SourcePaths[0]);
        using var workspace = PrivateCaseInputWorkspace.Create(fixture.OwnedRoot);

        var error = Assert.Throws<PrivateCaseExpectedReportException>(() =>
            PrivateCaseExpectedReportSet.Create(
                fixture.Manifest,
                fixture.SourceRoot,
                workspace));

        Assert.Equal(PrivateCaseExpectedReportFailure.PrivateFileUnavailable, error.Failure);
        Assert.DoesNotContain(fixture.SourceRoot, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-reference", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_IncompleteManifest_FailsBeforeOpeningAnyReport()
    {
        using var fixture = new SyntheticFixture(omitLastReport: true);
        using var workspace = PrivateCaseInputWorkspace.Create(fixture.OwnedRoot);

        var error = Assert.Throws<PrivateCaseExpectedReportException>(() =>
            PrivateCaseExpectedReportSet.Create(
                fixture.Manifest,
                fixture.SourceRoot,
                workspace));

        Assert.Equal(PrivateCaseExpectedReportFailure.InvalidInput, error.Failure);
        Assert.Empty(Directory.EnumerateFiles(workspace.Path));
    }

    private sealed class SyntheticFixture : IDisposable
    {
        internal SyntheticFixture(bool omitLastReport = false)
        {
            Root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"jet-private-expected-reports-{Guid.NewGuid():N}");
            SourceRoot = System.IO.Path.Combine(Root, "source");
            OwnedRoot = System.IO.Path.Combine(Root, "owned");
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(OwnedRoot);

            var kinds = Enum.GetValues<LegacyReportKind>();
            ReportBytes = kinds
                .Select((_, index) => new byte[] { 80, 75, 3, 4, (byte)(index + 1) })
                .ToArray();
            SourcePaths = kinds
                .Select((_, index) => Write($"private-reference-{index + 1:000}.xlsx", ReportBytes[index]))
                .ToArray();
            var reports = kinds
                .Take(omitLastReport ? kinds.Length - 1 : kinds.Length)
                .Select((kind, index) => new
                {
                    Kind = kind,
                    RelativePath = System.IO.Path.GetFileName(SourcePaths[index]),
                })
                .ToDictionary(item => item.Kind, item => item.RelativePath);
            Manifest = CreateManifest(reports);
        }

        internal string Root { get; }

        internal string SourceRoot { get; }

        internal string OwnedRoot { get; }

        internal byte[][] ReportBytes { get; }

        internal string[] SourcePaths { get; }

        internal PrivateCaseManifest Manifest { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private string Write(string fileName, byte[] bytes)
        {
            var path = System.IO.Path.Combine(SourceRoot, fileName);
            File.WriteAllBytes(path, bytes);
            return path;
        }
    }

    internal static PrivateCaseManifest CreateManifest(
        IReadOnlyDictionary<LegacyReportKind, string> reports) => new(
        schemaVersion: 1,
        caseAlias: "local-case-01",
        new PrivateCaseProjectSettings(
            "SYNTHETIC-PRIVATE-CASE",
            "Synthetic private case entity",
            "synthetic-private-operator",
            new DateOnly(2025, 1, 1),
            new DateOnly(2025, 12, 31),
            new DateOnly(2024, 1, 1),
            sampleSeed: 1_600_001),
        new PrivateCaseGlSettings(
            [new PrivateCaseSource("gl.xlsx", "GL", firstRowIsFieldNames: true)],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GlMappingKeys.DocNum] = "document",
            },
            GlAmountMode.DualAmount),
        new PrivateCaseTbSettings(
            [new PrivateCaseSource("tb.xlsx", "TB", firstRowIsFieldNames: true)],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TbMappingKeys.AccNum] = "account",
            },
            TbChangeMode.DirectChange),
        new PrivateCaseReferenceData(
            "account-mapping.xlsx",
            PrivateCaseAuthorizedPreparerMode.NotProvided,
            authorizedPreparerRelativePath: null,
            holidayDates: [],
            makeupDates: []),
        scenarios: [],
        new PrivateCaseLegacyEvidence(
            "gl-log.txt",
            "tb-log.txt",
            reports,
            scenarioCounts: [],
            contentDecisions: []));
}
