using System.Text.Json;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class PrivateCaseActualReportSetTests
{
    [Fact]
    public void Create_CompleteSyntheticOutputs_CopiesSafeNamesWithoutSerializingPaths()
    {
        using var fixture = new SyntheticFixture(createFiles: true);

        var result = PrivateCaseActualReportSet.Create(
            fixture.Artifacts,
            fixture.GeneratedRoot,
            fixture.Workspace);

        Assert.Equal(6, result.ReportCount);
        Assert.Equal(Enum.GetValues<LegacyReportKind>(), result.ReportKinds);
        Assert.Equal(
            Enumerable.Range(1, 6).Select(index => $"actual-report-{index:000}.xlsx"),
            result.Reports.Select(report => report.FileName));

        foreach (var report in result.Reports)
        {
            var source = fixture.Artifacts.Single(item => item.Kind == report.Kind).FullPath;
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(report.FullPath));
            Assert.True(File.Exists(source));
        }

        var serialized = JsonSerializer.Serialize(result);
        var serializedReport = JsonSerializer.Serialize(result.Reports[0]);
        Assert.DoesNotContain(fixture.GeneratedRoot, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("actual-report", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.GeneratedRoot, serializedReport, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("actual-report", serializedReport, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("private case actual report set", result.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("private case actual report", result.Reports[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Create_MissingOutputRole_FailsBeforeOpeningFiles()
    {
        using var fixture = new SyntheticFixture(createFiles: false);

        var error = Assert.Throws<PrivateCaseActualReportException>(() =>
            PrivateCaseActualReportSet.Create(
                fixture.Artifacts.SkipLast(1).ToArray(),
                fixture.GeneratedRoot,
                fixture.Workspace));

        Assert.Equal(PrivateCaseActualReportFailure.InvalidInput, error.Failure);
        Assert.DoesNotContain(fixture.GeneratedRoot, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_PathThatDoesNotMatchOwnedRoot_IsRejectedWithoutOpeningIt()
    {
        using var fixture = new SyntheticFixture(createFiles: true);
        var artifacts = fixture.Artifacts.ToArray();
        artifacts[0] = artifacts[0] with
        {
            FullPath = System.IO.Path.Combine(fixture.Root, "outside.xlsx"),
        };

        var error = Assert.Throws<PrivateCaseActualReportException>(() =>
            PrivateCaseActualReportSet.Create(
                artifacts,
                fixture.GeneratedRoot,
                fixture.Workspace));

        Assert.Equal(PrivateCaseActualReportFailure.InvalidInput, error.Failure);
        Assert.DoesNotContain("outside.xlsx", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.GeneratedRoot, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_MissingGeneratedFile_FailsWithoutEchoingItsName()
    {
        using var fixture = new SyntheticFixture(createFiles: false);

        var error = Assert.Throws<PrivateCaseActualReportException>(() =>
            PrivateCaseActualReportSet.Create(
                fixture.Artifacts,
                fixture.GeneratedRoot,
                fixture.Workspace));

        Assert.Equal(PrivateCaseActualReportFailure.GeneratedFileUnavailable, error.Failure);
        Assert.DoesNotContain("generated-output", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.GeneratedRoot, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SyntheticFixture : IDisposable
    {
        internal SyntheticFixture(bool createFiles)
        {
            Root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"jet-private-actual-reports-{Guid.NewGuid():N}");
            GeneratedRoot = System.IO.Path.Combine(Root, "generated");
            var ownedRoot = System.IO.Path.Combine(Root, "owned");
            Directory.CreateDirectory(GeneratedRoot);
            Directory.CreateDirectory(ownedRoot);

            Artifacts = Enum.GetValues<LegacyReportKind>()
                .Select((kind, index) =>
                {
                    var fileName = $"generated-output-{index + 1:000}.xlsx";
                    var fullPath = System.IO.Path.Combine(GeneratedRoot, fileName);
                    if (createFiles)
                    {
                        File.WriteAllBytes(fullPath, [80, 75, 3, 4, checked((byte)index)]);
                    }
                    return new LegacyAuditParityJourneyArtifact(kind, fileName, fullPath);
                })
                .ToArray();
            Workspace = PrivateCaseInputWorkspace.Create(ownedRoot);
            WorkspacePath = Workspace.Path;
        }

        internal string Root { get; }

        internal string GeneratedRoot { get; }

        internal IReadOnlyList<LegacyAuditParityJourneyArtifact> Artifacts { get; }

        internal PrivateCaseInputWorkspace Workspace { get; }

        private string WorkspacePath { get; }

        public void Dispose()
        {
            Workspace.Dispose();
            Assert.False(Directory.Exists(WorkspacePath));
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
