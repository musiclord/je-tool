using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

public sealed class SensitiveDataPolicyTests
{
    [Fact]
    public void SensitiveFixtureAndParityWorkspace_AreEffectivelyIgnored()
    {
        SensitiveDataPolicyGuard.ValidateEffectiveIgnoreRules(TestRepositoryPaths.RepositoryRoot);
    }

    [Fact]
    public void TrackedFiles_DoNotEnterSensitiveFixtureDirectory()
    {
        var trackedPaths = SensitiveDataPolicyGuard.ReadTrackedPaths(TestRepositoryPaths.RepositoryRoot);
        SensitiveDataPolicyGuard.ValidateNoSensitivePaths(TestRepositoryPaths.RepositoryRoot, trackedPaths);
    }

    [Fact]
    public void ProductionSourceCandidates_DoNotContainSampleSpecificLiterals()
    {
        var candidatePaths = SensitiveDataPolicyGuard.ReadSafetyCandidatePaths(TestRepositoryPaths.RepositoryRoot);
        SensitiveDataPolicyGuard.ValidateProductionSourceDoesNotContainSampleSpecificLiterals(
            TestRepositoryPaths.RepositoryRoot,
            candidatePaths);
    }

    [Fact]
    public void TestSourceCandidates_DoNotContainPrivateFixtureContracts()
    {
        var candidatePaths = SensitiveDataPolicyGuard.ReadSafetyCandidatePaths(TestRepositoryPaths.RepositoryRoot);
        SensitiveDataPolicyGuard.ValidateTestSourceDoesNotContainPrivateFixtureContracts(
            TestRepositoryPaths.RepositoryRoot,
            candidatePaths);
    }

    [Fact]
    public void TrackedAppearanceSnapshots_UseTheClosedValueBlindSchemaWhenPresent()
    {
        SpreadsheetAppearanceSnapshotSchemaGuard.ValidateRepositoryInventory(
            TestRepositoryPaths.RepositoryRoot);
    }

    [Fact]
    public void TrackedSensitivePath_IsRejectedWithActualPath()
    {
        using var directory = new TemporaryGuardDirectory();
        var relativePath = "data/temporary-test-case/case-A/tracked-guard-sentinel.txt";
        var fullPath = System.IO.Path.Combine(
            directory.Path,
            relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "synthetic fixture");
        SensitiveDataPolicyGuard.RunGitCommand(directory.Path, "init", "--quiet");
        SensitiveDataPolicyGuard.RunGitCommand(directory.Path, "add", "--", relativePath);

        var trackedPaths = SensitiveDataPolicyGuard.ReadTrackedPaths(directory.Path);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SensitiveDataPolicyGuard.ValidateNoSensitivePaths(directory.Path, trackedPaths));

