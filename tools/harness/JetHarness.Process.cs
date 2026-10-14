#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Jet.Harness;

public sealed class BoundedProcessResult
{
    public int ProcessId { get; init; }
    public bool HasExitCode { get; init; }
    public int ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public bool Cancelled { get; init; }
    public bool KillAttempted { get; init; }
    public bool KillSucceeded { get; init; }
    public string? KillError { get; init; }
    public bool OwnedProcessTree { get; init; }
    public bool? CleanupSucceeded { get; init; }
    public bool? BelowNormalApplied { get; init; }
    public long StandardOutputBytes { get; init; }
    public bool StandardOutputTruncated { get; init; }
    public long StandardErrorBytes { get; init; }
    public bool StandardErrorTruncated { get; init; }
    // 2026-10-13：自有程序樹的記憶體上限。沒有設定上限或不是自有程序樹時，上限是 null；尖峰只在自有程序樹讀得到。
    public long? ProcessTreeMemoryLimitBytes { get; init; }
    public long? PeakProcessTreeMemoryBytes { get; init; }
    public bool MemoryLimitReached { get; init; }
}

public static class BoundedProcessRunner
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly byte[] TruncationMarker = Utf8.GetBytes("[output truncated]\n");

    public static BoundedProcessResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? userProfileDirectory,
        string standardOutputPath,
        string standardErrorPath,
        int maximumBytesPerStream,
        TimeSpan timeout,
        IReadOnlyList<string> environmentVariablesToRemove,
        IReadOnlyDictionary<string, string> environmentVariablesToSet,
        IReadOnlyList<string> sensitiveValuesToRedact,
        int standardStreamCodePage) => Run(
            fileName, arguments, workingDirectory, userProfileDirectory,
            standardOutputPath, standardErrorPath, maximumBytesPerStream, timeout,
            environmentVariablesToRemove, environmentVariablesToSet, sensitiveValuesToRedact,
            standardStreamCodePage, false, CancellationToken.None);

    public static BoundedProcessResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? userProfileDirectory,
        string standardOutputPath,
        string standardErrorPath,
        int maximumBytesPerStream,
        TimeSpan timeout,
        IReadOnlyList<string> environmentVariablesToRemove,
        IReadOnlyDictionary<string, string> environmentVariablesToSet,
        IReadOnlyList<string> sensitiveValuesToRedact,
        int standardStreamCodePage,
        bool ownProcessTree = false,
        CancellationToken cancellationToken = default) => Run(
            fileName, arguments, workingDirectory, userProfileDirectory,
            standardOutputPath, standardErrorPath, maximumBytesPerStream, timeout,
            environmentVariablesToRemove, environmentVariablesToSet, sensitiveValuesToRedact,
            standardStreamCodePage, ownProcessTree, cancellationToken, 0);

    // maximumProcessTreeMemoryBytes 大於 0 時，整個自有程序樹的已認可記憶體合計不得超過這個值；
    // 一碰到上限就整批結束，結果標記 MemoryLimitReached（2026-10-13）。0 表示不設上限。
    public static BoundedProcessResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? userProfileDirectory,
        string standardOutputPath,
        string standardErrorPath,
        int maximumBytesPerStream,
        TimeSpan timeout,
        IReadOnlyList<string> environmentVariablesToRemove,
        IReadOnlyDictionary<string, string> environmentVariablesToSet,
        IReadOnlyList<string> sensitiveValuesToRedact,
        int standardStreamCodePage,
        bool ownProcessTree,
        CancellationToken cancellationToken,
        long maximumProcessTreeMemoryBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(standardOutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(standardErrorPath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environmentVariablesToRemove);
        ArgumentNullException.ThrowIfNull(environmentVariablesToSet);
        ArgumentNullException.ThrowIfNull(sensitiveValuesToRedact);

        if (maximumBytesPerStream < 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytesPerStream));
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (maximumProcessTreeMemoryBytes < 0
            || (maximumProcessTreeMemoryBytes > 0 && maximumProcessTreeMemoryBytes < 64L * 1024 * 1024))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumProcessTreeMemoryBytes));
        }

        if (maximumProcessTreeMemoryBytes > 0 && !ownProcessTree)
        {
            throw new ArgumentException(
                "A process-tree memory limit requires an owned process tree.",
                nameof(maximumProcessTreeMemoryBytes));
        }

        var standardStreamEncoding = standardStreamCodePage == Utf8.CodePage
            ? Utf8
            : Encoding.GetEncoding(standardStreamCodePage);

        using var userCancellation = new CancellationTokenSource();
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            userCancellation.Token,
            timeoutCancellation.Token,
            cancellationToken);

        var cancellationRequested = 0;
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Interlocked.Exchange(ref cancellationRequested, 1);
            userCancellation.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;
        try
        {
            return RunAsync(
                    fileName,
                    arguments,
                    workingDirectory,
                    userProfileDirectory,
                    standardOutputPath,
                    standardErrorPath,
                    maximumBytesPerStream,
                    environmentVariablesToRemove,
                    environmentVariablesToSet,
                    sensitiveValuesToRedact,
                    standardStreamEncoding,
                    linkedCancellation.Token,
                    timeoutCancellation,
                    () => Volatile.Read(ref cancellationRequested) == 1 || cancellationToken.IsCancellationRequested,
                    ownProcessTree,
                    maximumProcessTreeMemoryBytes)
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<BoundedProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? userProfileDirectory,
        string standardOutputPath,
        string standardErrorPath,
        int maximumBytesPerStream,
        IReadOnlyList<string> environmentVariablesToRemove,
        IReadOnlyDictionary<string, string> environmentVariablesToSet,
        IReadOnlyList<string> sensitiveValuesToRedact,
        Encoding standardStreamEncoding,
        CancellationToken cancellationToken,
        CancellationTokenSource timeoutCancellation,
        Func<bool> wasUserCancelled,
        bool ownProcessTree,
        long maximumProcessTreeMemoryBytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(standardOutputPath)
            ?? throw new InvalidOperationException("Standard-output path has no parent directory."));
        Directory.CreateDirectory(Path.GetDirectoryName(standardErrorPath)
            ?? throw new InvalidOperationException("Standard-error path has no parent directory."));

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = standardStreamEncoding,
            StandardErrorEncoding = standardStreamEncoding
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var variableName in environmentVariablesToRemove)
        {
            if (string.IsNullOrWhiteSpace(variableName))
            {
                throw new ArgumentException(
                    "Environment-variable removal names cannot be empty.",
                    nameof(environmentVariablesToRemove));
            }

            startInfo.Environment.Remove(variableName);
        }

        foreach (var variable in environmentVariablesToSet)
        {
            if (string.IsNullOrWhiteSpace(variable.Key)
                || variable.Key.Contains('=')
                || variable.Value is null)
            {
                throw new ArgumentException(
                    "Environment-variable overrides must contain valid names and values.",
                    nameof(environmentVariablesToSet));
            }

            startInfo.Environment[variable.Key] = variable.Value;
        }

        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["TESTINGPLATFORM_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION"] = "0";
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["NUGET_XMLDOC_MODE"] = "skip";

        var evidenceRedactions = CreateEvidenceRedactions(
            workingDirectory,
            userProfileDirectory,
            sensitiveValuesToRedact);
        if (ownProcessTree)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        using var ownedProcess = ownProcessTree ? OwnedWindowsProcess.Start(startInfo, maximumProcessTreeMemoryBytes) : null;
        using var process = ownedProcess?.Process ?? new Process { StartInfo = startInfo };
        if (ownedProcess is null && !process.Start())
        {
            throw new InvalidOperationException($"Unable to start child process: {fileName}");
        }

        var processId = process.Id;
        var stdoutTask = CaptureAsync(
            ownedProcess?.StandardOutput ?? process.StandardOutput,
            standardOutputPath,
            maximumBytesPerStream,
            evidenceRedactions);
        var stderrTask = CaptureAsync(
            ownedProcess?.StandardError ?? process.StandardError,
            standardErrorPath,
            maximumBytesPerStream,
            evidenceRedactions);

        var timedOut = false;
        var cancelled = false;
        var killAttempted = false;
        var killSucceeded = false;
        string? killError = null;
        var memoryLimitReached = false;
        long? peakProcessTreeMemory = null;

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = wasUserCancelled();
            timedOut = timeoutCancellation.IsCancellationRequested && !cancelled;
            if (ownedProcess is null)
            {
                killAttempted = true;
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }

                    using var killWait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await process.WaitForExitAsync(killWait.Token).ConfigureAwait(false);
                    killSucceeded = process.HasExited;
                }
                catch (Exception exception)
                {
                    killError = RedactForEvidence(
                        exception.GetType().Name + ": " + exception.Message,
                        evidenceRedactions);
                }
            }
        }

        if (ownedProcess is not null)
        {
            // 父程序正常離開時，子程序仍可能持有 stdout。先收掉本次 Job 的所有程序，再讀到 EOF。
            killAttempted = true;
            try
            {
                await ownedProcess.TerminateAndWaitAsync().ConfigureAwait(false);
                using var killWait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(killWait.Token).ConfigureAwait(false);
                killSucceeded = true;
            }
            catch (Exception exception)
            {
                killError = RedactForEvidence(
                    exception.GetType().Name + ": " + exception.Message,
                    evidenceRedactions);
            }
            finally
            {
                memoryLimitReached = ownedProcess.MemoryLimitReached;
                peakProcessTreeMemory = ownedProcess.TryReadPeakJobMemoryBytes();
                ownedProcess.CloseJob();
            }
        }

        var capturesTask = Task.WhenAll(stdoutTask, stderrTask);
        var captures = ownedProcess is null
            ? await capturesTask.ConfigureAwait(false)
            : await capturesTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var hasExitCode = process.HasExited;

        return new BoundedProcessResult
        {
            ProcessId = processId,
            HasExitCode = hasExitCode,
            ExitCode = hasExitCode ? process.ExitCode : -1,
            TimedOut = timedOut,
            Cancelled = cancelled,
            KillAttempted = killAttempted,
            KillSucceeded = killSucceeded,
            KillError = killError,
            OwnedProcessTree = ownProcessTree,
            CleanupSucceeded = ownProcessTree ? killSucceeded : null,
            BelowNormalApplied = ownProcessTree ? true : null,
            StandardOutputBytes = captures[0].Bytes,
            StandardOutputTruncated = captures[0].Truncated,
            StandardErrorBytes = captures[1].Bytes,
            StandardErrorTruncated = captures[1].Truncated,
            ProcessTreeMemoryLimitBytes = ownProcessTree && maximumProcessTreeMemoryBytes > 0 ? maximumProcessTreeMemoryBytes : null,
            PeakProcessTreeMemoryBytes = peakProcessTreeMemory,
            MemoryLimitReached = memoryLimitReached
        };
    }

    private static async Task<CaptureResult> CaptureAsync(
        StreamReader reader,
        string destinationPath,
        int maximumBytes,
        IReadOnlyList<EvidenceRedaction> evidenceRedactions)
    {
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 16 * 1024,
            useAsync: true);

        long written = 0;
        var truncated = false;
        string? line;

        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            line = RedactForEvidence(line, evidenceRedactions);
            var bytes = Utf8.GetBytes(line + "\n");
            var writableLimit = maximumBytes - TruncationMarker.Length;

            if (!truncated && written + bytes.Length <= writableLimit)
            {
                await output.WriteAsync(bytes).ConfigureAwait(false);
                written += bytes.Length;
                continue;
            }

            if (truncated)
            {
                continue;
            }

            var remaining = Math.Max(0, writableLimit - (int)written);
            if (remaining > 0)
            {
                var prefix = GetUtf8Prefix(line + "\n", remaining);
                if (prefix.Length > 0)
                {
                    var prefixBytes = Utf8.GetBytes(prefix);
                    await output.WriteAsync(prefixBytes).ConfigureAwait(false);
                    written += prefixBytes.Length;
                }
            }

            await output.WriteAsync(TruncationMarker).ConfigureAwait(false);
            written += TruncationMarker.Length;
            truncated = true;
        }

        await output.FlushAsync().ConfigureAwait(false);
        return new CaptureResult(written, truncated);
    }

    private static string GetUtf8Prefix(string value, int maximumBytes)
    {
        var low = 0;
        var high = value.Length;

        while (low < high)
        {
            var middle = low + ((high - low + 1) / 2);
            if (Utf8.GetByteCount(value.Substring(0, middle)) <= maximumBytes)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return value.Substring(0, low);
    }

    private static IReadOnlyList<EvidenceRedaction> CreateEvidenceRedactions(
        string workingDirectory,
        string? userProfileDirectory,
        IReadOnlyList<string> sensitiveValuesToRedact)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddEvidenceRedaction(values, workingDirectory, "[repository]");
        AddEvidenceRedaction(values, userProfileDirectory, "[user-profile]");
        foreach (var sensitiveValue in sensitiveValuesToRedact)
        {
            AddSensitiveRedaction(values, sensitiveValue);
        }

        var redactions = new List<EvidenceRedaction>(values.Count);
        foreach (var value in values)
        {
            redactions.Add(new EvidenceRedaction(value.Key, value.Value));
        }

        redactions.Sort((left, right) => right.Source.Length.CompareTo(left.Source.Length));
        return redactions;
    }

    private static void AddEvidenceRedaction(
        IDictionary<string, string> values,
        string? source,
        string replacement)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        var normalized = Path.GetFullPath(source)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalized.Length < 4)
        {
            return;
        }

        values[normalized] = replacement;
        values[normalized.Replace('\\', '/')] = replacement;
    }

    private static void AddSensitiveRedaction(
        IDictionary<string, string> values,
        string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        values[source] = "[sensitive]";
    }

    private static string RedactForEvidence(
        string value,
        IReadOnlyList<EvidenceRedaction> evidenceRedactions)
    {
        foreach (var redaction in evidenceRedactions)
        {
            value = value.Replace(
                redaction.Source,
                redaction.Replacement,
                StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    private sealed class OwnedWindowsProcess : IDisposable
    {
        private const uint JobMessageJobMemoryLimit = 10; // JOB_OBJECT_MSG_JOB_MEMORY_LIMIT
        private const uint JobMessageNotificationLimit = 11; // JOB_OBJECT_MSG_NOTIFICATION_LIMIT
        private const uint StatusNoMemory = 0xC0000017; // 碰到記憶體上限時，整批程序的結束碼
        private readonly SafeJobHandle _job;
        private readonly IntPtr _completionPort;
        private readonly Thread? _memoryMonitor;
        private int _memoryLimitReached;
        private volatile bool _stopMemoryMonitor;

        private OwnedWindowsProcess(
            Process process,
            SafeJobHandle job,
            IntPtr completionPort,
            StreamReader standardOutput,
            StreamReader standardError)
        {
            Process = process;
            _job = job;
            _completionPort = completionPort;
            StandardOutput = standardOutput;
            StandardError = standardError;
            if (completionPort != IntPtr.Zero)
            {
                _memoryMonitor = new Thread(MonitorMemoryLimit) { IsBackground = true, Name = "Jet owned process memory limit" };
                _memoryMonitor.Start();
            }
        }

        internal Process Process { get; }
        internal StreamReader StandardOutput { get; }
        internal StreamReader StandardError { get; }
        internal bool MemoryLimitReached => Volatile.Read(ref _memoryLimitReached) == 1;

        // 上限分兩層。程序樹合計的已認可記憶體超過上限時，Windows 送出預警通知，這裡收到就結束整個 Job。
        // .NET 程式在有記憶體上限的 Job 裡，會把自己的堆積上限設成 Job 硬上限的 75%，所以硬上限放在上限的 1.5 倍：
        // .NET 程式不會在碰到上限之前先自己丟出記憶體不足，硬上限也還在，預警漏接時配置會直接失敗並送出另一種通知。
        private void MonitorMemoryLimit()
        {
            while (!_stopMemoryMonitor)
            {
                if (!Native.GetQueuedCompletionStatus(_completionPort, out var message, out _, out _, 200))
                {
                    if (Marshal.GetLastWin32Error() == 258) continue; // WAIT_TIMEOUT
                    return;
                }
                if (message != JobMessageNotificationLimit && message != JobMessageJobMemoryLimit) continue;
                Interlocked.Exchange(ref _memoryLimitReached, 1);
                try { Native.TerminateJobObject(_job, StatusNoMemory); }
                catch (ObjectDisposedException) { return; }
            }
        }

        internal long? TryReadPeakJobMemoryBytes()
        {
            if (_job.IsClosed) return null;
            return Native.QueryInformationJobObject(
                _job, 9, out Native.ExtendedLimitInformation limits, Marshal.SizeOf<Native.ExtendedLimitInformation>(), IntPtr.Zero)
                ? (long)limits.PeakJobMemoryUsed
                : null;
        }

        internal static OwnedWindowsProcess Start(ProcessStartInfo startInfo, long memoryLimitBytes = 0)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Owned process trees require Windows Job Objects.");
            }

            var job = Native.CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid)
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the owned process job.");
                job.Dispose();
                throw error;
            }

            Process? process = null;
            AnonymousPipeServerStream? stdout = null;
            AnonymousPipeServerStream? stderr = null;
            AnonymousPipeServerStream? stdin = null;
            var processInfo = new Native.ProcessInformation();
            var attributes = IntPtr.Zero;
            var handleList = IntPtr.Zero;
            var environment = IntPtr.Zero;
            var completionPort = IntPtr.Zero;
            var attributesInitialized = false;
            try
            {
                var limits = new Native.ExtendedLimitInformation();
                limits.BasicLimitInformation.LimitFlags = 0x2000 | 0x20; // KILL_ON_JOB_CLOSE | PRIORITY_CLASS
                limits.BasicLimitInformation.PriorityClass = 0x4000; // BELOW_NORMAL_PRIORITY_CLASS
                if (memoryLimitBytes > 0)
                {
                    limits.BasicLimitInformation.LimitFlags |= 0x200; // JOB_OBJECT_LIMIT_JOB_MEMORY，硬上限
                    limits.JobMemoryLimit = (nuint)(memoryLimitBytes + memoryLimitBytes / 2);
                }
                if (!Native.SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<Native.ExtendedLimitInformation>()))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to configure the owned process job.");
                }
                if (memoryLimitBytes > 0)
                {
                    // 先接上通知與預警門檻，再放任何程序進 Job，才不會漏掉第一次超過上限的通知。
                    completionPort = Native.CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, UIntPtr.Zero, 1);
                    if (completionPort == IntPtr.Zero)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the job notification port.");
                    }
                    var association = new Native.AssociateCompletionPort { CompletionKey = IntPtr.Zero, CompletionPort = completionPort };
                    if (!Native.SetInformationJobObject(job, 7, ref association, Marshal.SizeOf<Native.AssociateCompletionPort>()))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to attach the job notification port.");
                    }
                    var notification = new Native.NotificationLimitInformation
                    {
                        JobMemoryLimit = (ulong)memoryLimitBytes,
                        RateControlTolerance = 1, // ToleranceLow；只有限制速率時才用到，這裡填合法值
                        RateControlToleranceInterval = 1, // ToleranceIntervalShort
                        LimitFlags = 0x200 // JOB_OBJECT_LIMIT_JOB_MEMORY，預警門檻
                    };
                    if (!Native.SetInformationJobObject(job, 12, ref notification, Marshal.SizeOf<Native.NotificationLimitInformation>()))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to set the job memory notification limit.");
                    }
                }

                stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
                var inheritedHandles = new[]
                {
                    stdin.ClientSafePipeHandle.DangerousGetHandle(),
                    stdout.ClientSafePipeHandle.DangerousGetHandle(),
                    stderr.ClientSafePipeHandle.DangerousGetHandle(),
                };

                nuint attributeBytes = 0;
                Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeBytes);
                if (attributeBytes == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to size process startup attributes.");
                }
                attributes = Marshal.AllocHGlobal(checked((int)attributeBytes));
                if (!Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref attributeBytes))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to initialize process startup attributes.");
                }
                attributesInitialized = true;
                handleList = Marshal.AllocHGlobal(IntPtr.Size * inheritedHandles.Length);
                Marshal.Copy(inheritedHandles, 0, handleList, inheritedHandles.Length);
                if (!Native.UpdateProcThreadAttribute(
                    attributes, 0, (nuint)0x20002, handleList, (nuint)(IntPtr.Size * inheritedHandles.Length), IntPtr.Zero, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to restrict inherited process handles.");
                }

                var startup = new Native.StartupInfoEx();
                startup.StartupInfo.Size = Marshal.SizeOf<Native.StartupInfoEx>();
                startup.StartupInfo.Flags = 0x100; // STARTF_USESTDHANDLES
                startup.StartupInfo.StandardInput = inheritedHandles[0];
                startup.StartupInfo.StandardOutput = inheritedHandles[1];
                startup.StartupInfo.StandardError = inheritedHandles[2];
                startup.AttributeList = attributes;
                var commandLine = new StringBuilder(QuoteArgument(startInfo.FileName));
                foreach (var argument in startInfo.ArgumentList)
                {
                    commandLine.Append(' ').Append(QuoteArgument(argument));
                }
                var environmentEntries = new List<string>();
                foreach (var entry in startInfo.Environment)
                {
                    if (entry.Key.Contains('\0') || entry.Value?.Contains('\0') == true)
                    {
                        throw new ArgumentException("Process environment entries cannot contain null characters.");
                    }
                    environmentEntries.Add(entry.Key + "=" + entry.Value);
                }
                environmentEntries.Sort(StringComparer.OrdinalIgnoreCase);
                environment = Marshal.StringToHGlobalUni(string.Join('\0', environmentEntries) + "\0\0");

                // 暫停狀態下才加入 Job，讓任何目標程式碼都不能在取得歸屬之前執行。
                const uint creationFlags = 0x4 | 0x4000 | 0x08000000 | 0x400 | 0x80000;
                if (!Native.CreateProcessW(
                    null, commandLine, IntPtr.Zero, IntPtr.Zero, true, creationFlags,
                    environment, startInfo.WorkingDirectory, ref startup, out processInfo))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the suspended child process.");
                }
                stdout.DisposeLocalCopyOfClientHandle();
                stderr.DisposeLocalCopyOfClientHandle();
                stdin.DisposeLocalCopyOfClientHandle();
                stdin.Dispose();
                stdin = null;

                if (!Native.AssignProcessToJobObject(job, processInfo.Process))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to assign the suspended child to its job.");
                }
                process = Process.GetProcessById(processInfo.ProcessId);
                _ = process.SafeHandle; // 恢復前保留程序 handle，之後不靠可能重用的 PID 找回它。
                if (Native.ResumeThread(processInfo.Thread) == uint.MaxValue)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to resume the owned child process.");
                }

                return new OwnedWindowsProcess(
                    process, job, completionPort,
                    new StreamReader(stdout, startInfo.StandardOutputEncoding ?? Utf8),
                    new StreamReader(stderr, startInfo.StandardErrorEncoding ?? Utf8));
            }
            catch (Exception startupException)
            {
                job.Dispose();
                if (completionPort != IntPtr.Zero) Native.CloseHandle(completionPort);
                var stopped = true;
                if (processInfo.Process != IntPtr.Zero)
                {
                    Native.TerminateProcess(processInfo.Process, 1);
                    stopped = Native.WaitForSingleObject(processInfo.Process, 10_000) == 0;
                }
                process?.Dispose();
                stdout?.DisposeLocalCopyOfClientHandle();
                stderr?.DisposeLocalCopyOfClientHandle();
                stdout?.Dispose();
                stderr?.Dispose();
                if (!stopped)
                {
                    throw new InvalidOperationException("The suspended child could not be confirmed stopped after startup failed.", startupException);
                }
                throw;
            }
            finally
            {
                stdin?.DisposeLocalCopyOfClientHandle();
                stdin?.Dispose();
                if (processInfo.Thread != IntPtr.Zero) Native.CloseHandle(processInfo.Thread);
                if (processInfo.Process != IntPtr.Zero) Native.CloseHandle(processInfo.Process);
                if (attributesInitialized) Native.DeleteProcThreadAttributeList(attributes);
                if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
                if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            }
        }

        internal async Task TerminateAndWaitAsync()
        {
            if (!Native.TerminateJobObject(_job, 1))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to terminate the owned process job.");
            }
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                if (!Native.QueryInformationJobObject(
                    _job, 1, out Native.BasicAccountingInformation accounting, Marshal.SizeOf<Native.BasicAccountingInformation>(), IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to confirm owned process cleanup.");
                }
                if (accounting.ActiveProcesses == 0) return;
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(10))
                {
                    throw new TimeoutException("Owned process cleanup exceeded its time limit.");
                }
                await Task.Delay(20).ConfigureAwait(false);
            }
        }

        private static string QuoteArgument(string value)
        {
            if (value.Contains('\0')) throw new ArgumentException("Process arguments cannot contain null characters.");
            var result = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                result.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
                result.Append(character);
                backslashes = 0;
            }
            return result.Append('\\', backslashes * 2).Append('"').ToString();
        }

        public void Dispose()
        {
            CloseJob();
            StandardOutput.Dispose();
            StandardError.Dispose();
        }

        internal void CloseJob()
        {
            if (_completionPort != IntPtr.Zero && !_stopMemoryMonitor)
            {
                _stopMemoryMonitor = true;
                _memoryMonitor?.Join(TimeSpan.FromSeconds(2));
                Native.CloseHandle(_completionPort);
            }
            _job.Dispose();
        }
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => Native.CloseHandle(handle);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct BasicLimitInformation
        {
            internal long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            internal uint LimitFlags;
            internal nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
            internal uint ActiveProcessLimit;
            internal nuint Affinity;
            internal uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters
        {
            internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            internal ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ExtendedLimitInformation
        {
            internal BasicLimitInformation BasicLimitInformation;
            internal IoCounters IoInfo;
            internal nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct AssociateCompletionPort
        {
            internal IntPtr CompletionKey, CompletionPort;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NotificationLimitInformation
        {
            internal ulong IoReadBytesLimit, IoWriteBytesLimit;
            internal long PerJobUserTimeLimit;
            internal ulong JobMemoryLimit;
            internal int RateControlTolerance, RateControlToleranceInterval;
            internal uint LimitFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BasicAccountingInformation
        {
            internal long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
            internal uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfo
        {
            internal int Size;
            internal IntPtr Reserved, Desktop, Title;
            internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            internal ushort ShowWindow, ReservedBytes;
            internal IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfoEx
        {
            internal StartupInfo StartupInfo;
            internal IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation
        {
            internal IntPtr Process, Thread;
            internal int ProcessId, ThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeJobHandle CreateJobObjectW(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass, ref ExtendedLimitInformation information, int length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass, ref AssociateCompletionPort information, int length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass, ref NotificationLimitInformation information, int length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(SafeJobHandle job, int informationClass, out BasicAccountingInformation information, int length, IntPtr returnedLength);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(SafeJobHandle job, int informationClass, out ExtendedLimitInformation information, int length, IntPtr returnedLength);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateIoCompletionPort(IntPtr fileHandle, IntPtr existingCompletionPort, UIntPtr completionKey, uint numberOfConcurrentThreads);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetQueuedCompletionStatus(IntPtr completionPort, out uint numberOfBytes, out UIntPtr completionKey, out IntPtr overlapped, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, uint flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previousValue, IntPtr returnedSize);
        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(IntPtr attributes);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessW(string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string directory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }

    private readonly record struct CaptureResult(long Bytes, bool Truncated);
    private readonly record struct EvidenceRedaction(string Source, string Replacement);
}
