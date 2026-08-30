using Microsoft.Win32.SafeHandles;

namespace JET.Tests.Infrastructure;

internal enum PrivateCaseFileAccessFailure
{
    InvalidRoot,
    RootUnavailable,
    InvalidRelativePath,
    PathTraversal,
    FileUnavailable,
    ReparsePoint,
    NotRegularFile,
    FileSystemUnavailable,
}

internal sealed class PrivateCaseFileAccessException : InvalidOperationException
{
    internal PrivateCaseFileAccessException(PrivateCaseFileAccessFailure failure)
        : base($"私人案件檔案存取已拒絕（{failure}）。")
    {
        Failure = failure;
    }

    internal PrivateCaseFileAccessFailure Failure { get; }
}

internal sealed class PrivateCaseOpenFile : IDisposable
{
    private FileStream? _stream;

    internal PrivateCaseOpenFile(FileStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    internal long Length => RequireOpenStream().Length;

    internal void CopyTo(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        RequireOpenStream().CopyTo(destination);
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }

    public override string ToString() => "private case source file (redacted)";

    private FileStream RequireOpenStream() => _stream
        ?? throw new ObjectDisposedException(nameof(PrivateCaseOpenFile));
}

internal sealed class PrivateCaseFileAccess
{
    private readonly Func<string, FileAttributes> _readAttributes;
    private readonly Func<string, FileStream> _openRead;
    private readonly Func<SafeFileHandle, FileAttributes> _readHandleAttributes;

    internal PrivateCaseFileAccess()
        : this(File.GetAttributes, OpenReadStream, File.GetAttributes)
    {
    }

    internal PrivateCaseFileAccess(
        Func<string, FileAttributes> readAttributes,
        Func<string, FileStream> openRead,
        Func<SafeFileHandle, FileAttributes>? readHandleAttributes = null)
    {
        _readAttributes = readAttributes ?? throw new ArgumentNullException(nameof(readAttributes));
        _openRead = openRead ?? throw new ArgumentNullException(nameof(openRead));
        _readHandleAttributes = readHandleAttributes ?? File.GetAttributes;
    }

    internal PrivateCaseOpenFile OpenRead(string authorizedRootPath, string relativePath)
    {
        var root = NormalizeRoot(authorizedRootPath);
        var candidate = ResolveContainedPath(root, relativePath);

        EnsureRegularDirectory(root, PrivateCaseFileAccessFailure.RootUnavailable);
        EnsureNoReparseAncestors(root);
        EnsureContainedDirectoryChain(root, candidate);
        EnsureRegularFile(candidate);

        FileStream? stream = null;
        try
        {
            stream = _openRead(candidate);
            EnsureOpenedHandleIsRegular(stream.SafeFileHandle);

            // Recheck the named path after the handle is open. The caller receives only
            // the held stream lease and must not reopen the source by path.
            EnsureContainedDirectoryChain(root, candidate);
            EnsureRegularFile(candidate);

            var source = new PrivateCaseOpenFile(stream);
            stream = null;
            return source;
        }
        catch (PrivateCaseFileAccessException)
        {
            throw;
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            throw Error(PrivateCaseFileAccessFailure.FileSystemUnavailable);
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private static FileStream OpenReadStream(string path) => new(
        path,
        new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.SequentialScan,
        });

    private static string NormalizeRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Path.IsPathFullyQualified(rootPath))
        {
            throw Error(PrivateCaseFileAccessFailure.InvalidRoot);
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException)
        {
            throw Error(PrivateCaseFileAccessFailure.InvalidRoot);
        }
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw Error(PrivateCaseFileAccessFailure.InvalidRelativePath);
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(relativePath, root);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException)
        {
            throw Error(PrivateCaseFileAccessFailure.InvalidRelativePath);
        }

        var prefix = root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw Error(PrivateCaseFileAccessFailure.PathTraversal);
        }

        return candidate;
    }

    private void EnsureRegularDirectory(string path, PrivateCaseFileAccessFailure unavailableFailure)
    {
        var attributes = ReadAttributes(path, unavailableFailure);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Error(PrivateCaseFileAccessFailure.ReparsePoint);
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw Error(unavailableFailure);
        }
    }

    private void EnsureRegularFile(string path)
    {
        var attributes = ReadAttributes(path, PrivateCaseFileAccessFailure.FileUnavailable);
        EnsureRegularFileAttributes(attributes);
    }

    private void EnsureOpenedHandleIsRegular(SafeFileHandle handle)
    {
        FileAttributes attributes;
        try
        {
            attributes = _readHandleAttributes(handle);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            throw Error(PrivateCaseFileAccessFailure.FileSystemUnavailable);
        }

        EnsureRegularFileAttributes(attributes);
    }

    private static void EnsureRegularFileAttributes(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Error(PrivateCaseFileAccessFailure.ReparsePoint);
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw Error(PrivateCaseFileAccessFailure.NotRegularFile);
        }
    }

    private void EnsureNoReparseAncestors(string root)
    {
        for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
        {
            EnsureRegularDirectory(current.FullName, PrivateCaseFileAccessFailure.RootUnavailable);
        }
    }

    private void EnsureContainedDirectoryChain(string root, string candidate)
    {
        for (var current = Directory.GetParent(candidate); current is not null; current = current.Parent)
        {
            EnsureRegularDirectory(current.FullName, PrivateCaseFileAccessFailure.FileUnavailable);
            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(current.FullName)),
                    root,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw Error(PrivateCaseFileAccessFailure.PathTraversal);
    }

    private FileAttributes ReadAttributes(string path, PrivateCaseFileAccessFailure unavailableFailure)
    {
        try
        {
            return _readAttributes(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException)
        {
            throw Error(unavailableFailure);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            throw Error(PrivateCaseFileAccessFailure.FileSystemUnavailable);
        }
    }

    private static bool IsFileSystemFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException
        or NotSupportedException
        or System.Security.SecurityException;

    private static PrivateCaseFileAccessException Error(PrivateCaseFileAccessFailure failure) =>
        new(failure);
}
