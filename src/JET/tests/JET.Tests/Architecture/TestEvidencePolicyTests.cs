using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

public sealed class TestEvidencePolicyTests
{
    [Fact]
    public void UngatedTests_DoNotTurnMissingEnvironmentIntoPassingEarlyReturn()
    {
        TestEvidencePolicyGuard.ValidateTree(Path.Combine(RepoRoot(), "src", "JET", "tests", "JET.Tests"));
    }

    [Fact]
    public void EnvironmentEarlyReturn_FailsWithActualSourcePath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"jet-test-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "FalseGreenTests.cs");
        File.WriteAllText(
            sourcePath,
            """
            using Xunit;

            public sealed class FalseGreenTests
            {
                [Fact]
                public void PrivateFixture_Missing_ReturnsBeforeAssertion()
                {
                    if (!File.Exists("private-fixture.xlsx"))
                    {
                        return;
                    }

                    Assert.True(true);
                }
            }
            """);

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => TestEvidencePolicyGuard.ValidateTree(root));

            Assert.Contains(sourcePath, exception.Message, StringComparison.Ordinal);
            Assert.Contains("PrivateFixture_Missing_ReturnsBeforeAssertion", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProviderTests_KeepTheirCapabilityContracts()
    {
        var testsRoot = Path.Combine(RepoRoot(), "src", "JET", "tests", "JET.Tests", "Infrastructure");
        var logging = File.ReadAllText(Path.Combine(testsRoot, "ProviderLoggingParityTests.cs"));

        Assert.Matches(
            new Regex(@"\[Fact\]\s+public async Task ImportReplace_Sqlite_", RegexOptions.CultureInvariant),
            logging);
        Assert.Matches(
            new Regex(@"\[Fact\]\s+public async Task GlProjection_Sqlite_", RegexOptions.CultureInvariant),
            logging);
        Assert.Matches(
            new Regex(@"\[Fact\]\s+public async Task ImportReplace_DuckDb_", RegexOptions.CultureInvariant),
            logging);
        Assert.Matches(
            new Regex(@"\[Fact\]\s+public async Task GlProjection_DuckDb_", RegexOptions.CultureInvariant),
            logging);
        Assert.Matches(
            new Regex(@"\[SqlServerFact\]\s+public async Task ImportReplace_SqlServer_", RegexOptions.CultureInvariant),
            logging);
        Assert.Matches(
            new Regex(@"\[SqlServerFact\]\s+public async Task GlProjection_SqlServer_", RegexOptions.CultureInvariant),
            logging);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !(File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(directory.FullName, "src", "JET", "tests", "JET.Tests"))))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到含 AGENTS.md 與 JET.Tests 的 repository root。");
    }
}

internal static partial class TestEvidencePolicyGuard
{
    private static readonly Regex TestMethodStart = TestMethodStartRegex();
    private static readonly Regex EnvironmentProbe = EnvironmentProbeRegex();

