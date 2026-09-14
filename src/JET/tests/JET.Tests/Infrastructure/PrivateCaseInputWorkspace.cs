using System.Text.Json.Serialization;

namespace JET.Tests.Infrastructure;

internal enum PrivateCaseInputRole
{
    ExpectedReport,
    ActualReport,
    Gl,
    Tb,
    AccountMapping,
    AuthorizedPreparer,
    Holiday,
    MakeupDay,
}

internal enum PrivateCaseInputWorkspaceFailure
{
    InvalidOwnedRoot,
    RootUnavailable,
    WorkspaceConflict,
    InvalidInput,
    DuplicateInput,
    CopyFailed,
    GenerateFailed,
    CleanupRefused,
    ReparsePoint,
    FileSystemUnavailable,
}

internal sealed class PrivateCaseInputWorkspaceException : InvalidOperationException
{
    internal PrivateCaseInputWorkspaceException(PrivateCaseInputWorkspaceFailure failure)
        : base($"私人案件輸入暫存已拒絕（{failure}）。")
    {
        Failure = failure;
    }

    internal PrivateCaseInputWorkspaceFailure Failure { get; }
}

internal sealed record PrivateCaseInputCopy(
    PrivateCaseInputRole Role,
    int Position,
    [property: JsonIgnore] string FileName,
    [property: JsonIgnore] string FullPath,
    long Length)
{
    public override string ToString() => "private case input copy (redacted)";
}

/// <summary>
/// Copies already-authorized private inputs into one run-owned directory. Product actions and
/// comparison helpers receive only these generated paths and never reopen the original paths.
/// </summary>
internal sealed class PrivateCaseInputWorkspace : IDisposable
{
    private const string DirectoryPrefix = "private-case-inputs-";
    private static readonly HashSet<string> AllowedExtensions =
        new([".csv", ".txt", ".xlsx", ".xlsm"], StringComparer.Ordinal);

    private readonly string _ownedRoot;
    private readonly string _workspacePath;
    private readonly Func<string, FileAttributes> _readAttributes;
    private readonly List<string> _createdFiles = [];
    private readonly HashSet<(PrivateCaseInputRole Role, int Position)> _copiedInputs = [];
    private bool _disposed;

    private PrivateCaseInputWorkspace(
        string ownedRoot,
        string workspacePath,
        Func<string, FileAttributes> readAttributes)
    {
        _ownedRoot = ownedRoot;
        _workspacePath = workspacePath;
        _readAttributes = readAttributes;
    }

    [JsonIgnore]
    internal string Path => _workspacePath;

    internal static PrivateCaseInputWorkspace Create(
        string ownedRootPath,
        Func<string, FileAttributes>? readAttributes = null,
        Func<string>? workspaceIdFactory = null)
    {
        var attributes = readAttributes ?? File.GetAttributes;
        var root = NormalizeRoot(ownedRootPath);
        EnsureDirectoryAndAncestors(root, attributes, PrivateCaseInputWorkspaceFailure.RootUnavailable);

        var workspaceId = (workspaceIdFactory ?? (() => Guid.NewGuid().ToString("N")))();
        if (!IsWorkspaceId(workspaceId))
        {
            throw Error(PrivateCaseInputWorkspaceFailure.InvalidInput);
        }

        var workspacePath = System.IO.Path.GetFullPath(DirectoryPrefix + workspaceId, root);
        EnsureContained(root, workspacePath);
        if (TryReadAttributes(workspacePath, attributes, out _))
        {
            throw Error(PrivateCaseInputWorkspaceFailure.WorkspaceConflict);
        }

        try
        {
            Directory.CreateDirectory(workspacePath);
            EnsureDirectoryChain(root, workspacePath, attributes);
            if (Directory.EnumerateFileSystemEntries(workspacePath).Any())
            {
                throw Error(PrivateCaseInputWorkspaceFailure.WorkspaceConflict);
            }

            return new PrivateCaseInputWorkspace(root, workspacePath, attributes);
        }
        catch (PrivateCaseInputWorkspaceException)
        {
            throw;
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            throw Error(PrivateCaseInputWorkspaceFailure.FileSystemUnavailable);
        }
    }

