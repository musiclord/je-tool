using System.Text.RegularExpressions;
using JET.Application;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// action 契約裡凡是說「現行」或「目前」篩選版本的句子，版本都要等於程式常數 <see cref="RuleLogicVersions.Filter"/>。
/// 版本沿革可以列舊版本；只有宣稱現行的句子要跟程式一致，避免契約同時寫出兩個現行版本。
/// </summary>
public sealed class ActionContractRuleVersionTests
{
    private static readonly Regex FilterVersion = new(@"(?<![A-Za-z0-9_-])(?:filter-\d{4}-\d{2}-\d{2}-v\d+|v\d+)(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);

    [Fact]
    public void EveryCurrentFilterVersionClaimInTheManifestMatchesTheProgramConstant()
    {
        var manifest = File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot, "docs", "action-contract-manifest.md"));
        var claims = CurrentFilterVersionClaims(manifest);
        Assert.NotEmpty(claims);
        Assert.All(claims, claim => Assert.True(claim.Version == RuleLogicVersions.Filter,
            $"契約宣稱現行篩選版本為 {claim.Version}，程式為 {RuleLogicVersions.Filter}：{claim.Clause}"));
    }

    [Fact]
    public void ClaimScanRecognisesBothCurrentWordingsAndIgnoresHistory()
    {
        const string text = """
            不新增由後端解讀字母的通道。目前篩選計算版本為 `filter-2026-10-04-v17`；
            2026-10-04 落實空白號碼的裁定，篩選版本推進為 `filter-2026-10-04-v17`。
            2026-10-06 加入查核期末視窗，現行篩選版本為
            `filter-2026-10-06-v18`。
            """;
        Assert.Equal(
            new[] { "filter-2026-10-04-v17", "filter-2026-10-06-v18" },
            CurrentFilterVersionClaims(text).Select(claim => claim.Version));
    }

    [Theory]
    [InlineData("目前篩選計算版本為 `v17`。", "v17")]
    [InlineData("現行篩選版本為 v18。", "v18")]
    [InlineData("目前篩選版本為v17。", "v17")]
    public void CurrentShorthandClaimsAreFoundButCannotMatchTheFullProgramVersion(string text, string version)
    {
        var claim = Assert.Single(CurrentFilterVersionClaims(text));
        Assert.Equal(version, claim.Version);
        Assert.NotEqual(RuleLogicVersions.Filter, claim.Version);
        Assert.Empty(CurrentFilterVersionClaims("歷史篩選版本為 v17。"));
        Assert.Empty(CurrentFilterVersionClaims("目前的名稱為 savev17 或 v17suffix。"));
    }

    private static IReadOnlyList<(string Clause, string Version)> CurrentFilterVersionClaims(string text) =>
        text.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal)
            .Split(['。', '；'])
            .Where(clause => clause.Contains("現行", StringComparison.Ordinal) || clause.Contains("目前", StringComparison.Ordinal))
            .SelectMany(clause => FilterVersion.Matches(clause).Select(match => (clause, match.Value)))
            .ToArray();
}