    public static void ValidateTree(string testsRoot)
    {
        var violations = Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(FindViolations)
            .ToArray();

        if (violations.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "非 availability-gated 的 Fact/Theory 不得在任何斷言前以環境探測 early return 偽裝通過："
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    private static IEnumerable<string> FindViolations(string sourcePath)
    {
        var source = File.ReadAllText(sourcePath);
        var code = MaskCommentsAndLiterals(source);

        foreach (Match match in TestMethodStart.Matches(code))
        {
            var openBrace = code.IndexOf('{', match.Index + match.Length - 1);
            var closeBrace = FindMatchingBrace(code, openBrace);
            var body = code[(openBrace + 1)..closeBrace];
            var firstReturn = body.IndexOf("return;", StringComparison.Ordinal);
            if (firstReturn < 0)
            {
                continue;
            }

            var firstAssert = body.IndexOf("Assert.", StringComparison.Ordinal);
            if (firstAssert >= 0 && firstAssert < firstReturn)
            {
                continue;
            }

            var prefixThroughReturn = body[..(firstReturn + "return;".Length)];
            if (!EnvironmentProbe.IsMatch(prefixThroughReturn))
            {
                continue;
            }

            var methodName = match.Groups["method"].Value;
            var returnIndex = openBrace + 1 + firstReturn;
            var lineNumber = 1 + source.AsSpan(0, returnIndex).Count('\n');
            yield return $"{sourcePath}:{lineNumber} ({methodName})";
        }
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static int FindMatchingBrace(string code, int openBrace)
    {
        var depth = 0;
        for (var index = openBrace; index < code.Length; index++)
        {
            if (code[index] == '{')
            {
                depth++;
            }
            else if (code[index] == '}' && --depth == 0)
            {
                return index;
            }
        }

        throw new InvalidOperationException("測試原始碼的方法大括號未閉合。");
    }

    private static string MaskCommentsAndLiterals(string source)
    {
        var masked = new StringBuilder(source);
        var index = 0;

        while (index < source.Length)
        {
            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                var end = source.IndexOf('\n', index + 2);
                Mask(masked, source, index, end < 0 ? source.Length : end);
                index = end < 0 ? source.Length : end;
                continue;
            }

            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                end = end < 0 ? source.Length : end + 2;
                Mask(masked, source, index, end);
                index = end;
                continue;
            }

            if (source[index] == '\'')
            {
                var end = ConsumeQuoted(source, index, '\'', verbatim: false);
                Mask(masked, source, index, end);
                index = end;
                continue;
            }

            if (source[index] == '"')
            {
                var quoteCount = CountRun(source, index, '"');
                var end = quoteCount >= 3
                    ? ConsumeRawString(source, index, quoteCount)
                    : ConsumeQuoted(source, index, '"', IsVerbatimString(source, index));
                Mask(masked, source, index, end);
                index = end;
                continue;
            }

            index++;
        }

        return masked.ToString();
    }

    private static int ConsumeQuoted(string source, int start, char delimiter, bool verbatim)
    {
        for (var index = start + 1; index < source.Length; index++)
        {
            if (source[index] != delimiter)
            {
                if (!verbatim && source[index] == '\\')
                {
                    index++;
                }
                continue;
            }

            if (verbatim && index + 1 < source.Length && source[index + 1] == delimiter)
            {
                index++;
                continue;
            }

            return index + 1;
        }

        return source.Length;
    }

    private static int ConsumeRawString(string source, int start, int quoteCount)
    {
        var delimiter = new string('"', quoteCount);
        var end = source.IndexOf(delimiter, start + quoteCount, StringComparison.Ordinal);
        return end < 0 ? source.Length : end + quoteCount;
    }

    private static bool IsVerbatimString(string source, int quoteIndex) =>
        quoteIndex > 0 && source[quoteIndex - 1] == '@'
        || quoteIndex > 1 && source[quoteIndex - 2] == '@' && source[quoteIndex - 1] == '$';

    private static int CountRun(string source, int start, char character)
    {
        var index = start;
        while (index < source.Length && source[index] == character)
        {
            index++;
        }
        return index - start;
    }

    private static void Mask(StringBuilder masked, string source, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (source[index] != '\r' && source[index] != '\n')
            {
                masked[index] = ' ';
            }
        }
    }

    [GeneratedRegex(
        @"(?ms)^\s*\[(?:Fact|Theory)(?:Attribute)?(?:\([^\r\n]*\))?\]\s*"
        + @"(?:\[[^\r\n]+\]\s*)*"
        + @"(?:public|internal|private|protected)\s+(?:static\s+)?(?:async\s+)?[^\r\n{;=]+?\s+"
        + @"(?<method>[A-Za-z_][A-Za-z0-9_]*)\s*\([^;{}]*\)\s*\{",
        RegexOptions.CultureInvariant)]
    private static partial Regex TestMethodStartRegex();

    [GeneratedRegex(
        @"(?:Environment\s*\.\s*GetEnvironmentVariable|File\s*\.\s*Exists|Directory\s*\.\s*Exists|"
        + @"\b(?:Find|Probe|TryCreate)[A-Za-z0-9_]*(?:Directory|Path|Connection|Async)?\s*\(|"
        + @"\b[A-Za-z0-9_]*Availability\b)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentProbeRegex();
}
