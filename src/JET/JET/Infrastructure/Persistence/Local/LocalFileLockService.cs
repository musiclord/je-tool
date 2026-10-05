using System.Security.Cryptography;
using System.Text;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// SQLite／DuckDB 共用的跨程序工作鎖。鎖檔位於 projects root、而非案件資料夾內，因此持鎖時仍可在
/// project.delete 成功後移除整個案件資料夾。檔名只含 canonical 案件路徑的 SHA-256，不洩漏案名。
/// </summary>
public sealed class LocalFileLockService : ILockService, IProjectDeletionLockService, IDisposable
{
    private const string LockFilePrefix = ".project-session-";
    private const string LockFileSuffix = ".lock";
    private const string ExternalHolder = "另一個 JET 執行個體";

    private static readonly Task<IReadOnlyList<ProjectLockInfo>> NoListedLocks =
        Task.FromResult<IReadOnlyList<ProjectLockInfo>>([]);

    private readonly JetProjectFolder _folder;
    private readonly string _rootPath;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HeldLock> _held = new(PathComparer);
    private bool _disposed;

    public LocalFileLockService(JetProjectFolder folder)
    {
        _folder = folder;
        try
        {
            _rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.RootPath));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("本地案件根目錄路徑格式無效。", nameof(folder), exception);
        }
    }

    public Task<LockOutcome> AcquireAsync(
        string projectId,
        string principal,
        CancellationToken cancellationToken)
    {
        var attempt = TryAcquire(projectId, principal, cancellationToken);
        LockOutcome outcome = attempt.Blocked is null
            ? new LockOutcome.Acquired(attempt.NewlyAcquired)
            : new LockOutcome.Held(
                attempt.Blocked.Principal,
                attempt.Blocked.MachineName,
                attempt.Blocked.LockedUtc);
        return Task.FromResult(outcome);
    }

    public Task RenewAsync(string projectId, string principal, CancellationToken cancellationToken)
    {
        // OS handle 沒有租期；只要程序仍活著且未 release，排他鎖就持續有效。
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(string projectId, string principal, CancellationToken cancellationToken)
    {
        ReleaseOwned(GetLockPath(projectId), principal);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProjectLockInfo>> ListActiveAsync(CancellationToken cancellationToken) =>
        NoListedLocks;

    public Task<ProjectDeletionLockOutcome> TryAcquireAsync(
        string projectId,
        string principal,
        CancellationToken cancellationToken)
    {
        var attempt = TryAcquire(projectId, principal, cancellationToken);
        ProjectDeletionLockOutcome outcome;
        if (attempt.Blocked is not null)
        {
            outcome = new ProjectDeletionLockOutcome.Held(
                attempt.Blocked.Principal,
                attempt.Blocked.MachineName,
                attempt.Blocked.LockedUtc);
        }
        else
        {
            outcome = new ProjectDeletionLockOutcome.Acquired(
                new DeletionLease(this, attempt.LockPath, principal, attempt.NewlyAcquired));
        }

        return Task.FromResult(outcome);
    }

    public void Dispose()
    {
        HeldLock[] held;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            held = [.. _held.Values];
            _held.Clear();
        }

        foreach (var item in held)
        {
            item.Stream.Dispose();
        }
    }

    private AcquisitionAttempt TryAcquire(
        string projectId,
        string principal,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        cancellationToken.ThrowIfCancellationRequested();
        var projectDirectory = GetProjectDirectory(projectId);
        var lockPath = GetLockPathForProjectDirectory(projectDirectory);
        Directory.CreateDirectory(_rootPath);
        EnsureNoReparseDirectoryChain(new DirectoryInfo(_rootPath));
        EnsureNoReparseDirectoryChain(new DirectoryInfo(projectDirectory), missingLeafIsProjectNotFound: true);
        EnsureSafeLockPath(lockPath, requireExists: false);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_held.TryGetValue(lockPath, out var current))
            {
                return string.Equals(current.Principal, principal, StringComparison.OrdinalIgnoreCase)
                    ? new AcquisitionAttempt(lockPath, NewlyAcquired: false, Blocked: null)
                    : new AcquisitionAttempt(lockPath, NewlyAcquired: false, Blocked: current);
            }

            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                try
                {
                    EnsureNoReparseDirectoryChain(new DirectoryInfo(_rootPath));
                    EnsureNoReparseDirectoryChain(
                        new DirectoryInfo(projectDirectory),
                        missingLeafIsProjectNotFound: true);
                    EnsureSafeLockPath(lockPath, requireExists: true);
                    var acquired = new HeldLock(
                        principal,
                        Environment.MachineName,
                        DateTimeOffset.UtcNow,
                        stream);
                    _held.Add(lockPath, acquired);
                    return new AcquisitionAttempt(lockPath, NewlyAcquired: true, Blocked: null);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                return new AcquisitionAttempt(
                    lockPath,
                    NewlyAcquired: false,
                    Blocked: new HeldLock(
                        ExternalHolder,
                        string.Empty,
                        default,
                        Stream.Null));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new JetActionException(
                    JetErrorCodes.FileReadError,
                    "無法鎖定這個案件，系統沒有開始作業。請確認 JET 資料夾的權限，或重新啟動 JET。",
                    innerException: exception);
            }
        }
    }

    private string GetProjectDirectory(string projectId)
    {
        try
        {
            var projectDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(_folder.GetProjectDirectory(projectId)));
            var requiredPrefix = _rootPath + Path.DirectorySeparatorChar;
            if (!projectDirectory.StartsWith(requiredPrefix, PathComparison))
            {
                throw new JetActionException(
                    JetErrorCodes.FileReadError,
                    "案件資料夾不在 JET 的案件位置內，無法鎖定這個案件。請確認 JET 資料夾的權限，或重新啟動 JET。");
            }

            return projectDirectory;
        }
        catch (JetActionException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                "案件資料夾的路徑格式無效，無法鎖定這個案件。請確認 JET 資料夾的權限，或重新啟動 JET。");
        }
    }

    private string GetLockPath(string projectId) =>
        GetLockPathForProjectDirectory(GetProjectDirectory(projectId));

    private string GetLockPathForProjectDirectory(string projectDirectory)
    {
        var normalizedForHash = OperatingSystem.IsWindows()
            ? projectDirectory.ToUpperInvariant()
            : projectDirectory;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedForHash)));
        return Path.Combine(_rootPath, $"{LockFilePrefix}{hash}{LockFileSuffix}");
    }

    private static void EnsureSafeLockPath(string lockPath, bool requireExists)
    {
        try
        {
            var entry = new FileInfo(lockPath);
            EnsureNoReparseDirectoryChain(
                entry.Directory ?? throw new IOException("鎖檔缺少父資料夾。"));
            if (entry.LinkTarget is not null)
            {
                throw new IOException("鎖檔不可為檔案系統連結。");
            }

            entry.Refresh();
            if (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("鎖檔不可為 reparse point。");
            }

            if (Directory.Exists(lockPath))
            {
                throw new IOException("鎖檔路徑不可為資料夾。");
            }

            if (requireExists && !entry.Exists)
            {
                throw new IOException("鎖檔未安全建立。");
            }
        }
        catch (JetActionException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or NotSupportedException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                "無法確認案件鎖定檔的位置是否安全，系統沒有開始作業。請確認 JET 資料夾的權限，或重新啟動 JET。");
        }
    }

    private static void EnsureNoReparseDirectoryChain(
        DirectoryInfo directory,
        bool missingLeafIsProjectNotFound = false)
    {
        try
        {
            var chain = new Stack<DirectoryInfo>();
            for (DirectoryInfo? current = directory; current is not null; current = current.Parent)
            {
                chain.Push(current);
            }

            foreach (var current in chain)
            {
                if (current.LinkTarget is not null)
                {
                    throw new IOException("本地案件鎖路徑不可包含檔案系統連結。");
                }

                current.Refresh();
                if (!current.Exists)
                {
                    if (missingLeafIsProjectNotFound
                        && string.Equals(current.FullName, directory.FullName, PathComparison))
                    {
                        throw new JetActionException(
                            JetErrorCodes.ProjectNotFound,
                            "找不到指定的案件資料夾。案件資料夾可能已被移動或刪除，請確認案件資料夾仍在 JET 的案件位置。");
                    }

                    throw new IOException("本地案件鎖路徑祖先不存在。");
                }

                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("本地案件鎖路徑不可包含 reparse point。");
                }
            }
        }
        catch (JetActionException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or NotSupportedException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                "無法確認案件鎖定檔的位置是否安全，系統沒有開始作業。請確認 JET 資料夾的權限，或重新啟動 JET。");
        }
    }

    private void ReleaseOwned(string lockPath, string principal)
    {
        HeldLock? released = null;
        lock (_gate)
        {
            if (_held.TryGetValue(lockPath, out var current)
                && string.Equals(current.Principal, principal, StringComparison.OrdinalIgnoreCase))
            {
                _held.Remove(lockPath);
                released = current;
            }
        }

        released?.Stream.Dispose();
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var code = exception.HResult & 0xFFFF;
        return code is 32 or 33;
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record HeldLock(
        string Principal,
        string MachineName,
        DateTimeOffset LockedUtc,
        Stream Stream);

    private sealed record AcquisitionAttempt(
        string LockPath,
        bool NewlyAcquired,
        HeldLock? Blocked);

    private sealed class DeletionLease(
        LocalFileLockService owner,
        string lockPath,
        string principal,
        bool newlyAcquired) : IProjectDeletionLockLease
    {
        private int _completed;
        private int _disposed;

        public void Complete()
        {
            Volatile.Write(ref _completed, 1);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0
                && (newlyAcquired || Volatile.Read(ref _completed) == 1))
            {
                owner.ReleaseOwned(lockPath, principal);
            }

            return ValueTask.CompletedTask;
        }
    }
}
