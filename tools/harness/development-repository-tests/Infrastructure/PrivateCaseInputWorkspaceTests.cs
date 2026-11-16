using System.Text.Json;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class PrivateCaseInputWorkspaceTests
{
    private const string FixedWorkspaceId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void CopyInput_CreatesByteIdenticalSafeNameAndCleanupLeavesOwnedRoot()
    {
        using var roots = new TemporaryRoots();
        var sourceBytes = new byte[] { 1, 2, 3, 4, 5 };
        var sourcePath = roots.WriteSource(Path.Combine("inputs", "original.xlsx"), sourceBytes);
        using var source = new PrivateCaseFileAccess().OpenRead(
            roots.SourceRoot,
            Path.Combine("inputs", "original.xlsx"));
        var workspace = PrivateCaseInputWorkspace.Create(
            roots.OwnedRoot,
            workspaceIdFactory: () => FixedWorkspaceId);

        var copy = workspace.CopyInput(source, PrivateCaseInputRole.Gl, 1, ".XLSX");

        Assert.Equal("gl-001.xlsx", copy.FileName);
        Assert.Equal(sourceBytes.Length, copy.Length);
        Assert.Equal(sourceBytes, File.ReadAllBytes(copy.FullPath));
        Assert.StartsWith(workspace.Path + Path.DirectorySeparatorChar, copy.FullPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("original", copy.FullPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("private case input copy (redacted)", copy.ToString());
        Assert.Equal("private case input workspace (redacted)", workspace.ToString());

        workspace.Dispose();

        Assert.False(Directory.Exists(workspace.Path));
        Assert.True(Directory.Exists(roots.OwnedRoot));
        Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath));
    }

    [Fact]
    public void Create_RejectsRelativeMissingFileAndInjectedReparseRootsWithoutEchoingPaths()
    {
        using var roots = new TemporaryRoots();
        var fileRoot = roots.WriteOwnedFile("not-a-directory.bin", [1]);

        var relative = Assert.Throws<PrivateCaseInputWorkspaceException>(() =>
            PrivateCaseInputWorkspace.Create("relative-root"));
        var file = Assert.Throws<PrivateCaseInputWorkspaceException>(() =>
            PrivateCaseInputWorkspace.Create(fileRoot));
        var reparse = Assert.Throws<PrivateCaseInputWorkspaceException>(() =>
            PrivateCaseInputWorkspace.Create(
                roots.OwnedRoot,
                path => string.Equals(path, roots.OwnedRoot, StringComparison.OrdinalIgnoreCase)
                    ? File.GetAttributes(path) | FileAttributes.ReparsePoint
                    : File.GetAttributes(path),
                () => FixedWorkspaceId));

        Assert.Equal(PrivateCaseInputWorkspaceFailure.InvalidOwnedRoot, relative.Failure);
        Assert.Equal(PrivateCaseInputWorkspaceFailure.RootUnavailable, file.Failure);
        Assert.Equal(PrivateCaseInputWorkspaceFailure.ReparsePoint, reparse.Failure);
        Assert.DoesNotContain("relative-root", relative.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fileRoot, file.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(roots.OwnedRoot, reparse.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CopyInput_RejectsInvalidAndDuplicateIdentifiersBeforeCreatingAnotherFile()
    {
        using var roots = new TemporaryRoots();
        roots.WriteSource("source.csv", [1, 2]);
        using var source = new PrivateCaseFileAccess().OpenRead(roots.SourceRoot, "source.csv");
        using var secondSource = new PrivateCaseFileAccess().OpenRead(roots.SourceRoot, "source.csv");
        var workspace = PrivateCaseInputWorkspace.Create(
            roots.OwnedRoot,
            workspaceIdFactory: () => FixedWorkspaceId);

        _ = workspace.CopyInput(source, PrivateCaseInputRole.Tb, 1, ".csv");
        var duplicate = Assert.Throws<PrivateCaseInputWorkspaceException>(() =>
            workspace.CopyInput(secondSource, PrivateCaseInputRole.Tb, 1, ".csv"));
        var badPosition = Assert.Throws<PrivateCaseInputWorkspaceException>(() =>
            workspace.CopyInput(secondSource, PrivateCaseInputRole.Tb, 0, ".csv"));
        var badRole = Assert.Throws<PrivateCaseInputWorkspaceException>(() =>
            workspace.CopyInput(secondSource, (PrivateCaseInputRole)999, 2, ".csv"));
        var badExtension = Assert.Throws<PrivateCaseInputWorkspaceException>(() =>
            workspace.CopyInput(secondSource, PrivateCaseInputRole.Tb, 2, ".exe"));

        Assert.Equal(PrivateCaseInputWorkspaceFailure.DuplicateInput, duplicate.Failure);
        Assert.Equal(PrivateCaseInputWorkspaceFailure.InvalidInput, badPosition.Failure);
        Assert.Equal(PrivateCaseInputWorkspaceFailure.InvalidInput, badRole.Failure);
        Assert.Equal(PrivateCaseInputWorkspaceFailure.InvalidInput, badExtension.Failure);
        Assert.Single(Directory.EnumerateFiles(workspace.Path));

        workspace.Dispose();
    }

    [Fact]
    public void Dispose_RemovesKnownCopiesButRefusesUnknownEntries()
    {
        using var roots = new TemporaryRoots();
        roots.WriteSource("source.txt", [1, 2, 3]);
        using var source = new PrivateCaseFileAccess().OpenRead(roots.SourceRoot, "source.txt");
        var workspace = PrivateCaseInputWorkspace.Create(
            roots.OwnedRoot,
            workspaceIdFactory: () => FixedWorkspaceId);
        var copy = workspace.CopyInput(source, PrivateCaseInputRole.Gl, 1, ".txt");
        var unexpectedPath = Path.Combine(workspace.Path, "unexpected.txt");
        File.WriteAllBytes(unexpectedPath, [9]);

        var exception = Assert.Throws<PrivateCaseInputWorkspaceException>(workspace.Dispose);

        Assert.Equal(PrivateCaseInputWorkspaceFailure.CleanupRefused, exception.Failure);
        Assert.False(File.Exists(copy.FullPath));
        Assert.True(File.Exists(unexpectedPath));
        Assert.True(Directory.Exists(workspace.Path));
        Assert.DoesNotContain(workspace.Path, exception.Message, StringComparison.OrdinalIgnoreCase);

        File.Delete(unexpectedPath);
        workspace.Dispose();
        Assert.False(Directory.Exists(workspace.Path));
    }

    [Fact]
    public void Dispose_RejectsChangedWorkspaceReparseStateBeforeDeletingKnownCopies()
    {
        using var roots = new TemporaryRoots();
        roots.WriteSource("source.xlsx", [1]);
        using var source = new PrivateCaseFileAccess().OpenRead(roots.SourceRoot, "source.xlsx");
        var injectReparse = false;
        var workspace = PrivateCaseInputWorkspace.Create(
            roots.OwnedRoot,
            path => injectReparse
                    && Path.GetFileName(path).StartsWith("private-case-inputs-", StringComparison.Ordinal)
                ? File.GetAttributes(path) | FileAttributes.ReparsePoint
                : File.GetAttributes(path),
            () => FixedWorkspaceId);
        var copy = workspace.CopyInput(source, PrivateCaseInputRole.Tb, 1, ".xlsx");
        injectReparse = true;

        var exception = Assert.Throws<PrivateCaseInputWorkspaceException>(workspace.Dispose);

        Assert.Equal(PrivateCaseInputWorkspaceFailure.ReparsePoint, exception.Failure);
        Assert.True(File.Exists(copy.FullPath));
        Assert.DoesNotContain(workspace.Path, exception.Message, StringComparison.OrdinalIgnoreCase);

        injectReparse = false;
        workspace.Dispose();
        Assert.False(Directory.Exists(workspace.Path));
    }

    [Fact]
    public void CopyInput_CreateNewDoesNotOverwriteAnUnexpectedDestination()
    {
        using var roots = new TemporaryRoots();
        roots.WriteSource("source.xlsx", [1, 2, 3]);
        using var source = new PrivateCaseFileAccess().OpenRead(roots.SourceRoot, "source.xlsx");
        var workspace = PrivateCaseInputWorkspace.Create(
            roots.OwnedRoot,
            workspaceIdFactory: () => FixedWorkspaceId);
        var collisionPath = Path.Combine(workspace.Path, "gl-001.xlsx");
        File.WriteAllBytes(collisionPath, [9, 9]);

        var exception = Assert.Throws<PrivateCaseInputWorkspaceException>(() =>
            workspace.CopyInput(source, PrivateCaseInputRole.Gl, 1, ".xlsx"));

        Assert.Equal(PrivateCaseInputWorkspaceFailure.CopyFailed, exception.Failure);
        Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(collisionPath));
        Assert.DoesNotContain("source.xlsx", exception.Message, StringComparison.OrdinalIgnoreCase);

        File.Delete(collisionPath);
        workspace.Dispose();
    }

    [Fact]
    public void InputCopy_SerializationOmitsFileNamesAndPaths()
    {
        using var roots = new TemporaryRoots();
        roots.WriteSource(Path.Combine("private", "source.xlsx"), [1, 2, 3]);
        using var source = new PrivateCaseFileAccess().OpenRead(
            roots.SourceRoot,
            Path.Combine("private", "source.xlsx"));
        using var workspace = PrivateCaseInputWorkspace.Create(
            roots.OwnedRoot,
            workspaceIdFactory: () => FixedWorkspaceId);
        var copy = workspace.CopyInput(source, PrivateCaseInputRole.Gl, 1, ".xlsx");

        var json = JsonSerializer.Serialize(copy);

        Assert.DoesNotContain("FileName", json, StringComparison.Ordinal);
        Assert.DoesNotContain("FullPath", json, StringComparison.Ordinal);
        Assert.DoesNotContain("source.xlsx", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(copy.FileName, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(copy.FullPath, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(roots.SourceRoot, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateGeneratedInput_WritesOnlyCalendarRolesAndParticipatesInCleanup()
    {
        using var roots = new TemporaryRoots();
        var workspace = PrivateCaseInputWorkspace.Create(
            roots.OwnedRoot,
            workspaceIdFactory: () => FixedWorkspaceId);

        var generated = workspace.CreateGeneratedInput(
            PrivateCaseInputRole.Holiday,
            1,
            ".xlsx",
            stream => stream.Write([1, 2, 3]));
        var invalid = Assert.Throws<PrivateCaseInputWorkspaceException>(() =>
            workspace.CreateGeneratedInput(
                PrivateCaseInputRole.Gl,
                2,
                ".xlsx",
                stream => stream.WriteByte(1)));

        Assert.Equal("holiday-001.xlsx", generated.FileName);
        Assert.Equal(3, generated.Length);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(generated.FullPath));
        Assert.Equal(PrivateCaseInputWorkspaceFailure.InvalidInput, invalid.Failure);

        workspace.Dispose();

        Assert.False(Directory.Exists(workspace.Path));
        Assert.True(Directory.Exists(roots.OwnedRoot));
    }

    private sealed class TemporaryRoots : IDisposable
    {
        internal TemporaryRoots()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"jet-private-case-workspace-{Guid.NewGuid():N}");
            SourceRoot = System.IO.Path.Combine(Path, "source");
            OwnedRoot = System.IO.Path.Combine(Path, "owned-run");
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(OwnedRoot);
        }

        internal string Path { get; }

        internal string SourceRoot { get; }

        internal string OwnedRoot { get; }

        internal string WriteSource(string relativePath, byte[] content) =>
            WriteFile(SourceRoot, relativePath, content);

        internal string WriteOwnedFile(string relativePath, byte[] content) =>
            WriteFile(OwnedRoot, relativePath, content);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }

        private static string WriteFile(string root, string relativePath, byte[] content)
        {
            var path = System.IO.Path.GetFullPath(relativePath, root);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            return path;
        }
    }
}
