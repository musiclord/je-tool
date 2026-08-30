using System.Text;
using System.Text.Json;

namespace Jet.GuiDriver;

internal sealed record GuiScenarioDefinition(string Name, int ActionLimit);

internal static class GuiScenarioCatalog
{
    internal const string StartupSmoke = "startup-smoke";
    internal const string SyntheticSqliteCreate = "synthetic-sqlite-create";

    internal static bool TryResolve(string name, out GuiScenarioDefinition definition)
    {
        definition = name switch
        {
            StartupSmoke => new GuiScenarioDefinition(StartupSmoke, 4),
            SyntheticSqliteCreate => new GuiScenarioDefinition(SyntheticSqliteCreate, 16),
            _ => null!
        };
        return definition is not null;
    }
}

internal sealed record DriverOptions(
    string ApplicationPath,
    string ManifestPath,
    TimeSpan Timeout,
    GuiScenarioDefinition Scenario)
{
    internal static DriverOptions Parse(string[] args)
    {
        if (args.Length != 8)
        {
            throw new DriverUsageException("invalid_arguments");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            var name = args[index];
            if (name is not "--app" and not "--manifest" and not "--timeout-seconds" and not "--scenario"
                || !values.TryAdd(name, args[index + 1]))
            {
                throw new DriverUsageException("invalid_arguments");
            }
        }

        if (!GuiScenarioCatalog.TryResolve(values["--scenario"], out var scenario))
        {
            throw new DriverUsageException("invalid_scenario");
        }

        var applicationPath = Path.GetFullPath(values["--app"]);
        var manifestPath = Path.GetFullPath(values["--manifest"]);
        if (!File.Exists(applicationPath)
            || !Path.GetFileName(applicationPath).Equals("JET.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new DriverUsageException("application_not_found");
        }

        if (!Path.GetFileName(manifestPath).Equals(
                $"gui-{scenario.Name}-manifest.json",
                StringComparison.Ordinal))
        {
            throw new DriverUsageException("invalid_manifest_path");
        }

        var manifestDirectory = Path.GetDirectoryName(manifestPath);
        if (string.IsNullOrWhiteSpace(manifestDirectory) || !Directory.Exists(manifestDirectory))
        {
            throw new DriverUsageException("invalid_manifest_path");
        }

        if (!int.TryParse(values["--timeout-seconds"], out var timeoutSeconds)
            || timeoutSeconds is < 30 or > 240)
        {
            throw new DriverUsageException("invalid_timeout");
        }

        return new DriverOptions(
            applicationPath,
            manifestPath,
            TimeSpan.FromSeconds(timeoutSeconds),
            scenario);
    }
}

internal sealed class DriverUsageException(string code) : Exception
{
    internal string Code { get; } = code;
}

internal sealed class GuiCheckException(string code) : Exception
{
    internal string Code { get; } = code;
}

internal sealed class GuiInfrastructureException(string code, Exception? innerException = null)
    : Exception(code, innerException)
{
    internal string Code { get; } = code;
}

internal sealed class GuiAssertions
{
    internal bool DocumentLoaded { get; set; }
    internal bool BridgeReady { get; set; }
    internal bool SystemPingSucceeded { get; set; }
    internal bool ProjectPickerVisible { get; set; }
    internal bool ExitButtonVisible { get; set; }
    internal bool ExitRequested { get; set; }
    internal bool ProcessExited { get; set; }
    internal bool NewProjectButtonVisible { get; set; }
    internal bool CreateFormVisible { get; set; }
    internal bool RequiredFieldsEntered { get; set; }
    internal bool SqliteSelected { get; set; }
    internal bool ProjectCreated { get; set; }
    internal bool ImportStepVisible { get; set; }
    internal bool ProjectCodeVisible { get; set; }
    internal bool ProjectJsonExists { get; set; }
    internal bool SqliteDatabaseExists { get; set; }
    internal bool StoredProjectMatches { get; set; }
}

internal sealed class GuiProcessEvidence
{
    internal int? ProcessId { get; set; }
    internal DateTimeOffset? StartedUtc { get; set; }
    internal bool Exited { get; set; }
    internal int? ExitCode { get; set; }
    internal bool KillAttempted { get; set; }
    internal bool KillSucceeded { get; set; }
}

internal sealed class GuiCleanupEvidence
{
    internal bool RootRemoved { get; set; }
}

internal sealed class GuiRunOutcome
{
    internal GuiRunOutcome(GuiScenarioDefinition scenario)
    {
        Scenario = scenario;
    }

    internal GuiScenarioDefinition Scenario { get; }
    internal DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    internal DateTimeOffset CompletedUtc { get; set; }
    internal string Status { get; set; } = "infrastructure_error";
    internal int ExitCode { get; set; } = 4;
    internal string? ErrorCode { get; set; }
    internal string? ErrorType { get; set; }
    internal int ActionCount { get; set; }
    internal GuiAssertions Assertions { get; } = new();
    internal GuiProcessEvidence Process { get; } = new();
    internal GuiCleanupEvidence Cleanup { get; } = new();

    internal void RecordAction()
    {
        if (ActionCount >= Scenario.ActionLimit)
        {
            throw new GuiInfrastructureException("action_budget_exceeded");
        }

        ActionCount++;
    }
}

internal static class ManifestWriter
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    internal static void Write(string manifestPath, GuiRunOutcome outcome)
    {
        var error = outcome.ErrorCode is null
            ? Array.Empty<object>()
            : [new { code = outcome.ErrorCode, type = outcome.ErrorType }];
        var manifest = new
        {
            schemaVersion = 1,
            scenario = outcome.Scenario.Name,
            status = outcome.Status,
            exitCode = outcome.ExitCode,
            startedUtc = outcome.StartedUtc.ToString("O"),
            completedUtc = outcome.CompletedUtc.ToString("O"),
            durationSeconds = Math.Round((outcome.CompletedUtc - outcome.StartedUtc).TotalSeconds, 3),
            browserAutomation = new
            {
                protocol = "WebView2-CDP",
                loopbackOnly = true,
                arbitraryScriptAccepted = false
            },
            budget = new
            {
                actionLimit = outcome.Scenario.ActionLimit,
                actionCount = outcome.ActionCount,
                screenshotLimit = 0,
                screenshotCount = 0
            },
            assertions = new
            {
                documentLoaded = outcome.Assertions.DocumentLoaded,
                bridgeReady = outcome.Assertions.BridgeReady,
                systemPingSucceeded = outcome.Assertions.SystemPingSucceeded,
                projectPickerVisible = outcome.Assertions.ProjectPickerVisible,
                exitButtonVisible = outcome.Assertions.ExitButtonVisible,
                exitRequested = outcome.Assertions.ExitRequested,
                processExited = outcome.Assertions.ProcessExited,
                newProjectButtonVisible = outcome.Assertions.NewProjectButtonVisible,
                createFormVisible = outcome.Assertions.CreateFormVisible,
                requiredFieldsEntered = outcome.Assertions.RequiredFieldsEntered,
                sqliteSelected = outcome.Assertions.SqliteSelected,
                projectCreated = outcome.Assertions.ProjectCreated,
                importStepVisible = outcome.Assertions.ImportStepVisible,
                projectCodeVisible = outcome.Assertions.ProjectCodeVisible,
                projectJsonExists = outcome.Assertions.ProjectJsonExists,
                sqliteDatabaseExists = outcome.Assertions.SqliteDatabaseExists,
                storedProjectMatches = outcome.Assertions.StoredProjectMatches
            },
            process = new
            {
                processId = outcome.Process.ProcessId,
                startedUtc = outcome.Process.StartedUtc?.ToString("O"),
                exited = outcome.Process.Exited,
                exitCode = outcome.Process.ExitCode,
                killAttempted = outcome.Process.KillAttempted,
                killSucceeded = outcome.Process.KillSucceeded
            },
            cleanup = new
            {
                rootRemoved = outcome.Cleanup.RootRemoved
            },
            errors = error,
            privateData = new
            {
                state = "not_selected",
                pathInspected = false
            }
        };

        var temporaryPath = manifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, JsonOptions), Utf8);
            File.Move(temporaryPath, manifestPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static void WriteSummary(GuiRunOutcome outcome)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            scenario = outcome.Scenario.Name,
            status = outcome.Status,
            exitCode = outcome.ExitCode,
            errorCode = outcome.ErrorCode,
            privateData = new { pathInspected = false }
        }));
    }

    internal static void WriteUsageSummary(string code)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            scenario = "unknown",
            status = "usage_error",
            exitCode = 3,
            errorCode = code,
            privateData = new { pathInspected = false }
        }));
    }
}
