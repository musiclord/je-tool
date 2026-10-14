using System.Text.RegularExpressions;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 科目分類編輯器的前端守衛。身分、semantic role 與分類能否刪除的最終裁定都在後端；
/// 本組只鎖內建分類與 role 封閉選單的鏡像。內建保護、revision 樂觀鎖與儲存後的失效範圍，
/// 2026-10-14 起改由 <c>tools/tests/frontend-journey-step4.test.cjs</c> 核對；後端失效政策由 <c>Domain/AuditDependencyPolicyTests</c> 核對。
/// </summary>
public sealed class AccountTaxonomyFrontendTests
{
    [Fact]
    public void BuiltInCategories_MirrorDomainIdentityAndRole()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var builtIns = ExtractBuiltIns(core);

        Assert.Equal(AccountTaxonomyBuiltIns.All.Count, builtIns.Count);
        foreach (var expected in AccountTaxonomyBuiltIns.All)
        {
            Assert.True(builtIns.TryGetValue(expected.CategoryId, out var actual),
                $"ui-core.js 缺少內建分類 {expected.CategoryId}");
            Assert.Equal(expected.Label, actual.Label);
            Assert.Equal(expected.SemanticRole, actual.SemanticRole);
            Assert.Equal(expected.Ordinal, actual.Ordinal);
        }
    }

    [Fact]
    public void SemanticRoleOptions_CoverEveryBuiltInRoleExactly()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var roles = ExtractValueLabelMap(core, "ACCOUNT_TAXONOMY_ROLES");

        Assert.Equal(
            AccountTaxonomyBuiltIns.All.Select(item => item.SemanticRole).OrderBy(r => r, StringComparer.Ordinal),
            roles.Keys.OrderBy(r => r, StringComparer.Ordinal));
    }

    private static Dictionary<string, (string Label, string SemanticRole, int Ordinal)> ExtractBuiltIns(string source)
    {
        var arrayMatch = Regex.Match(
            source,
            @"ACCOUNT_TAXONOMY_BUILT_INS\s*=\s*\[(?<body>.*?)\];",
            RegexOptions.Singleline);
        Assert.True(arrayMatch.Success, "ui-core.js 內找不到 ACCOUNT_TAXONOMY_BUILT_INS 陣列。");

        var result = new Dictionary<string, (string, string, int)>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(
            arrayMatch.Groups["body"].Value,
            @"\{\s*categoryId:\s*'(?<id>[^']+)'\s*,\s*label:\s*'(?<label>[^']+)'\s*,\s*ordinal:\s*(?<ordinal>\d+)\s*,\s*semanticRole:\s*'(?<role>[^']+)'"))
        {
            result[match.Groups["id"].Value] = (
                match.Groups["label"].Value,
                match.Groups["role"].Value,
                int.Parse(match.Groups["ordinal"].Value));
        }

        Assert.NotEmpty(result);
        return result;
    }

    private static Dictionary<string, string> ExtractValueLabelMap(string source, string arrayName)
    {
        var arrayMatch = Regex.Match(
            source,
            arrayName + @"\s*=\s*\[(?<body>.*?)\];",
            RegexOptions.Singleline);
        Assert.True(arrayMatch.Success, $"ui-core.js 內找不到 {arrayName} 陣列。");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(
            arrayMatch.Groups["body"].Value,
            @"\{\s*value:\s*'(?<value>[^']+)'\s*,\s*label:\s*'(?<label>[^']+)'"))
        {
            result[match.Groups["value"].Value] = match.Groups["label"].Value;
        }

        Assert.NotEmpty(result);
        return result;
    }

    private static string ReadFrontend(params string[] segments)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot(), "JET", "wwwroot" }.Concat(segments).ToArray()));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("向上尋不到 JET.slnx。");
    }
}
