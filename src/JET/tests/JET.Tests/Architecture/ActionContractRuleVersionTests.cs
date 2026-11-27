using System.Text.RegularExpressions;
using JET.Application;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 篩選版本宣告的掃描規則：句子寫「現行」或「目前」時抓出其中的版本，寫版本沿革的句子不算；
/// 簡寫的版本抓得到，但不會等於程式常數 <see cref="RuleLogicVersions.Filter"/>。
/// </summary>
public sealed partial class ActionContractRuleVersionTests
{
    private static readonly Regex FilterVersion = new(@"(?<![A-Za-z0-9_-])(?:filter-\d{4}-\d{2}-\d{2}-v\d+|v\d+)(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);

    [Fact]
    public void ClaimScanRecognisesBothCurrentWordingsAndIgnoresHistory()
    {
        const string text = """
            目前篩選計算版本為 `filter-2026-10-04-v17`；
            歷史篩選版本為 `filter-2026-10-04-v17`。
            現行篩選版本為
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
