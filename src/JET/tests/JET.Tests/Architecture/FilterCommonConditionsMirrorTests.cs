using System.Text.RegularExpressions;
using JET.Application;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 裁定 2026-10-29-16：情境名稱與篩選動機留白時由後端補上自動文字，畫面帶入的文字和後端產生的必須相同。
/// 後端用的卡片代號、卡片名稱、評估說明與動機句首是前端 ui-core.js 的鏡像，這裡逐字比對，順序也要相同。
/// </summary>
public sealed class FilterCommonConditionsMirrorTests
{
    [Fact]
    public void CommonConditionCards_MatchFrontendCardsInOrder()
    {
        var source = ProductSources.Frontend("ui-core.js");
        var start = source.IndexOf("var FILTER_COMMON_CONDITIONS = [", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到前端 FILTER_COMMON_CONDITIONS。");
        var end = source.IndexOf("\n  ];", start, StringComparison.Ordinal);
        var block = source[start..end];
        var entries = Regex.Split(block, @"(?=\{ key: ')").Skip(1).ToArray();

        var frontend = entries.Select(entry => new FilterCommonConditions.Card(
            Regex.Match(entry, @"^\{ key: '(?<v>[^']+)'").Groups["v"].Value,
            Regex.Match(entry, @"\blabel: '(?<v>[^']+)'").Groups["v"].Value,
            Regex.Match(entry, "\\bdefaultRationale: \"(?<v>[^\"]+)\"") is { Success: true } rationale
                ? rationale.Groups["v"].Value
                : null)).ToArray();

        Assert.Equal(10, frontend.Length);
        Assert.Equal(frontend, FilterCommonConditions.All);
    }

    [Fact]
    public void GeneratedRationalePrefixes_MatchFrontend()
    {
        var source = ProductSources.Frontend("ui-core.js");
        var match = Regex.Match(source, @"var GENERATED_RATIONALE_PREFIXES = \['(?<a>[^']+)', '(?<b>[^']+)'\];");
        Assert.True(match.Success, "找不到前端 GENERATED_RATIONALE_PREFIXES。");
        Assert.Equal(
            new[] { match.Groups["a"].Value, match.Groups["b"].Value },
            FilterScenarioAutomaticMetadata.GeneratedRationalePrefixes);
    }
}