    internal PrivateCaseInputCopy CopyInput(
        PrivateCaseOpenFile source,
        PrivateCaseInputRole role,
        int position,
        string extension)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);

        if (!Enum.IsDefined(role) || position is < 1 or > 999)
        {
            throw Error(PrivateCaseInputWorkspaceFailure.InvalidInput);
        }

        var normalizedExtension = NormalizeExtension(extension);
        var key = (role, position);
        if (!_copiedInputs.Add(key))
        {
            throw Error(PrivateCaseInputWorkspaceFailure.DuplicateInput);
        }

        var fileName = $"{RoleName(role)}-{position:000}{normalizedExtension}";
        var destinationPath = System.IO.Path.GetFullPath(fileName, _workspacePath);
        EnsureContained(_workspacePath, destinationPath);
        EnsureWorkspaceIsSafe();

        var expectedLength = source.Length;
        try
        {
            using (var destination = new FileStream(
                       destinationPath,
                       new FileStreamOptions
                       {
                           Mode = FileMode.CreateNew,
                           Access = FileAccess.Write,
                           Share = FileShare.None,
                           Options = FileOptions.SequentialScan,
                       }))
            {
                _createdFiles.Add(destinationPath);
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }

            var destinationAttributes = ReadAttributes(
                destinationPath,
                PrivateCaseInputWorkspaceFailure.CopyFailed);
            EnsureRegularFile(destinationAttributes, PrivateCaseInputWorkspaceFailure.CopyFailed);
            var actualLength = new FileInfo(destinationPath).Length;
            if (actualLength != expectedLength)
            {
                throw Error(PrivateCaseInputWorkspaceFailure.CopyFailed);
            }

            return new PrivateCaseInputCopy(role, position, fileName, destinationPath, actualLength);
        }
        catch (PrivateCaseInputWorkspaceException)
        {
            throw;
        }
        catch (Exception exception) when (IsFileSystemFailure(exception)
            || exception is ObjectDisposedException)
        {
            throw Error(PrivateCaseInputWorkspaceFailure.CopyFailed);
        }
    }

    internal PrivateCaseInputCopy CreateGeneratedInput(
        PrivateCaseInputRole role,
        int position,
        string extension,
        Action<Stream> write)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(write);

        if (role is not (PrivateCaseInputRole.Holiday or PrivateCaseInputRole.MakeupDay)
            || position is < 1 or > 999)
        {
            throw Error(PrivateCaseInputWorkspaceFailure.InvalidInput);
        }

        var normalizedExtension = NormalizeExtension(extension);
        var key = (role, position);
        if (!_copiedInputs.Add(key))
        {
            throw Error(PrivateCaseInputWorkspaceFailure.DuplicateInput);
        }

        var fileName = $"{RoleName(role)}-{position:000}{normalizedExtension}";
        var destinationPath = System.IO.Path.GetFullPath(fileName, _workspacePath);
        EnsureContained(_workspacePath, destinationPath);
        EnsureWorkspaceIsSafe();

        try
        {
            using (var destination = new FileStream(
                       destinationPath,
                       new FileStreamOptions
                       {
                           Mode = FileMode.CreateNew,
                           Access = FileAccess.Write,
                           Share = FileShare.None,
                           Options = FileOptions.SequentialScan,
                       }))
            {
                _createdFiles.Add(destinationPath);
                write(destination);
                destination.Flush(flushToDisk: true);
            }

            var destinationAttributes = ReadAttributes(
                destinationPath,
                PrivateCaseInputWorkspaceFailure.GenerateFailed);
            EnsureRegularFile(destinationAttributes, PrivateCaseInputWorkspaceFailure.GenerateFailed);
            var length = new FileInfo(destinationPath).Length;
            if (length == 0)
            {
                throw Error(PrivateCaseInputWorkspaceFailure.GenerateFailed);
            }

            return new PrivateCaseInputCopy(role, position, fileName, destinationPath, length);
        }
        catch (PrivateCaseInputWorkspaceException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Error(PrivateCaseInputWorkspaceFailure.GenerateFailed);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!TryReadAttributes(_workspacePath, _readAttributes, out _))
            {
                _disposed = true;
                return;
            }

            EnsureWorkspaceIsSafe();
            for (var index = _createdFiles.Count - 1; index >= 0; index--)
            {
                var path = _createdFiles[index];
                if (!TryReadAttributes(path, _readAttributes, out var attributes))
                {
                    continue;
                }

                EnsureRegularFile(attributes, PrivateCaseInputWorkspaceFailure.CleanupRefused);
                File.Delete(path);
            }

            // Non-recursive deletion is intentional. Any unknown file or directory leaves this
            // workspace in place and turns cleanup into a visible failure.
            Directory.Delete(_workspacePath);
            _disposed = true;
        }
        catch (PrivateCaseInputWorkspaceException)
        {
            throw;
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            throw Error(PrivateCaseInputWorkspaceFailure.CleanupRefused);
        }
    }

    public override string ToString() => "private case input workspace (redacted)";

    private void EnsureWorkspaceIsSafe()
    {
        EnsureDirectoryAndAncestors(
            _ownedRoot,
            _readAttributes,
            PrivateCaseInputWorkspaceFailure.RootUnavailable);
        EnsureDirectoryChain(_ownedRoot, _workspacePath, _readAttributes);
    }

    private static string NormalizeRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !System.IO.Path.IsPathFullyQualified(rootPath))
        {
            throw Error(PrivateCaseInputWorkspaceFailure.InvalidOwnedRoot);
        }

        try
        {
            return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(rootPath));
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException)
        {
            throw Error(PrivateCaseInputWorkspaceFailure.InvalidOwnedRoot);
        }
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            throw Error(PrivateCaseInputWorkspaceFailure.InvalidInput);
        }

        var normalized = extension.ToLowerInvariant();
        if (!AllowedExtensions.Contains(normalized))
        {
            throw Error(PrivateCaseInputWorkspaceFailure.InvalidInput);
        }

        return normalized;
    }

    private static string RoleName(PrivateCaseInputRole role) => role switch
    {
        PrivateCaseInputRole.ExpectedReport => "expected-report",
        PrivateCaseInputRole.ActualReport => "actual-report",
        PrivateCaseInputRole.Gl => "gl",
        PrivateCaseInputRole.Tb => "tb",
        PrivateCaseInputRole.AccountMapping => "account-mapping",
        PrivateCaseInputRole.AuthorizedPreparer => "authorized-preparer",
        PrivateCaseInputRole.Holiday => "holiday",
        PrivateCaseInputRole.MakeupDay => "makeup-day",
        _ => throw Error(PrivateCaseInputWorkspaceFailure.InvalidInput),
    };

    private static bool IsWorkspaceId(string? value) => value is not null
        && value.Length == 32
        && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    private static void EnsureContained(string rootPath, string candidatePath)
    {
        var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(rootPath));
        var candidate = System.IO.Path.GetFullPath(candidatePath);
        var prefix = root + System.IO.Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw Error(PrivateCaseInputWorkspaceFailure.InvalidOwnedRoot);
        }
    }

    private static void EnsureDirectoryAndAncestors(
        string path,
        Func<string, FileAttributes> readAttributes,
        PrivateCaseInputWorkspaceFailure unavailableFailure)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            var attributes = ReadAttributes(current.FullName, readAttributes, unavailableFailure);
            EnsureRegularDirectory(attributes, unavailableFailure);
        }
    }

    private static void EnsureDirectoryChain(
        string rootPath,
        string candidatePath,
        Func<string, FileAttributes> readAttributes)
    {
        var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(rootPath));
        for (var current = new DirectoryInfo(candidatePath); current is not null; current = current.Parent)
        {
            var attributes = ReadAttributes(
                current.FullName,
                readAttributes,
                PrivateCaseInputWorkspaceFailure.RootUnavailable);
            EnsureRegularDirectory(attributes, PrivateCaseInputWorkspaceFailure.RootUnavailable);
            if (string.Equals(
                    System.IO.Path.TrimEndingDirectorySeparator(current.FullName),
                    root,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw Error(PrivateCaseInputWorkspaceFailure.InvalidOwnedRoot);
    }

    private static void EnsureRegularDirectory(
        FileAttributes attributes,
        PrivateCaseInputWorkspaceFailure unavailableFailure)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Error(PrivateCaseInputWorkspaceFailure.ReparsePoint);
        }
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw Error(unavailableFailure);
        }
    }

    private static void EnsureRegularFile(
        FileAttributes attributes,
        PrivateCaseInputWorkspaceFailure unavailableFailure)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Error(PrivateCaseInputWorkspaceFailure.ReparsePoint);
        }
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw Error(unavailableFailure);
        }
    }

    private FileAttributes ReadAttributes(
        string path,
        PrivateCaseInputWorkspaceFailure unavailableFailure) =>
        ReadAttributes(path, _readAttributes, unavailableFailure);

    private static FileAttributes ReadAttributes(
        string path,
        Func<string, FileAttributes> readAttributes,
        PrivateCaseInputWorkspaceFailure unavailableFailure)
    {
        try
        {
            return readAttributes(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException)
        {
            throw Error(unavailableFailure);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            throw Error(PrivateCaseInputWorkspaceFailure.FileSystemUnavailable);
        }
    }

    private static bool TryReadAttributes(
        string path,
        Func<string, FileAttributes> readAttributes,
        out FileAttributes attributes)
    {
        try
        {
            attributes = readAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            throw Error(PrivateCaseInputWorkspaceFailure.FileSystemUnavailable);
        }
    }

    private static bool IsFileSystemFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException
        or NotSupportedException
        or System.Security.SecurityException;

    private static PrivateCaseInputWorkspaceException Error(PrivateCaseInputWorkspaceFailure failure) =>
        new(failure);
}
