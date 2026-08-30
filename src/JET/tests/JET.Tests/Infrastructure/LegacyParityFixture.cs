using JET.Tests.TestInfrastructure;

namespace JET.Tests.Infrastructure;

internal enum LegacyParityCase
{
    CaseA,
    CaseB,
}

internal sealed class LegacyParityCaseFixture
{
    internal LegacyParityCaseFixture(LegacyParityCase @case, string directoryPath)
    {
        Case = @case;
        DirectoryPath = directoryPath;
    }

    public LegacyParityCase Case { get; }

    internal string DirectoryPath { get; }

    public string Alias => Case == LegacyParityCase.CaseA ? "case-A" : "case-B";

    internal string ResolveFile(string relativePath) =>
        ResolveFile(relativePath, File.GetAttributes);

    internal string ResolveFile(
        string relativePath,
        Func<string, FileAttributes> readAttributes)
    {
        ArgumentNullException.ThrowIfNull(readAttributes);

        var candidate = CombineContained(DirectoryPath, relativePath);
        FileAttributes candidateAttributes;
        try
        {
            candidateAttributes = readAttributes(candidate);
        }
        catch (Exception exception) when (exception is IOException
            or NotSupportedException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            throw new InvalidOperationException(
                "Legacy parity case file is unavailable; resolution was refused.");
        }

        if ((candidateAttributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidOperationException(
                "Legacy parity case-file resolution requires a regular file.");
        }

        EnsureNoReparsePoints(DirectoryPath, candidate, readAttributes, candidateAttributes);
        return candidate;
    }

    internal bool ContainsFile(string relativePath)
    {
        var candidate = CombineContained(DirectoryPath, relativePath);
        if (!File.Exists(candidate))
        {
            return false;
        }

        ResolveFile(relativePath);
        return true;
    }

    private static string CombineContained(string rootPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException(
                "Fixture requirement must be a non-empty relative path.",
                nameof(relativePath));
        }

        var root = Path.GetFullPath(rootPath);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Fixture requirement must remain inside its case directory.",
                nameof(relativePath));
        }

        return candidate;
    }

    private static void EnsureNoReparsePoints(
        string rootPath,
        string candidatePath,
        Func<string, FileAttributes> readAttributes,
        FileAttributes candidateAttributes)
    {
        if ((candidateAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "Legacy parity case-file path contains a reparse point; resolution was refused.");
        }

        var root = Path.GetFullPath(rootPath);
        for (var current = Directory.GetParent(candidatePath); current is not null; current = current.Parent)
        {
            FileAttributes attributes;
            try
            {
                attributes = readAttributes(current.FullName);
            }
            catch (Exception exception) when (exception is IOException
                or NotSupportedException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
            {
                throw new InvalidOperationException(
                    "Legacy parity case-file parent is unavailable; resolution was refused.");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "Legacy parity case-file path contains a reparse point; resolution was refused.");
            }

            if (string.Equals(current.FullName, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "Legacy parity case-file containment root was not reachable.");
    }

    public override string ToString() => Alias;
}

internal sealed class LegacyParityFixtureSet
{
    private readonly LegacyParityCaseFixture[] _cases;

    internal LegacyParityFixtureSet(string rootPath, LegacyParityCaseFixture[] cases)
    {
        RootPath = rootPath;
        _cases = cases;
    }

    internal string RootPath { get; }

    public IReadOnlyList<LegacyParityCaseFixture> Cases => _cases;

    internal LegacyParityCaseFixture GetCase(LegacyParityCase @case)
    {
        var matches = _cases.Where(fixture => fixture.Case == @case).Take(2).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException(
                "Legacy parity fixture set does not contain exactly one requested case.");
    }

    internal string? FindUniqueCaseDirectory(IReadOnlyList<string> requiredRelativePaths)
        => FindUniqueCaseFixture(requiredRelativePaths)?.DirectoryPath;

    internal LegacyParityCaseFixture? FindUniqueCaseFixture(IReadOnlyList<string> requiredRelativePaths)
    {
        if (requiredRelativePaths.Count == 0)
        {
            return null;
        }

        var matches = _cases
            .Where(fixture => requiredRelativePaths.All(fixture.ContainsFile))
            .Take(2)
            .ToArray();

        return matches.Length == 1 ? matches[0] : null;
    }

    public override string ToString() => "legacy parity fixtures (case-A, case-B)";
}

internal static class TestRepositoryPaths
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !(File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(directory.FullName, "src", "JET", "tests", "JET.Tests"))))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET repository root。");
    }
}

internal sealed class LegacyParityWorkspace : IDisposable
{
    public const string RelativeRoot = "data/legacy-parity-work";

    public const string KeepOutputsEnvironmentVariableName = "JET_LEGACY_PARITY_KEEP_OUTPUTS";

