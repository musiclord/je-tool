using System.Text.RegularExpressions;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 方案資料夾（src/JET）會單獨交出去，所以裡面的程式、測試與設定不能提到只有開發儲存庫才有的東西：
/// Agent 的指引檔、驗證框架的資料夾與命令、用真實案件驗收與和舊版輸出比對的機制，以及只在開發儲存庫執行的測試。
/// 這個檢查放在驗證框架底下，被擋的字樣才不會反過來寫進方案資料夾。
/// </summary>
public sealed class SolutionFolderSelfContainedTests
{
    private static readonly string[] Extensions =
    [
        ".cs", ".js", ".cjs", ".css", ".html", ".csproj", ".json",
        ".editorconfig", ".props", ".targets", ".pubxml", ".slnx",
    ];

    // 整行都看：註解、字串與識別字都算。大小寫要相符，
    // 所以一般測試裡叫 repositoryRoot 的參數不算，TestRepositoryPaths.SolutionRoot 也不算。
    private static readonly (string Name, Regex Pattern)[] DevelopmentRepositoryOnly =
    [
        ("Agent 的指引檔", new Regex(@"AGENTS\.md", RegexOptions.CultureInvariant)),
        ("Gui 測試夾具的資料夾與名稱", new Regex("agent-gui-test|AgentGui", RegexOptions.CultureInvariant)),
        ("開發儲存庫的根目錄", new Regex(@"(?<![A-Za-z])RepositoryRoot(?![A-Za-z])", RegexOptions.CultureInvariant)),
        ("用真實案件驗收的機制", new Regex("PrivateCase|JET_PRIVATE_CASE", RegexOptions.CultureInvariant)),
        ("和舊版輸出比對的機制", new Regex(
            "LegacyAuditParity|LegacyParity|legacy-parity-work|JET_LEGACY_PARITY", RegexOptions.CultureInvariant)),
        ("只在開發儲存庫執行的測試標記", new Regex("DevelopmentRepository", RegexOptions.CultureInvariant)),
        ("驗證框架的命令與資料夾", new Regex(
            @"verify\.ps1|tools[/\\](?:harness|tests)|""tools""|development-repository-tests", RegexOptions.CultureInvariant)),
        ("放在驗證框架底下的類別", new Regex(
            @"GuiScenarioCoverageTests|SensitiveDataPolicy|GuiWorkingPaperInspection|Jet\.GuiDriver", RegexOptions.CultureInvariant)),
    ];

    [Fact]
    public void SolutionFolder_DoesNotMentionWhatOnlyTheDevelopmentRepositoryHas()
    {
        var root = TestRepositoryPaths.SolutionRoot;
        var findings = new List<string>();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                     .Where(path => !Path.GetRelativePath(root, path)
                         .Split(Path.DirectorySeparatorChar)
                         .Any(part => part is "bin" or "obj"))
                     .Order(StringComparer.Ordinal))
        {
            scanned++;
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                foreach (var (name, pattern) in DevelopmentRepositoryOnly)
                {
                    if (pattern.IsMatch(lines[index]))
                    {
                        findings.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{index + 1}: {name}");
                    }
                }
            }
        }

        Assert.True(scanned > 500, $"只掃到 {scanned} 個檔，方案資料夾的位置可能不對。");
        Assert.True(findings.Count == 0,
            "方案資料夾提到了只有開發儲存庫才有的東西（共 " + findings.Count + " 處，列出前 40 處）："
            + Environment.NewLine + string.Join(Environment.NewLine, findings.Take(40)));
    }

    [Theory]
    [InlineData("var root = TestRepositoryPaths.RepositoryRoot;", true)]
    [InlineData("&& !(File.Exists(Path.Combine(directory.FullName, \"AGENTS.md\"))", true)]
    [InlineData("TestRepositoryPaths.RepositoryRoot, \"tools\", \"harness\", \"agent-gui-test\", \"AgentGuiTestFixtures.cs\"));", true)]
    [InlineData("[Trait(TestProfileTraits.Key, \"PrivateCase\")]", true)]
    [InlineData("[Trait(TestProfileTraits.Key, TestProfileTraits.DevelopmentRepository)]", true)]
    [InlineData("internal sealed record LegacyAuditParityRunSelection(", true)]
    [InlineData("public const string RelativeRoot = \"data/legacy-parity-work\";", true)]
    [InlineData("while (root is not null && !File.Exists(Path.Combine(root.FullName, \"tools\", \"verify.ps1\"))) root = root.Parent;", true)]
    [InlineData("<Compile Include=\"..\\..\\..\\..\\tools\\harness\\gui-driver\\GuiWorkingPaperInspection.cs\" />", true)]
    [InlineData("// 測試名稱由 GuiScenarioCoverageTests 核對，改名時一起改。", true)]
    [InlineData("var root = TestRepositoryPaths.SolutionRoot;", false)]
    [InlineData("internal static void ValidateRepositoryInventory(string repositoryRoot)", false)]
    [InlineData("internal enum LegacyReportKind", false)]
    [InlineData("public sealed class LegacyWorkbookHeaderCatalogTests", false)]
    [InlineData("namespace Jet.Harness.TaiwanSme;", false)]
    [InlineData("// project.update 的回應不帶 artifacts，前端也不讀它。", false)]
    public void PatternsRecogniseDevelopmentRepositoryNamesAndLeaveOrdinaryCodeAlone(string line, bool expected)
    {
        Assert.Equal(expected, DevelopmentRepositoryOnly.Any(item => item.Pattern.IsMatch(line)));
    }
}
