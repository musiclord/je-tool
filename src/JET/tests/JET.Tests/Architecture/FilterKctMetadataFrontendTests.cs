using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// KCT 情境來源與選填 metadata 的 frontend mirror guard。
/// Domain/Application 仍是權威；這裡只鎖主前端所有送出路徑共用同一份投影，
/// 並避免 KCT marker 移除後留下會錯誤豁免名稱／動機的 stale source。
/// </summary>
public sealed class FilterKctMetadataFrontendTests
{
    [Fact]
    public void KctSourceProjection_DerivesDraftMarker_PreservesSavedSource_AndOmitsAuthoredSource()
    {
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        var marker = ExtractFunction(filter, "hasKctMarker");
        var source = ExtractFunction(filter, "scenarioSource");
        var projection = ExtractFunction(filter, "toWireScenario");

        Assert.Contains("(scenario.groups || []).some", marker, StringComparison.Ordinal);
        Assert.Contains("(group.rules || []).some", marker, StringComparison.Ordinal);
        Assert.Contains("rule[KCT_LETTER_KEY]", marker, StringComparison.Ordinal);

        Assert.Matches(
            @"hasKctMarker\(scenario\)\s*\|\|\s*scenario\.source\s*===\s*'kct'",
            source);
        Assert.Contains("? 'kct' : null", source, StringComparison.Ordinal);

        Assert.Contains("scenarioSource(s)", projection, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                @"if\s*\([^)]*source[^)]*\)\s*\{\s*wire\.source\s*=\s*(?:source|'kct')\s*;",
                RegexOptions.Singleline),
            projection);
        Assert.DoesNotContain("source: undefined", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("source: null", projection, StringComparison.Ordinal);
    }

    [Fact]
    public void KctMetadataOptionality_FollowsCurrentMarkerLifecycle()
    {
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        var requirement = ExtractFunction(filter, "requiresScenarioMetadata");
        var builder = ExtractFunction(filter, "scenarioBuilderHtml");
        var gate = ExtractFunction(filter, "scenarioGate");
        var softGate = ExtractFunction(filter, "softRefreshGate");

        Assert.Contains("return !hasKctMarker(draft)", requirement, StringComparison.Ordinal);

        Assert.Contains("requiresScenarioMetadata(draft)", builder, StringComparison.Ordinal);
        Assert.Contains("form__req", builder, StringComparison.Ordinal);
        Assert.Contains("選填", builder, StringComparison.Ordinal);

        Assert.Contains("requiresScenarioMetadata(draft)", gate, StringComparison.Ordinal);
        Assert.Matches(@"metadataRequired\s*&&\s*nameEmpty", gate);
        Assert.Matches(@"metadataRequired\s*&&\s*rationaleEmpty", gate);

        Assert.Contains("requiresScenarioMetadata(draft)", softGate, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"metadataRequired[\s\S]*draft\.name", RegexOptions.Singleline),
            softGate);
        Assert.Matches(
            new Regex(@"metadataRequired[\s\S]*draft\.rationale", RegexOptions.Singleline),
            softGate);

        Assert.DoesNotContain("draft.source", builder + gate + softGate, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewCommitResaveAndRemove_AllUseTheSharedScenarioProjection()
    {
        var filter = ReadFrontend("js", "steps", "filter-step.js");

        // 草稿與已存情境的兩條 preview 路徑。
        Assert.Contains("scenario: toWireDraft(draft)", filter, StringComparison.Ordinal);
        Assert.Contains("scenario: toWireScenario(scenario)", filter, StringComparison.Ordinal);

        // stale revision 重存、加入草稿後 replace-all、以及移除情境後 replace-all。
        Assert.Contains(
            "state.filter.savedScenarios.map(toWireScenario)",
            filter,
            StringComparison.Ordinal);
        Assert.Contains(
            "current.savedScenarios.map(toWireScenario)",
            filter,
            StringComparison.Ordinal);
        Assert.Contains(".concat([toWireDraft(current.draft)])", filter, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                @"removeState\.filter\.savedScenarios\s*\.filter\(.*?\)\s*\.map\(toWireScenario\)",
                RegexOptions.Singleline),
            filter);

        // Runtime 已無 demo handler；兩條 preview 與三條 commit 使用者路徑仍全部走同一投影。
        Assert.DoesNotContain("demoScenario", filter, StringComparison.Ordinal);
        Assert.Equal(
            2,
            Regex.Matches(
                filter,
                @"JetApi\.filterPreview\(\{\s*populationScope:\s*populationScope",
                RegexOptions.Multiline).Count);
        Assert.Equal(
            3,
            Regex.Matches(
                filter,
                @"JetApi\.filterCommit\(\{\s*populationScope:\s*populationScope",
                RegexOptions.Multiline).Count);
        Assert.Equal(
            3,
            Regex.Matches(filter, Regex.Escape("Store.setSavedScenarios(data.scenarios)")).Count);
        Assert.DoesNotContain("Store.setSavedScenarios(commit.scenarios)", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void BackendScenarioSummary_RoundTripsOnlyCanonicalKctSource()
    {
        var renderer = ReadApplicationFile("Support", "FilterScenarioSummaryRenderer.cs");
        var projectLoad = ReadApplicationFile("Handlers", "Project", "ProjectLoadHandler.cs");
        var filterHandlers = ReadApplicationFile("Handlers", "FilterHandlers.cs");

        Assert.Contains("source,", renderer, StringComparison.Ordinal);
        Assert.Contains("TryGetProperty(\"source\"", renderer, StringComparison.Ordinal);
        Assert.Contains("FilterScenarioSources.IsKct", renderer, StringComparison.Ordinal);
        Assert.Contains("FilterScenarioSources.Kct", renderer, StringComparison.Ordinal);
        Assert.Contains("FilterScenarioSummaryRenderer.Render", projectLoad, StringComparison.Ordinal);
        Assert.Contains("FilterScenarioSummaryRenderer.Render", filterHandlers, StringComparison.Ordinal);
    }

    private static string ExtractFunction(string source, string name)
    {
        var start = source.IndexOf($"function {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"filter-step.js 找不到 {name} helper。");

        var opened = false;
        var depth = 0;
        for (var index = start; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                opened = true;
                depth++;
            }
            else if (source[index] == '}' && opened && --depth == 0)
            {
                return source[start..(index + 1)];
            }
        }

        throw new Xunit.Sdk.XunitException($"filter-step.js 的 {name} helper 邊界不完整。");
    }

    private static string ReadFrontend(params string[] segments) =>
        File.ReadAllText(
            Path.Combine(new[] { RepoRoot(), "JET", "wwwroot" }.Concat(segments).ToArray()));

    private static string ReadApplicationFile(params string[] segments) =>
        File.ReadAllText(
            Path.Combine(new[] { RepoRoot(), "JET", "Application" }.Concat(segments).ToArray()));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx。");
    }
}
