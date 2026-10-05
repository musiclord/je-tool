#if JET_AGENT_GUI_TEST
using System.Globalization;
using System.Text.Json;

namespace JET;

/// <summary>
/// AgentGuiTest 組建專用的隔離執行設定。這個型別不會編入一般 Debug 或 Release。
/// </summary>
internal sealed record AgentGuiTestProfile(
    string RunId,
    string ChildId,
    int ChildCount,
    string RootPath,
    string ChildRootPath,
    string ProjectsRootPath,
    string WebViewUserDataFolder,
    string DiagnosticLogDirectory,
    string UserProfileDirectory,
    string ArtifactDirectory,
    DateTimeOffset DeadlineUtc,
    int ActionBudget,
    int ScreenshotBudget,
    IReadOnlyList<string> FixtureIds)
{
    internal const string RootEnvironmentVariable = "JET_AGENT_GUI_ROOT";
    internal const string ChildEnvironmentVariable = "JET_AGENT_GUI_CHILD";
    internal const string MarkerFileName = ".jet-agent-gui-run.json";
    internal const string DirectoryPrefix = "jet-agent-gui-";
    internal const string PrimaryChildId = "primary";
    internal const string SecondaryChildId = "secondary";
    internal const int MaximumDurationSeconds = 300;
    // 測試組態自己的安全上限，要不小於 tools/harness/lanes.json 裡最大的情境預算；各情境的預算只寫在 lanes.json。
    internal const int MaximumActionBudget = 102;
    internal const int MaximumScreenshotBudget = 2;

    internal const string IsolatedSqlServerConnectionString =
        "Data Source=tcp:127.0.0.1,1;"
        + "Initial Catalog=JET_AGENT_GUI_TEST;"
        + "Integrated Security=True;"
        + "Encrypt=True;"
        + "TrustServerCertificate=True;"
        + "Connect Timeout=1;"
        + "Application Name=JET Agent GUI Harness";

    internal const string IsolatedPrincipal = @"JET\agent-gui";

    internal static AgentGuiTestProfile LoadRequired()
    {
        var rawRoot = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(rawRoot))
        {
            throw new InvalidOperationException(
                $"{RootEnvironmentVariable} is required for the AgentGuiTest build.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawRoot.Trim()));
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var relativeToTemp = Path.GetRelativePath(tempRoot, root);
        if (Path.IsPathFullyQualified(relativeToTemp)
            || relativeToTemp.Equals("..", StringComparison.Ordinal)
            || relativeToTemp.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Agent GUI root must be a child of the process temp directory: {tempRoot}");
        }

        var directoryName = Path.GetFileName(root);
        if (!directoryName.StartsWith(DirectoryPrefix, StringComparison.Ordinal)
            || directoryName.Length != DirectoryPrefix.Length + 32)
        {
            throw new InvalidOperationException(
                $"Agent GUI root name must be {DirectoryPrefix}<32 lowercase hex characters>.");
        }

        var runIdFromDirectory = directoryName[DirectoryPrefix.Length..];
        if (!runIdFromDirectory.All(IsLowerHex))
        {
            throw new InvalidOperationException("Agent GUI run id must contain lowercase hexadecimal characters only.");
        }

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Agent GUI root does not exist: {root}");
        }

        RejectReparsePoint(root, "Agent GUI root");

        var markerPath = Path.Combine(root, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            throw new FileNotFoundException("Agent GUI marker file is missing.", markerPath);
        }

        RejectReparsePoint(markerPath, "Agent GUI marker");

        using var markerDocument = JsonDocument.Parse(File.ReadAllText(markerPath));
        var marker = markerDocument.RootElement;
        if (marker.ValueKind != JsonValueKind.Object
            || !marker.TryGetProperty("schemaVersion", out var schemaVersion)
            || schemaVersion.GetInt32() != 1)
        {
            throw new InvalidOperationException("Agent GUI marker schemaVersion must be 1.");
        }

        var runId = ReadRequiredString(marker, "runId");
        if (!runId.Equals(runIdFromDirectory, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Agent GUI marker runId does not match its directory.");
        }

        var deadlineText = ReadRequiredString(marker, "deadlineUtc");
        if (!DateTimeOffset.TryParse(
                deadlineText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var deadlineUtc))
        {
            throw new InvalidOperationException("Agent GUI marker deadlineUtc is not a valid UTC timestamp.");
        }

        var now = DateTimeOffset.UtcNow;
        if (deadlineUtc <= now)
        {
            throw new InvalidOperationException("Agent GUI marker deadline has already expired.");
        }

        if (deadlineUtc - now > TimeSpan.FromSeconds(MaximumDurationSeconds))
        {
            throw new InvalidOperationException(
                $"Agent GUI marker deadline exceeds the {MaximumDurationSeconds}-second hard limit.");
        }

        var actionBudget = ReadBudget(marker, "actionBudget", MaximumActionBudget);
        var screenshotBudget = ReadBudget(marker, "screenshotBudget", MaximumScreenshotBudget, allowZero: true);
        var childCount = ReadChildCount(marker);
        var fixtureIds = ReadFixtureIds(marker);

        var rawChildId = Environment.GetEnvironmentVariable(ChildEnvironmentVariable);
        var childId = string.IsNullOrEmpty(rawChildId) ? PrimaryChildId : rawChildId;
        if (childId is not PrimaryChildId and not SecondaryChildId)
        {
            throw new InvalidOperationException(
                $"{ChildEnvironmentVariable} must be exactly '{PrimaryChildId}' or '{SecondaryChildId}'.");
        }
        if (childId == SecondaryChildId && childCount != 2)
        {
            throw new InvalidOperationException(
                $"{SecondaryChildId} can start only when the Agent GUI marker childCount is 2.");
        }

        var childrenRoot = Path.Combine(root, "children");
        Directory.CreateDirectory(childrenRoot);
        RejectReparsePoint(childrenRoot, "Agent GUI children directory");
        var childRoot = Path.Combine(childrenRoot, childId);

        var projectsRootPath = fixtureIds.Contains(
            AgentGuiTestFixtures.SeparateChildProjectRootsId,
            StringComparer.Ordinal)
            ? Path.Combine(root, "projects-" + childId)
            : Path.Combine(root, "projects");

        var profile = new AgentGuiTestProfile(
            runId,
            childId,
            childCount,
            root,
            childRoot,
            projectsRootPath,
            Path.Combine(childRoot, "webview2"),
            Path.Combine(childRoot, "logs"),
            Path.Combine(childRoot, "profile"),
            Path.Combine(childRoot, "artifacts"),
            deadlineUtc,
            actionBudget,
            screenshotBudget,
            fixtureIds);

        foreach (var directory in profile.OwnedDirectories())
        {
            Directory.CreateDirectory(directory);
            RejectReparsePoint(directory, "Agent GUI owned directory");
        }

        // Process-scope only. Environment variables take precedence over any WebView2 registry policy,
        // so this run cannot inherit the normal JET UDF or a persistent remote-debugging port.
        Environment.SetEnvironmentVariable(
            "WEBVIEW2_USER_DATA_FOLDER",
            profile.WebViewUserDataFolder,
            EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable(
            "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
            "--remote-debugging-port=0",
            EnvironmentVariableTarget.Process);

        return profile;
    }

    internal string MutexName => $@"Local\JET.AgentGuiTest.{RunId}.{ChildId}";

    private IEnumerable<string> OwnedDirectories()
    {
        yield return ProjectsRootPath;
        yield return ChildRootPath;
        yield return WebViewUserDataFolder;
        yield return DiagnosticLogDirectory;
        yield return UserProfileDirectory;
        yield return ArtifactDirectory;
    }

    private static string ReadRequiredString(JsonElement marker, string propertyName)
    {
        if (!marker.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException($"Agent GUI marker {propertyName} is required.");
        }

        return value.GetString()!;
    }

    private static int ReadBudget(
        JsonElement marker,
        string propertyName,
        int maximum,
        bool allowZero = false)
    {
        if (!marker.TryGetProperty(propertyName, out var value)
            || !value.TryGetInt32(out var budget)
            || budget < (allowZero ? 0 : 1)
            || budget > maximum)
        {
            throw new InvalidOperationException(
                $"Agent GUI marker {propertyName} must be between {(allowZero ? 0 : 1)} and {maximum}.");
        }

        return budget;
    }

    private static IReadOnlyList<string> ReadFixtureIds(JsonElement marker)
    {
        if (!marker.TryGetProperty("fixtures", out var fixturesElement))
        {
            return [];
        }

        if (fixturesElement.ValueKind != JsonValueKind.Array
            || fixturesElement.GetArrayLength() > AgentGuiTestFixtures.MaximumFixtureCount)
        {
            throw new InvalidOperationException(
                $"Agent GUI marker fixtures must be an array of at most {AgentGuiTestFixtures.MaximumFixtureCount} items.");
        }

        var fixtureIds = new List<string>(fixturesElement.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fixtureElement in fixturesElement.EnumerateArray())
        {
            if (fixtureElement.ValueKind != JsonValueKind.String
                || fixtureElement.GetString() is not { } fixtureId
                || !AgentGuiTestFixtures.IsAllowedFixtureId(fixtureId))
            {
                throw new InvalidOperationException(
                    "Agent GUI marker fixtures contains an unknown fixture id.");
            }

            if (!seen.Add(fixtureId))
            {
                throw new InvalidOperationException(
                    $"Agent GUI marker fixtures contains duplicate id '{fixtureId}'.");
            }

            fixtureIds.Add(fixtureId);
        }

        return fixtureIds.ToArray();
    }

    private static int ReadChildCount(JsonElement marker)
    {
        if (!marker.TryGetProperty("childCount", out var value)
            || !value.TryGetInt32(out var childCount)
            || childCount is not 1 and not 2)
        {
            throw new InvalidOperationException(
                "Agent GUI marker childCount must be exactly 1 or 2.");
        }

        return childCount;
    }

    private static void RejectReparsePoint(string path, string label)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"{label} cannot be a reparse point: {path}");
        }
    }

    private static bool IsLowerHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';
}
#endif
