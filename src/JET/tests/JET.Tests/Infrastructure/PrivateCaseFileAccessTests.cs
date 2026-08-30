using Microsoft.Win32.SafeHandles;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class PrivateCaseFileAccessTests
{
    [Fact]
    public void OpenRead_ContainedRegularFileReturnsBytesAndHoldsReadLease()
    {
        using var root = new TemporaryPrivateCaseRoot();
        var sourcePath = root.WriteFile(Path.Combine("inputs", "source.bin"), [1, 2, 3, 4]);

        using (var source = new PrivateCaseFileAccess().OpenRead(
                   root.Path,
                   Path.Combine("inputs", "source.bin")))
        {
            using var copy = new MemoryStream();
            source.CopyTo(copy);

            Assert.Equal(4, source.Length);
            Assert.Equal([1, 2, 3, 4], copy.ToArray());
            Assert.ThrowsAny<IOException>(() =>
            {
                using var blockedWrite = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
            });
            Assert.Equal("private case source file (redacted)", source.ToString());
        }

        using var writeAfterRelease = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None);
    }

    [Fact]
    public void OpenRead_RequiresAbsoluteRoot()
    {
        var exception = Assert.Throws<PrivateCaseFileAccessException>(() =>
            new PrivateCaseFileAccess().OpenRead("relative-root", "source.xlsx"));

        Assert.Equal(PrivateCaseFileAccessFailure.InvalidRoot, exception.Failure);
        Assert.DoesNotContain("relative-root", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenRead_RejectsRootedAndTraversingFilePathsBeforeOpeningThem()
    {
        using var root = new TemporaryPrivateCaseRoot();
        var outsidePath = Path.Combine(
            Directory.GetParent(root.Path)!.FullName,
            $"outside-{Guid.NewGuid():N}.xlsx");
        File.WriteAllBytes(outsidePath, [1]);
        var outsideOpened = false;
        var access = new PrivateCaseFileAccess(
            File.GetAttributes,
            path =>
            {
                if (string.Equals(path, outsidePath, StringComparison.OrdinalIgnoreCase))
                {
                    outsideOpened = true;
                }
                return OpenDefault(path);
            });

        try
        {
            var rooted = Assert.Throws<PrivateCaseFileAccessException>(() =>
                access.OpenRead(root.Path, outsidePath));
            var traversing = Assert.Throws<PrivateCaseFileAccessException>(() =>
                access.OpenRead(root.Path, Path.Combine("..", Path.GetFileName(outsidePath))));

            Assert.Equal(PrivateCaseFileAccessFailure.InvalidRelativePath, rooted.Failure);
            Assert.Equal(PrivateCaseFileAccessFailure.PathTraversal, traversing.Failure);
            Assert.False(outsideOpened);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Theory]
    [InlineData(InjectedReparsePoint.Root)]
    [InlineData(InjectedReparsePoint.Parent)]
    [InlineData(InjectedReparsePoint.File)]
    public void OpenRead_RejectsReparsePointAtEveryInspectedLevel(InjectedReparsePoint target)
    {
        using var root = new TemporaryPrivateCaseRoot();
        var parentPath = Path.Combine(root.Path, "inputs");
        var sourcePath = root.WriteFile(Path.Combine("inputs", "source.xlsx"), [1]);
        var rejectedPath = target switch
        {
            InjectedReparsePoint.Root => root.Path,
            InjectedReparsePoint.Parent => parentPath,
            InjectedReparsePoint.File => sourcePath,
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        var access = new PrivateCaseFileAccess(
            path =>
            {
                var attributes = File.GetAttributes(path);
                return string.Equals(path, rejectedPath, StringComparison.OrdinalIgnoreCase)
                    ? attributes | FileAttributes.ReparsePoint
                    : attributes;
            },
            OpenDefault);

        var exception = Assert.Throws<PrivateCaseFileAccessException>(() =>
            access.OpenRead(root.Path, Path.Combine("inputs", "source.xlsx")));

        Assert.Equal(PrivateCaseFileAccessFailure.ReparsePoint, exception.Failure);
        Assert.DoesNotContain(root.Path, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source.xlsx", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpenRead_RechecksNamedPathAfterOpeningAndReleasesRejectedHandle()
    {
        using var root = new TemporaryPrivateCaseRoot();
        var sourcePath = root.WriteFile("source.xlsx", [1]);
        var sourceReads = 0;
        var access = new PrivateCaseFileAccess(
            path =>
            {
                var attributes = File.GetAttributes(path);
                if (!string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    return attributes;
                }

                sourceReads++;
                return sourceReads == 1
                    ? attributes
                    : attributes | FileAttributes.ReparsePoint;
            },
            OpenDefault,
            File.GetAttributes);

        var exception = Assert.Throws<PrivateCaseFileAccessException>(() =>
            access.OpenRead(root.Path, "source.xlsx"));

        Assert.Equal(PrivateCaseFileAccessFailure.ReparsePoint, exception.Failure);
        Assert.True(sourceReads >= 2);
        using var writeAfterFailure = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None);
    }

    [Fact]
    public void OpenRead_RejectsMissingFileAndDirectoryWithoutEchoingNames()
    {
        using var root = new TemporaryPrivateCaseRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "not-a-file"));
        var access = new PrivateCaseFileAccess();

        var missing = Assert.Throws<PrivateCaseFileAccessException>(() =>
            access.OpenRead(root.Path, "missing-input.xlsx"));
        var directory = Assert.Throws<PrivateCaseFileAccessException>(() =>
            access.OpenRead(root.Path, "not-a-file"));

        Assert.Equal(PrivateCaseFileAccessFailure.FileUnavailable, missing.Failure);
        Assert.Equal(PrivateCaseFileAccessFailure.NotRegularFile, directory.Failure);
        Assert.DoesNotContain("missing-input.xlsx", missing.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-file", directory.Message, StringComparison.Ordinal);
    }

    private static FileStream OpenDefault(string path) => new(
        path,
        new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.SequentialScan,
        });

    public enum InjectedReparsePoint
    {
        Root,
        Parent,
        File,
    }

    private sealed class TemporaryPrivateCaseRoot : IDisposable
    {
        internal TemporaryPrivateCaseRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"jet-private-case-access-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal string WriteFile(string relativePath, byte[] content)
        {
            var path = System.IO.Path.GetFullPath(relativePath, Path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
