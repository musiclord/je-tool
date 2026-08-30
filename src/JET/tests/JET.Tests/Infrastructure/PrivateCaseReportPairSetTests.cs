using System.Text.Json;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class PrivateCaseReportPairSetTests
{
    [Fact]
    public void Create_CompleteSyntheticSets_PairsAllReportsWithoutSerializingPaths()
    {
        using var fixture = new SyntheticFixture();

        var result = PrivateCaseReportPairSet.Create(
            fixture.ExpectedReports,
            fixture.ActualReports);

        Assert.Equal(6, result.ReportCount);
        Assert.Equal(Enum.GetValues<LegacyReportKind>(), result.ReportKinds);
        Assert.Equal(Enum.GetValues<LegacyReportKind>(), result.Pairs.Select(pair => pair.Kind));

        var serialized = JsonSerializer.Serialize(result);
        var serializedPair = JsonSerializer.Serialize(result.Pairs[0]);
        foreach (var path in fixture.ExpectedReports.Reports.Select(item => item.FullPath)
                     .Concat(fixture.ActualReports.Reports.Select(item => item.FullPath)))
        {
            Assert.DoesNotContain(path, serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(path, serializedPair, StringComparison.OrdinalIgnoreCase);
        }
        Assert.StartsWith("private case report pair set", result.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("private case report pair", result.Pairs[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Create_MissingActualSet_IsRejectedBeforePairing()
    {
        using var fixture = new SyntheticFixture();

        Assert.Throws<ArgumentNullException>(() =>
            PrivateCaseReportPairSet.Create(fixture.ExpectedReports, null!));
    }

    private sealed class SyntheticFixture : IDisposable
    {
        private readonly string _root;
        private readonly PrivateCaseInputWorkspace _workspace;

        internal SyntheticFixture()
        {
            _root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"jet-private-report-pairs-{Guid.NewGuid():N}");
            var sourceRoot = System.IO.Path.Combine(_root, "source");
            var generatedRoot = System.IO.Path.Combine(_root, "generated");
            var ownedRoot = System.IO.Path.Combine(_root, "owned");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(generatedRoot);
            Directory.CreateDirectory(ownedRoot);

            var reports = Enum.GetValues<LegacyReportKind>()
                .Select((kind, index) =>
                {
                    var fileName = $"private-reference-{index + 1:000}.xlsx";
                    File.WriteAllBytes(System.IO.Path.Combine(sourceRoot, fileName), [80, 75, 3, 4]);
                    return new { Kind = kind, FileName = fileName };
                })
                .ToDictionary(item => item.Kind, item => item.FileName);
            _workspace = PrivateCaseInputWorkspace.Create(ownedRoot);
            ExpectedReports = PrivateCaseExpectedReportSet.Create(
                PrivateCaseExpectedReportSetTests.CreateManifest(reports),
                sourceRoot,
                _workspace);
            var artifacts = Enum.GetValues<LegacyReportKind>()
                .Select((kind, index) =>
                {
                    var fileName = $"generated-output-{index + 1:000}.xlsx";
                    var fullPath = System.IO.Path.Combine(generatedRoot, fileName);
                    File.WriteAllBytes(fullPath, [80, 75, 3, 4, checked((byte)index)]);
                    return new LegacyAuditParityJourneyArtifact(kind, fileName, fullPath);
                })
                .ToArray();
            ActualReports = PrivateCaseActualReportSet.Create(
                artifacts,
                generatedRoot,
                _workspace);
        }

        internal PrivateCaseExpectedReportSet ExpectedReports { get; }

        internal PrivateCaseActualReportSet ActualReports { get; }

        public void Dispose()
        {
            _workspace.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
