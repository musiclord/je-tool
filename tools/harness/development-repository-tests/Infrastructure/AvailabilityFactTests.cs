using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class LegacyFixtureSafetyTests
{
    [Fact]
    public void CaseFixture_GetCaseAndResolveFileStayInsideRegularFiles()
    {
        using var repository = new TemporaryFixtureDirectory();
        var casePath = Path.Combine(repository.Path, "fixture-one");
        var nestedPath = Path.Combine(casePath, "nested");
        var sourcePath = Path.Combine(nestedPath, "source.xlsx");
        Directory.CreateDirectory(nestedPath);
        File.WriteAllText(sourcePath, "synthetic fixture");
        var fixtureSet = new LegacyParityFixtureSet(
            repository.Path,
            [
                new LegacyParityCaseFixture(LegacyParityCase.CaseA, casePath),
                new LegacyParityCaseFixture(
                    LegacyParityCase.CaseB,
                    Path.Combine(repository.Path, "fixture-two")),
            ]);

        var fixture = fixtureSet.GetCase(LegacyParityCase.CaseA);

        Assert.Equal("case-A", fixture.Alias);
        Assert.Equal(sourcePath, fixture.ResolveFile(Path.Combine("nested", "source.xlsx")));
        Assert.Throws<ArgumentException>(() => fixture.ResolveFile(sourcePath));
        Assert.Throws<ArgumentException>(() => fixture.ResolveFile(Path.Combine("..", "outside.xlsx")));
        Assert.Throws<InvalidOperationException>(() => fixture.ResolveFile("missing.xlsx"));
    }

    [Fact]
    public void CaseFixture_ResolveFileRejectsReparsePointAtAnyContainedSegment()
    {
        using var repository = new TemporaryFixtureDirectory();
        var casePath = Path.Combine(repository.Path, "fixture-one");
        var nestedPath = Path.Combine(casePath, "nested");
        var sourcePath = Path.Combine(nestedPath, "source.xlsx");
        Directory.CreateDirectory(nestedPath);
        File.WriteAllText(sourcePath, "synthetic fixture");
        var fixture = new LegacyParityCaseFixture(LegacyParityCase.CaseA, casePath);

        Assert.Throws<InvalidOperationException>(() => fixture.ResolveFile(
            Path.Combine("nested", "source.xlsx"),
            path => string.Equals(path, nestedPath, StringComparison.OrdinalIgnoreCase)
                ? File.GetAttributes(path) | FileAttributes.ReparsePoint
                : File.GetAttributes(path)));
    }

    [Fact]
    public void LegacyParityWorkspace_DisposeRemovesOnlyItsOwnedRunDirectory()
    {
        using var repository = new TemporaryFixtureDirectory();
        var sibling = Path.Combine(repository.Path, LegacyParityWorkspace.RelativeRoot, "sibling");
        Directory.CreateDirectory(sibling);

        var workspace = LegacyParityWorkspace.Create(
            repository.Path,
            LegacyParityCase.CaseA,
            "owned-run");
        var ownedPath = workspace.Path;
        File.WriteAllText(Path.Combine(ownedPath, "synthetic-output.txt"), "output");

        workspace.Dispose();

        Assert.False(Directory.Exists(ownedPath));
        Assert.True(Directory.Exists(sibling));
    }

    [Fact]
    public void LegacyParityWorkspace_DisposeLeavesNoOwnedEntries()
    {
        using var repository = new TemporaryFixtureDirectory();
        var workspace = LegacyParityWorkspace.Create(
            repository.Path,
            LegacyParityCase.CaseB,
            "owned-run");
        var root = Path.Combine(repository.Path, LegacyParityWorkspace.RelativeRoot);

        workspace.Dispose();

        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public void LegacyParityWorkspace_TemporaryFactoryRemovesItsWholeContainmentRoot()
    {
        var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            "temporary-run");
        var legacyRoot = Directory.GetParent(workspace.Path)
            ?? throw new InvalidOperationException("Temporary workspace parent is missing.");
        var dataRoot = legacyRoot.Parent
            ?? throw new InvalidOperationException("Temporary workspace data root is missing.");
        var temporaryRoot = dataRoot.Parent
            ?? throw new InvalidOperationException("Temporary workspace root is missing.");

        workspace.Dispose();

        Assert.False(Directory.Exists(temporaryRoot.FullName));
    }

    [Fact]
    public void LegacyParityWorkspace_KeepOutputsRequiresExplicitSwitchAndRetainsOwnedRun()
    {
        Assert.False(LegacyParityWorkspace.ParseKeepOutputs(null));
        Assert.False(LegacyParityWorkspace.ParseKeepOutputs("0"));
        Assert.False(LegacyParityWorkspace.ParseKeepOutputs("false"));
        Assert.True(LegacyParityWorkspace.ParseKeepOutputs("1"));
        Assert.True(LegacyParityWorkspace.ParseKeepOutputs("TRUE"));
        Assert.Throws<ArgumentException>(() => LegacyParityWorkspace.ParseKeepOutputs("yes"));

        using var repository = new TemporaryFixtureDirectory();
        var workspace = LegacyParityWorkspace.Create(
            repository.Path,
            LegacyParityCase.CaseA,
            "retained-run",
            keepOutputs: true);
        var ownedPath = workspace.Path;
        File.WriteAllText(Path.Combine(ownedPath, "synthetic-output.txt"), "output");

        workspace.Dispose();

        Assert.True(workspace.KeepsOutputs);
        Assert.True(Directory.Exists(ownedPath));
    }

    [Fact]
    public void LegacyParityWorkspace_FailedCleanupCanBeRetried()
    {
        using var repository = new TemporaryFixtureDirectory();
        var workspace = LegacyParityWorkspace.Create(
            repository.Path,
            LegacyParityCase.CaseA,
            "retry-run");
        var lockedPath = Path.Combine(workspace.Path, "locked.db");

        try
        {
            using (var locked = new FileStream(
                       lockedPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                Assert.ThrowsAny<IOException>(workspace.Dispose);
            }

            workspace.Dispose();
            Assert.False(Directory.Exists(workspace.Path));
        }
        finally
        {
            if (Directory.Exists(workspace.Path))
            {
                Directory.Delete(workspace.Path, recursive: true);
            }
        }
    }

    [Fact]
    public void LegacyParityWorkspace_RejectsUnsafeRunId()
    {
        using var repository = new TemporaryFixtureDirectory();

        Assert.Throws<ArgumentException>(() =>
            LegacyParityWorkspace.Create(repository.Path, LegacyParityCase.CaseA, "..\\outside"));
    }

    private sealed class TemporaryFixtureDirectory : IDisposable
    {
        public TemporaryFixtureDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"jet-fixture-safety-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
