using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jet.GuiDriver;

internal sealed partial class OwnedGuiRun : IDisposable
{
    private const string DirectoryPrefix = "jet-agent-gui-";
    private const string MarkerFileName = ".jet-agent-gui-run.json";
    private const int MaximumCleanupEntries = 20_000;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly string[] EnvironmentVariablesToRemove =
    [
        "JET_ACCOUNT_MAPPING_EVIDENCE_PATH",
        "JET_AGENT_GUI_CHILD",
        "JET_AGENT_GUI_ROOT",
        "JET_LEGACY_PARITY_KEEP_OUTPUTS",
        "JET_LEGACY_PARITY_PROVIDER",
        "JET_LEGACY_PARITY_STOP_AFTER",
        "JET_PRESCREEN_MONTHLY_BENCHMARK",
        "JET_PRESCREEN_MONTHLY_BENCHMARK_OUTPUT",
        "JET_PROJECTS_ROOT",
        "JET_SIX_REPORT_EVIDENCE_DIR",
        "JET_SQLSERVER_CONNECTION",
        "JET_STEP41_BASELINE_ROOT",
        "JET_STEP41_PARITY_EVIDENCE_ROOT",
        "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
        "WEBVIEW2_USER_DATA_FOLDER"
    ];

    private Process? _process;

    private OwnedGuiRun(string runId, string rootPath)
    {
        RunId = runId;
        RootPath = rootPath;
    }

    internal string RunId { get; }
    internal string RootPath { get; }
    internal Process? Process => _process;
    internal string ProjectsRootPath => Path.Combine(RootPath, "projects");
    internal string DevToolsActivePortPath => Path.Combine(
        RootPath,
        "children",
        "primary",
        "webview2",
        "EBWebView",
        "DevToolsActivePort");
    internal string FixtureTracePath => Path.Combine(
        RootPath,
        "children",
        "primary",
        "logs",
        "agent-gui-fixtures.ndjson");

    internal static OwnedGuiRun Create(
        TimeSpan timeout,
        int actionBudget,
        IReadOnlyList<string> fixtures,
        int screenshotBudget = 0)
    {
        if (actionBudget is < 1 or > 96)
        {
            throw new GuiInfrastructureException("action_budget_invalid");
        }
        if (screenshotBudget is < 0 or > 2)
        {
            throw new GuiInfrastructureException("screenshot_budget_invalid");
        }

        var runId = Guid.NewGuid().ToString("N");
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var rootPath = Path.Combine(tempRoot, DirectoryPrefix + runId);
        if (!Path.GetDirectoryName(rootPath)!.Equals(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !OwnedDirectoryName().IsMatch(Path.GetFileName(rootPath)))
        {
            throw new GuiInfrastructureException("owned_root_invalid");
        }

        Directory.CreateDirectory(rootPath);
        RejectReparsePoint(rootPath);
        var ownedRun = new OwnedGuiRun(runId, rootPath);
        try
        {
            var marker = new
            {
                schemaVersion = 1,
                runId,
                deadlineUtc = DateTimeOffset.UtcNow.Add(timeout).ToString("O"),
                actionBudget,
                screenshotBudget,
                childCount = 1,
                fixtures
            };
            var markerPath = Path.Combine(rootPath, MarkerFileName);
            using var stream = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, Utf8);
            writer.Write(JsonSerializer.Serialize(marker));
            return ownedRun;
        }
        catch
        {
            ownedRun.TryRemoveRoot(out _);
            throw;
        }
    }

    internal Process StartApplication(string applicationPath)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("Application process has already started.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = applicationPath,
            WorkingDirectory = Path.GetDirectoryName(applicationPath)
                ?? throw new GuiInfrastructureException("application_directory_missing"),
            UseShellExecute = false,
            CreateNoWindow = false
        };
        foreach (var variableName in EnvironmentVariablesToRemove)
        {
            startInfo.Environment.Remove(variableName);
        }

        startInfo.Environment["JET_AGENT_GUI_ROOT"] = RootPath;
        startInfo.Environment["JET_AGENT_GUI_CHILD"] = "primary";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["NO_COLOR"] = "1";

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!_process.Start())
        {
            throw new GuiInfrastructureException("application_start_failed");
        }

        return _process;
    }

    internal async Task<bool> StopApplicationAsync(GuiProcessEvidence evidence)
    {
        if (_process is null)
        {
            return true;
        }

        if (_process.HasExited)
        {
            return true;
        }

        evidence.KillAttempted = true;
        try
        {
            _process.Kill(entireProcessTree: true);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            evidence.KillSucceeded = _process.HasExited;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            evidence.KillSucceeded = _process.HasExited;
        }

        return _process.HasExited;
    }

    internal bool TryRemoveRoot(out string? errorCode)
    {
        errorCode = null;
        if (!Directory.Exists(RootPath))
        {
            return true;
        }

        var validationDeadline = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            try
            {
                ValidateOwnedRootBeforeDelete();
                break;
            }
            catch (IOException) when (DateTime.UtcNow < validationDeadline)
            {
                Thread.Sleep(150);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < validationDeadline)
            {
                Thread.Sleep(150);
            }
            catch
            {
                errorCode = "owned_root_validation_failed";
                return false;
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(8);
        do
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
                return !Directory.Exists(RootPath);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }
            catch
            {
                errorCode = "owned_root_delete_failed";
                return false;
            }
        }
        while (DateTime.UtcNow < deadline);

        errorCode = "owned_root_delete_failed";
        return false;
    }

    private void ValidateOwnedRootBeforeDelete()
    {
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootPath));
        if (!Path.GetDirectoryName(rootFull)!.Equals(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(rootFull).Equals(DirectoryPrefix + RunId, StringComparison.Ordinal)
            || !OwnedDirectoryName().IsMatch(Path.GetFileName(rootFull)))
        {
            throw new InvalidOperationException("Owned root escaped the process temp directory.");
        }

        RejectReparsePoint(rootFull);
        var markerPath = Path.Combine(rootFull, MarkerFileName);
        RejectReparsePoint(markerPath);
        using (var marker = JsonDocument.Parse(File.ReadAllText(markerPath)))
        {
            if (!marker.RootElement.TryGetProperty("schemaVersion", out var schemaVersion)
                || schemaVersion.GetInt32() != 1
                || !marker.RootElement.TryGetProperty("runId", out var runId)
                || !string.Equals(runId.GetString(), RunId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Owned root marker does not match this run.");
            }
        }

        var directories = new Stack<string>();
        directories.Push(rootFull);
        var entryCount = 0;
        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                entryCount++;
                if (entryCount > MaximumCleanupEntries)
                {
                    throw new InvalidOperationException("Owned root exceeded the cleanup entry limit.");
                }

                RejectReparsePoint(entry);
                if (Directory.Exists(entry))
                {
                    directories.Push(entry);
                }
            }
        }
    }

    internal static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new GuiInfrastructureException("reparse_point_rejected");
        }
    }

    [GeneratedRegex("^jet-agent-gui-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnedDirectoryName();

    public void Dispose()
    {
        _process?.Dispose();
    }
}
