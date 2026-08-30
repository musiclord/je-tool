#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
    public long StandardOutputBytes { get; init; }
    public bool StandardOutputTruncated { get; init; }
    public long StandardErrorBytes { get; init; }
    public bool StandardErrorTruncated { get; init; }
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
        int standardStreamCodePage)
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

        var standardStreamEncoding = standardStreamCodePage == Utf8.CodePage
            ? Utf8
            : Encoding.GetEncoding(standardStreamCodePage);

        using var userCancellation = new CancellationTokenSource();
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            userCancellation.Token,
            timeoutCancellation.Token);

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
                    () => Volatile.Read(ref cancellationRequested) == 1)
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
        Func<bool> wasUserCancelled)
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
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start child process: {fileName}");
        }

        var processId = process.Id;
        var stdoutTask = CaptureAsync(
            process.StandardOutput,
            standardOutputPath,
            maximumBytesPerStream,
            evidenceRedactions);
        var stderrTask = CaptureAsync(
            process.StandardError,
            standardErrorPath,
            maximumBytesPerStream,
            evidenceRedactions);

        var timedOut = false;
        var cancelled = false;
        var killAttempted = false;
        var killSucceeded = false;
        string? killError = null;

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = wasUserCancelled();
            timedOut = timeoutCancellation.IsCancellationRequested && !cancelled;
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

        var captures = await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
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
            StandardOutputBytes = captures[0].Bytes,
            StandardOutputTruncated = captures[0].Truncated,
            StandardErrorBytes = captures[1].Bytes,
            StandardErrorTruncated = captures[1].Truncated
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

    private readonly record struct CaptureResult(long Bytes, bool Truncated);
    private readonly record struct EvidenceRedaction(string Source, string Replacement);
}