        Assert.Contains(fullPath, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvidenceContainingSyntheticIdentifier_IsRejectedWithEvidencePathAndWithoutIdentifier()
    {
        using var directory = new TemporaryGuardDirectory();
        const string identifier = "synthetic-private-identifier";
        string[] identifiers = [identifier];
        var relativePath = "docs/specs/evidence/synthetic-snapshot.json";
        var fullPath = System.IO.Path.Combine(
            directory.Path,
            relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(new { entity = identifier }));
        var evidencePaths = SensitiveDataPolicyGuard.SelectEvidencePaths([relativePath]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SensitiveDataPolicyGuard.ValidateFilesDoNotContainIdentifiers(
                directory.Path,
                evidencePaths,
                identifiers));

        Assert.True(
            !exception.Message.Contains(identifier, StringComparison.Ordinal),
            "守衛失敗訊息不得回顧命中的機敏識別字。");
        Assert.Contains(fullPath, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TestSourceGuard_RejectsSyntheticPrivateFixtureContractWithoutEchoingMarker()
    {
        using var directory = new TemporaryGuardDirectory();
        var marker = "JET_" + "PBC_DIR";
        const string relativePath = "src/JET/tests/JET.Tests/SyntheticPrivateRunnerTests.cs";
        var fullPath = System.IO.Path.Combine(
            directory.Path,
            relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, $"internal static class SyntheticRunner {{ private const string Root = \"{marker}\"; }}");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SensitiveDataPolicyGuard.ValidateTestSourceDoesNotContainPrivateFixtureContracts(
                directory.Path,
                [relativePath]));

        Assert.DoesNotContain(marker, exception.Message, StringComparison.Ordinal);
        Assert.Contains(fullPath, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionSourceGuard_RejectsSyntheticSamplePopulationWithoutEchoingLiteral()
    {
        using var directory = new TemporaryGuardDirectory();
        const string relativePath = "src/JET/JET/SyntheticSource.cs";
        var fullPath = System.IO.Path.Combine(
            directory.Path,
            relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "/// 參考樣本(SyntheticEntity 123 列)的固定母體。");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SensitiveDataPolicyGuard.ValidateProductionSourceDoesNotContainSampleSpecificLiterals(
                directory.Path,
                [relativePath]));

        Assert.DoesNotContain("SyntheticEntity", exception.Message, StringComparison.Ordinal);
        Assert.Contains(fullPath, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionSourceCandidates_HandleUnstagedRenameWithoutMissingOrUnscannedFiles()
    {
        using var directory = new TemporaryGuardDirectory();
        const string oldRelativePath = "src/JET/JET/OldProgram.cs";
        const string newRelativePath = "src/JET/JET/NewProgram.cs";
        var oldFullPath = Path.Combine(
            directory.Path,
            oldRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var newFullPath = Path.Combine(
            directory.Path,
            newRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(oldFullPath)!);
        File.WriteAllText(oldFullPath, "internal static class OldProgram { }");
        SensitiveDataPolicyGuard.RunGitCommand(directory.Path, "init", "--quiet");
        SensitiveDataPolicyGuard.RunGitCommand(directory.Path, "add", "--", oldRelativePath);

        File.Delete(oldFullPath);
        File.WriteAllText(newFullPath, "internal static class NewProgram { }");

        var candidates = SensitiveDataPolicyGuard.ReadSafetyCandidatePaths(directory.Path);
        Assert.Contains(oldRelativePath, candidates);
        Assert.Contains(newRelativePath, candidates);

        SensitiveDataPolicyGuard.ValidateProductionSourceDoesNotContainSampleSpecificLiterals(
            directory.Path,
            candidates);
    }

    [Fact]
    public void SensitiveIgnoreRules_AreRejectedWhenARequiredRuleIsNotLast()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SensitiveDataPolicyGuard.ValidateIgnoreRuleOrder(
            [
                "/data/temporary-test-case/",
                "/data/legacy-parity-work/",
                "/data/test-case/",
                "!/data/temporary-test-case/case-A/",
            ]));

        Assert.Contains("最後三條有效規則", exception.Message, StringComparison.Ordinal);
    }

    private sealed class TemporaryGuardDirectory : IDisposable
    {
        public TemporaryGuardDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"jet-sensitive-guard-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }
    }
}

internal static class SensitiveDataPolicyGuard
{
    private const string SensitiveRoot = "data/temporary-test-case";
    private const string ParityWorkspaceRoot = "data/legacy-parity-work";
    private const string PrivateCaseRoot = "data/test-case";
    private static readonly string[] SensitiveRoots =
    [
        SensitiveRoot,
        ParityWorkspaceRoot,
        PrivateCaseRoot,
    ];
    private static readonly string[] ProductionSourceSensitiveMarkers =
    [
        "case-A",
        "case-B",
        "data/temporary-test-case",
        "data\\temporary-test-case",
        "data/legacy-parity-work",
        "data\\legacy-parity-work",
        "data/test-case",
        "data\\test-case",
    ];
    private static readonly string[] TestSourcePrivateFixtureMarkers =
    [
        "JET_" + "PBC_DIR",
        "JET_" + "LEGACY_PARITY_SAMPLE_ROOT",
        "JET_" + "PRIVATE_PARITY_ENABLED",
        "[" + "PbcFact]",
        "[" + "SqlServerPbcFact]",
        "[" + "LegacyParityFact]",
        "[" + "SqlServerLegacyParityFact]",
        "test-" + "je.xlsx",
        "test-" + "tb.csv",
    ];
    private static readonly Regex SampleSpecificRowCountPattern = new(
        @"樣本\s*[\(（][^\)）\r\n]{0,160}\d[\d,]*\s*列",
        RegexOptions.CultureInvariant);

    internal static void ValidateEffectiveIgnoreRules(string repositoryRoot)
    {
        var ignorePath = Path.Combine(repositoryRoot, ".gitignore");
        var lines = File.ReadAllLines(ignorePath);
        ValidateIgnoreRuleOrder(lines);
        var rules = lines.Select(line => line.Trim()).ToHashSet(StringComparer.Ordinal);

        if (!rules.Contains("/data/temporary-test-case/")
            || !rules.Contains("/data/legacy-parity-work/")
            || !rules.Contains("/data/test-case/")
            || rules.Contains("**/legacy-parity-work/")
            || rules.Contains("**/test-case/"))
        {
            throw new InvalidOperationException(
                ".gitignore 必須以儲存庫根目錄規則排除私人案件與 parity 工作區。");
        }

        AssertIgnored(repositoryRoot, $"{SensitiveRoot}/.sensitive-guard-probe");
        AssertIgnored(repositoryRoot, $"{ParityWorkspaceRoot}/.parity-workspace-guard-probe");
        AssertIgnored(repositoryRoot, $"{PrivateCaseRoot}/.private-case-guard-probe");
    }

    internal static void ValidateIgnoreRuleOrder(IEnumerable<string> lines)
    {
        var effectiveRules = lines
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
        var requiredTail = new[]
        {
            "/data/temporary-test-case/",
            "/data/legacy-parity-work/",
            "/data/test-case/",
        };

        if (effectiveRules.Length < requiredTail.Length
            || !effectiveRules[^requiredTail.Length..].SequenceEqual(requiredTail, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "私人案件與 parity 工作區必須維持為 .gitignore 最後三條有效規則。");
        }
    }

    internal static IReadOnlyList<string> ReadTrackedPaths(string repositoryRoot)
    {
        var result = RunGit(
            repositoryRoot,
            "ls-files",
            "--cached",
            "-z",
            "--");

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("無法列舉 Git 追蹤檔案。");
        }

        return result.StandardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<string> ReadSafetyCandidatePaths(string repositoryRoot)
    {
        var tracked = ReadTrackedPaths(repositoryRoot);
        var untrackedResult = RunGit(
            repositoryRoot,
            "ls-files",
            "--others",
            "--exclude-standard",
            "-z",
            "--",
            "src/JET/JET",
            "src/JET/tests/JET.Tests",
            "docs");
        if (untrackedResult.ExitCode != 0)
        {
            throw new InvalidOperationException("無法列舉產品、測試與文件範圍內的未追蹤安全候選檔案。");
        }

        return tracked
            .Concat(untrackedResult.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    internal static void ValidateNoSensitivePaths(
        string repositoryRoot,
        IEnumerable<string> repositoryRelativePaths)
    {
        foreach (var relativePath in repositoryRelativePaths)
        {
            var normalized = NormalizeGitPath(relativePath);
            var sensitiveRoot = SensitiveRoots.FirstOrDefault(root =>
                normalized.Equals(root, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));
            if (sensitiveRoot is not null)
            {
                var safeViolationPath = FormatSensitiveViolationPath(repositoryRoot, normalized, sensitiveRoot);
                throw new InvalidOperationException(
                    $"私人資料路徑不得由 Git 追蹤：{safeViolationPath}");
            }
        }
    }

    internal static void ValidateProductionSourceDoesNotContainSampleSpecificLiterals(
        string repositoryRoot,
        IEnumerable<string> repositoryRelativePaths)
    {
        var productionSources = repositoryRelativePaths
            .Select(NormalizeGitPath)
            .Where(path => path.StartsWith("src/JET/JET/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (productionSources.Length == 0)
        {
            throw new InvalidOperationException("未找到可掃描的 JET production C# source。");
        }

        foreach (var relativePath in productionSources)
        {
            var fullPath = ResolveContainedPath(repositoryRoot, relativePath);
            string content;
            try
            {
                content = File.ReadAllText(fullPath);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            if (ProductionSourceSensitiveMarkers.Any(marker =>
                    content.Contains(marker, StringComparison.OrdinalIgnoreCase))
                || SampleSpecificRowCountPattern.IsMatch(content))
            {
                throw new InvalidOperationException(
                    $"JET production source 含樣本專屬路徑、代號或固定筆數：{fullPath}");
            }
        }
    }

    internal static void ValidateTestSourceDoesNotContainPrivateFixtureContracts(
        string repositoryRoot,
        IEnumerable<string> repositoryRelativePaths)
    {
        var testSources = repositoryRelativePaths
            .Select(NormalizeGitPath)
            .Where(path => path.StartsWith("src/JET/tests/JET.Tests/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(
                    "Architecture/SensitiveDataPolicyTests.cs",
                    StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (testSources.Length == 0)
        {
            throw new InvalidOperationException("未找到可掃描的 JET test C# source。");
        }

        foreach (var relativePath in testSources)
        {
            var fullPath = ResolveContainedPath(repositoryRoot, relativePath);
            string content;
            try
            {
                content = File.ReadAllText(fullPath);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            if (TestSourcePrivateFixtureMarkers.Any(marker =>
                    content.Contains(marker, StringComparison.OrdinalIgnoreCase))
                || SampleSpecificRowCountPattern.IsMatch(content))
            {
                throw new InvalidOperationException(
                    $"JET test source 仍含舊私有 fixture 執行契約或樣本固定筆數：{fullPath}");
            }
        }
    }

    private static string FormatSensitiveViolationPath(
        string repositoryRoot,
        string normalizedPath,
        string sensitiveRoot)
    {
        var root = Path.GetFullPath(Path.Combine(repositoryRoot, sensitiveRoot));
        if (!sensitiveRoot.Equals(SensitiveRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(root, "<redacted>");
        }

        var suffix = normalizedPath.Length > sensitiveRoot.Length
            ? normalizedPath[(sensitiveRoot.Length + 1)..]
            : string.Empty;
        var segments = suffix.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var safeSegments = segments.Select(segment => segment switch
        {
            "case-A" or "case-B" or "tracked-guard-sentinel.txt" => segment,
            _ => "<redacted>",
        });

        return safeSegments.Any()
            ? Path.Combine(new[] { root }.Concat(safeSegments).ToArray())
            : root;
    }

    internal static IReadOnlyList<string> SelectEvidencePaths(IEnumerable<string> repositoryRelativePaths) =>
        repositoryRelativePaths
            .Select(NormalizeGitPath)
            .Where(path => path.StartsWith("docs/specs/evidence/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("src/JET/tests/JET.Tests/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    internal static void ValidateFilesDoNotContainIdentifiers(
        string repositoryRoot,
        IEnumerable<string> repositoryRelativePaths,
        IEnumerable<string> sensitiveIdentifiers)
    {
        var identifiers = sensitiveIdentifiers
            .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (identifiers.Length == 0)
        {
            return;
        }

        var patterns = BuildPatterns(identifiers);
        foreach (var relativePath in repositoryRelativePaths)
        {
            var normalized = NormalizeGitPath(relativePath);
            var fullPath = ResolveContainedPath(repositoryRoot, normalized);
            var safePath = RedactPathIfNeeded(fullPath, identifiers);

            if (identifiers.Any(identifier =>
                normalized.Contains(identifier, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Git 候選路徑含有機敏 fixture 識別字：{safePath}");
            }

            if (!File.Exists(fullPath))
            {
                continue;
            }

            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Git 候選檔案是 reparse point，無法安全掃描：{safePath}");
            }

            if (ContainsAnyPattern(fullPath, patterns)
                || IsJsonPath(normalized) && JsonContainsIdentifier(fullPath, identifiers))
            {
                throw new InvalidOperationException(
                    $"Git 候選檔案內容含有機敏 fixture 識別字：{safePath}");
            }
        }
    }

    private static void AssertIgnored(string repositoryRoot, string relativeProbePath)
    {
        var result = RunGit(
            repositoryRoot,
            "check-ignore",
            "--quiet",
            "--no-index",
            "--",
            relativeProbePath);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git ignore 規則未對預期路徑生效：{relativeProbePath}");
        }
    }

    private static string NormalizeGitPath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string ResolveContainedPath(string repositoryRoot, string relativePath)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var fullPath = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Git 回報了 repository 以外的候選路徑。");
        }

        return fullPath;
    }

    private static string RedactPathIfNeeded(string fullPath, IReadOnlyList<string> identifiers) =>
        identifiers.Any(identifier => fullPath.Contains(identifier, StringComparison.OrdinalIgnoreCase))
            ? "<repository path redacted because it contains a sensitive identifier>"
            : fullPath;

    private static IReadOnlyList<byte[]> BuildPatterns(IEnumerable<string> identifiers) =>
        identifiers
            .SelectMany(identifier => new[]
            {
                identifier,
                identifier.ToUpperInvariant(),
                identifier.ToLowerInvariant(),
            })
            .Distinct(StringComparer.Ordinal)
            .SelectMany(identifier => new[]
            {
                Encoding.UTF8.GetBytes(identifier),
                Encoding.Unicode.GetBytes(identifier),
                Encoding.BigEndianUnicode.GetBytes(identifier),
            })
            .Where(pattern => pattern.Length > 0)
            .GroupBy(Convert.ToBase64String, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

    private static bool ContainsAnyPattern(string path, IReadOnlyList<byte[]> patterns)
    {
        var maximumPatternLength = patterns.Max(pattern => pattern.Length);
        var buffer = new byte[64 * 1024 + maximumPatternLength];
        var carry = 0;

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);

        while (true)
        {
            var read = stream.Read(buffer, carry, 64 * 1024);
            var length = carry + read;
            var span = buffer.AsSpan(0, length);
            foreach (var pattern in patterns)
            {
                if (span.IndexOf(pattern) >= 0)
                {
                    return true;
                }
            }

            if (read == 0)
            {
                return false;
            }

            carry = Math.Min(maximumPatternLength - 1, length);
            span[(length - carry)..].CopyTo(buffer);
        }
    }

    private static bool IsJsonPath(string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static bool JsonContainsIdentifier(string path, IReadOnlyList<string> identifiers)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
            return JsonElementContainsIdentifier(document.RootElement, identifiers);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool JsonElementContainsIdentifier(JsonElement element, IReadOnlyList<string> identifiers)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (identifiers.Any(identifier =>
                            property.Name.Contains(identifier, StringComparison.OrdinalIgnoreCase))
                        || JsonElementContainsIdentifier(property.Value, identifiers))
                    {
                        return true;
                    }
                }
                return false;

            case JsonValueKind.Array:
                return element.EnumerateArray().Any(item =>
                    JsonElementContainsIdentifier(item, identifiers));

            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                return identifiers.Any(identifier =>
                    value.Contains(identifier, StringComparison.OrdinalIgnoreCase));

            default:
                return false;
        }
    }

    internal static void RunGitCommand(string repositoryRoot, params string[] arguments)
    {
        var result = RunGit(repositoryRoot, arguments);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("Synthetic Git setup failed.");
        }
    }

    private static GitResult RunGit(string repositoryRoot, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return new GitResult(
            process.ExitCode,
            output.GetAwaiter().GetResult(),
            error.GetAwaiter().GetResult());
    }

    private sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);
}