    private readonly string _workspaceRoot;
    private readonly string? _temporaryContainmentRoot;
    private readonly bool _keepOutputs;
    private bool _disposed;

    private LegacyParityWorkspace(
        string workspaceRoot,
        string path,
        bool keepOutputs,
        string? temporaryContainmentRoot)
    {
        _workspaceRoot = workspaceRoot;
        _temporaryContainmentRoot = temporaryContainmentRoot;
        _keepOutputs = keepOutputs;
        Path = path;
    }

    public string Path { get; }

    public bool KeepsOutputs => _keepOutputs;

    internal static LegacyParityWorkspace CreateTemporary(
        LegacyParityCase @case,
        string runId)
    {
        var temporaryRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"jet-legacy-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            return CreateCore(
                temporaryRoot,
                @case,
                runId,
                keepOutputs: false,
                temporaryContainmentRoot: temporaryRoot);
        }
        catch
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
            throw;
        }
    }

    internal static LegacyParityWorkspace Create(
        string repositoryRoot,
        LegacyParityCase @case,
        string runId) =>
        Create(repositoryRoot, @case, runId, keepOutputs: false);

    internal static LegacyParityWorkspace Create(
        string repositoryRoot,
        LegacyParityCase @case,
        string runId,
        bool keepOutputs) =>
        CreateCore(
            repositoryRoot,
            @case,
            runId,
            keepOutputs,
            temporaryContainmentRoot: null);

    private static LegacyParityWorkspace CreateCore(
        string repositoryRoot,
        LegacyParityCase @case,
        string runId,
        bool keepOutputs,
        string? temporaryContainmentRoot)
    {
        if (string.IsNullOrWhiteSpace(runId)
            || runId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("Parity workspace run id must be a safe path segment.", nameof(runId));
        }

        var repository = System.IO.Path.GetFullPath(repositoryRoot);
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(repository, RelativeRoot));
        var alias = @case == LegacyParityCase.CaseA ? "case-A" : "case-B";
        var ownedPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, $"{alias}-{runId}"));
        EnsureContained(repository, root);
        EnsureContained(root, ownedPath);
        EnsureNoReparsePoints(repository, root);
        Directory.CreateDirectory(ownedPath);
        EnsureNoReparsePoints(repository, ownedPath);
        return new LegacyParityWorkspace(
            root,
            ownedPath,
            keepOutputs,
            temporaryContainmentRoot);
    }

    internal static bool ParseKeepOutputs(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue)
            || string.Equals(configuredValue, "0", StringComparison.Ordinal)
            || string.Equals(configuredValue, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(configuredValue, "1", StringComparison.Ordinal)
            || string.Equals(configuredValue, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        throw new ArgumentException(
            $"{KeepOutputsEnvironmentVariableName} must be 1/true or 0/false.",
            nameof(configuredValue));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_keepOutputs)
        {
            _disposed = true;
            return;
        }

        if (_temporaryContainmentRoot is not null)
        {
            var systemTemporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            EnsureContained(systemTemporaryRoot, _temporaryContainmentRoot);
            if (!System.IO.Path.GetFileName(_temporaryContainmentRoot).StartsWith(
                    "jet-legacy-workspace-",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Temporary parity workspace cleanup target was not recognized.");
            }
            EnsureNoReparsePoints(systemTemporaryRoot, _temporaryContainmentRoot);
            if (Directory.Exists(_temporaryContainmentRoot))
            {
                SqliteTestPool.ClearAllUnder(_temporaryContainmentRoot);
                Directory.Delete(_temporaryContainmentRoot, recursive: true);
            }
        }
        else
        {
            EnsureContained(_workspaceRoot, Path);
            EnsureNoReparsePoints(_workspaceRoot, Path);
            if (Directory.Exists(Path))
            {
                SqliteTestPool.ClearAllUnder(Path);
                Directory.Delete(Path, recursive: true);
            }
        }

        _disposed = true;
    }

    private static void EnsureContained(string rootPath, string candidatePath)
    {
        var root = System.IO.Path.GetFullPath(rootPath);
        var candidate = System.IO.Path.GetFullPath(candidatePath);
        var prefix = root.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? root
            : root + System.IO.Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Parity workspace cleanup target escaped the ignored workspace root.");
        }
    }

    private static void EnsureNoReparsePoints(string containmentRoot, string candidatePath)
    {
        var root = System.IO.Path.TrimEndingDirectorySeparator(
            System.IO.Path.GetFullPath(containmentRoot));
        var candidate = System.IO.Path.GetFullPath(candidatePath);
        var prefix = root.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? root
            : root + System.IO.Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Parity workspace path escaped its containment root.");
        }

        for (var current = new DirectoryInfo(candidate); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "Parity workspace path contains a reparse point; cleanup was refused.");
            }

            if (string.Equals(current.FullName, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new InvalidOperationException("Parity workspace containment root was not reachable.");
    }

}
