using JET.Tests.Application;

namespace JET.Tests.Infrastructure;

internal enum PrivateCaseRunScopeFailure
{
    CreationFailed,
    CleanupFailed,
}

internal sealed class PrivateCaseRunScopeException : InvalidOperationException
{
    internal PrivateCaseRunScopeException(PrivateCaseRunScopeFailure failure)
        : base($"私人案件執行空間發生錯誤（{failure}）。")
    {
        Failure = failure;
    }

    internal PrivateCaseRunScopeFailure Failure { get; }
}

/// <summary>
/// Owns every local path that may contain copied private inputs or generated JET outputs.
/// RunAsync always disposes the scope before returning or rethrowing a work failure.
/// </summary>
internal sealed class PrivateCaseRunScope : IDisposable
{
    private readonly string _inputRootPath;
    private readonly string _projectsRootPath;
    private bool _disposed;

    private PrivateCaseRunScope(
        string inputRootPath,
        PrivateCaseInputWorkspace inputWorkspace,
        HandlerTestHost host)
    {
        _inputRootPath = inputRootPath;
        InputWorkspace = inputWorkspace;
        Host = host;
        _projectsRootPath = host.ProjectsRoot;
    }

    internal HandlerTestHost Host { get; }

    internal PrivateCaseInputWorkspace InputWorkspace { get; }

    internal static async Task<T> RunAsync<T>(
        Func<PrivateCaseRunScope, CancellationToken, Task<T>> work,
        string? sqlServerConnectionString = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var scope = Create(sqlServerConnectionString);
        return await work(scope, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        var cleanupFailed = false;
        try
        {
            Host.Dispose();
        }
        catch (Exception)
        {
            cleanupFailed = true;
        }

        try
        {
            InputWorkspace.Dispose();
        }
        catch (Exception)
        {
            cleanupFailed = true;
        }

        try
        {
            if (Directory.Exists(_inputRootPath))
            {
                // The input workspace deletes every file it created. Non-recursive removal
                // refuses an unexpected file instead of widening the cleanup target.
                Directory.Delete(_inputRootPath);
            }
        }
        catch (Exception)
        {
            cleanupFailed = true;
        }

        if (Directory.Exists(_projectsRootPath)
            || Directory.Exists(InputWorkspace.Path)
            || Directory.Exists(_inputRootPath))
        {
            cleanupFailed = true;
        }

        _disposed = true;
        if (cleanupFailed)
        {
            throw new PrivateCaseRunScopeException(PrivateCaseRunScopeFailure.CleanupFailed);
        }
    }

    public override string ToString() => "private case run scope";

    private static PrivateCaseRunScope Create(string? sqlServerConnectionString)
    {
        string? inputRootPath = null;
        PrivateCaseInputWorkspace? workspace = null;
        HandlerTestHost? host = null;
        try
        {
            inputRootPath = Directory.CreateTempSubdirectory("jet-private-case-run-").FullName;
            workspace = PrivateCaseInputWorkspace.Create(inputRootPath);
            host = new HandlerTestHost(sqlServerConnectionString: sqlServerConnectionString);
            return new PrivateCaseRunScope(inputRootPath, workspace, host);
        }
        catch (Exception)
        {
            var cleanupFailed = false;
            try
            {
                host?.Dispose();
            }
            catch (Exception)
            {
                cleanupFailed = true;
            }
            try
            {
                workspace?.Dispose();
            }
            catch (Exception)
            {
                cleanupFailed = true;
            }
            try
            {
                if (inputRootPath is not null && Directory.Exists(inputRootPath))
                {
                    Directory.Delete(inputRootPath);
                }
            }
            catch (Exception)
            {
                cleanupFailed = true;
            }

            throw new PrivateCaseRunScopeException(
                cleanupFailed
                    ? PrivateCaseRunScopeFailure.CleanupFailed
                    : PrivateCaseRunScopeFailure.CreationFailed);
        }
    }
}
