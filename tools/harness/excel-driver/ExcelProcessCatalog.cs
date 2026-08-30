using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Jet.ExcelDriver;

internal sealed class WindowsExcelProcessCatalog : IExcelProcessCatalog
{
    private static readonly TimeSpan ActivationClockTolerance = TimeSpan.FromSeconds(2);

    public ExcelProcessSnapshot CaptureSnapshot()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("EXCEL");
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            throw new ExcelProcessOwnershipException(exception);
        }

        var identities = new Dictionary<int, long?>();
        try
        {
            foreach (var process in processes)
            {
                long? startTime = null;
                try
                {
                    startTime = process.StartTime.ToUniversalTime().Ticks;
                }
                catch (Exception exception) when (exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or NotSupportedException)
                {
                }

                identities[process.Id] = startTime;
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return new ExcelProcessSnapshot(identities);
    }

    public ExcelProcessIdentity ResolveOwnedProcess(
        long applicationHwnd,
        DateTimeOffset activationStartedUtc,
        ExcelProcessSnapshot baseline)
    {
        if (applicationHwnd <= 0
            || GetWindowThreadProcessId(new IntPtr(applicationHwnd), out var processId) == 0
            || processId == 0
            || processId > int.MaxValue
            || baseline.Processes.ContainsKey((int)processId))
        {
            throw new ExcelProcessOwnershipException();
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var startTimeUtc = process.StartTime.ToUniversalTime();
            if (!string.Equals(process.ProcessName, "EXCEL", StringComparison.OrdinalIgnoreCase)
                || startTimeUtc < activationStartedUtc.UtcDateTime - ActivationClockTolerance)
            {
                throw new ExcelProcessOwnershipException();
            }

            return new ExcelProcessIdentity(process.Id, startTimeUtc.Ticks);
        }
        catch (ExcelProcessOwnershipException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            throw new ExcelProcessOwnershipException(exception);
        }
    }

    public bool WaitForExitExact(ExcelProcessIdentity identity, TimeSpan timeout) =>
        WithExactProcess(identity, process =>
        {
            var milliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
            return process.WaitForExit(milliseconds);
        }, whenMissingOrReused: true);

    public bool TryKillExact(ExcelProcessIdentity identity) =>
        WithExactProcess(identity, process =>
        {
            process.Kill();
            return process.WaitForExit((int)TimeSpan.FromSeconds(5).TotalMilliseconds);
        }, whenMissingOrReused: true);

    private static bool WithExactProcess(
        ExcelProcessIdentity identity,
        Func<Process, bool> action,
        bool whenMissingOrReused)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (!string.Equals(process.ProcessName, "EXCEL", StringComparison.OrdinalIgnoreCase)
                || process.StartTime.ToUniversalTime().Ticks != identity.StartTimeUtcTicks)
            {
                return whenMissingOrReused;
            }

            return action(process);
        }
        catch (ArgumentException)
        {
            return whenMissingOrReused;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}

internal sealed class ExactProcessDeadline : IExcelDeadline
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _watchdog;
    private int _timedOut;
    private int _processTerminated;
    private int _disposed;

    internal ExactProcessDeadline(
        IExcelProcessCatalog processCatalog,
        ExcelProcessIdentity identity,
        TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _watchdog = WatchAsync(processCatalog, identity, timeout, _cancellation.Token);
    }

    public bool TimedOut => Volatile.Read(ref _timedOut) == 1;

    public bool ProcessTerminated => Volatile.Read(ref _processTerminated) == 1;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        try
        {
            _watchdog.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private async Task WatchAsync(
        IExcelProcessCatalog processCatalog,
        ExcelProcessIdentity identity,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        Interlocked.Exchange(ref _timedOut, 1);
        if (processCatalog.TryKillExact(identity))
        {
            Interlocked.Exchange(ref _processTerminated, 1);
        }
    }
}
