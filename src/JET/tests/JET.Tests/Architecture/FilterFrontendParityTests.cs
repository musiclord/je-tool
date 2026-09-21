using System.Text.Json;
using System.Text.RegularExpressions;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// Phase 6 完成後的前後端呈現鏡像守衛。Domain/Application 是 backend 正本；
/// runtime frontend 只保留顯示鏡像，結構片語與欄位集合不得再漂移。
/// </summary>
public sealed class FilterFrontendParityTests
{
    [Fact]
    public void FrequencyReadbackPhrases_MirrorBackendExactly()
    {
        var frontendPhrases = ExtractFrontendFrequencyPhrases();
        var frontendControlPhrases = ExtractFrontendFrequencyControlPhrases();
        var actual = frontendPhrases
            .Select(pair =>
                $"{pair.Key}|{pair.Value}N")
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        var expected = new[]
        {
            "customAccountEntryCount|所選母體內科目分錄筆數 ≤ N",
            "customPreparerEntryCount|所選母體內編製人員分錄筆數 ≤ N"
        };

        Assert.Equal(expected, actual);
        Assert.All(
            frontendPhrases,
            pair => Assert.Equal(
                RenderBackendFrequencyPhrase(pair.Key),
                pair.Value + "N"));
        Assert.All(
            frontendControlPhrases,
            pair => Assert.Equal(
                RenderBackendFrequencyPhrase(pair.Key),
                pair.Value + " N"));
    }

    [Fact]
    public void VoucherDate_MirrorsBackendFilterAndMappingCatalogs()
    {
        var backendDateFields = GlMappingKeys.All
            .Where(key => GlFieldWhitelist.TryResolve(key, out var column)
                && column.Kind == GlFieldKind.Date)
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
        var uiCore = ReadFrontend("js", "ui-core.js");
        var frontendGlFields = ExtractObjectArrayKeys(uiCore, "GL_FIELDS");
        var frontendDateFields = ExtractStringArrayValues(uiCore, "FILTER_DATE_FIELDS");

        Assert.Empty(MissingFrom(backendDateFields, FilterConditionLabels.GlFields.Keys));
        Assert.Empty(MissingFrom(backendDateFields, frontendGlFields));
        Assert.Equal(backendDateFields, frontendDateFields.OrderBy(static key => key, StringComparer.Ordinal));
        Assert.Equal("傳票日期", FilterConditionLabels.GlFields[GlMappingKeys.VoucherDate]);
    }

    [Fact]
    public void MixedRuleJoinReadback_MirrorsNonMutatingExactLeftFoldBoundary()
    {
        var source = ReadFrontend("js", "steps", "filter-step.js");
        var effectiveJoin = ExtractFunction(
            source,
            "effectiveRuleJoin",
            "hasMixedEffectiveRuleJoins");
        var detection = ExtractFunction(
            source,
            "hasMixedEffectiveRuleJoins",
            "groupReadBackExpressionHtml");
        var rendering = ExtractFunction(
            source,
            "groupReadBackExpressionHtml",
            "readBackHtml");

        Assert.Contains(
            "rule.join.trim().toUpperCase()",
            effectiveJoin,
            StringComparison.Ordinal);
        Assert.Contains("raw === 'OR' ? 'OR' : 'AND'", effectiveJoin, StringComparison.Ordinal);
        Assert.Contains("group.rules.slice(1)", detection, StringComparison.Ordinal);
        Assert.Contains(
            "joins.indexOf('AND') >= 0 && joins.indexOf('OR') >= 0",
            detection,
            StringComparison.Ordinal);
        Assert.Contains(
            "exprJoin(labels, groupCombinator(group) === 'OR' ? 'OR' : 'AND'",
            rendering,
            StringComparison.Ordinal);
        Assert.Contains(
            "expression = '（' + expression + opHtml + Ui.esc(labels[i]) + '）'",
            rendering,
            StringComparison.Ordinal);
        Assert.Contains(
            "effectiveRuleJoin(group.rules[i])",
            rendering,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".join =", detection + rendering, StringComparison.Ordinal);
    }

    private static string RenderBackendFrequencyPhrase(string type)
    {
        using var document = JsonDocument.Parse(
            "{\"groups\":[{\"join\":\"OR\",\"rules\":[{\"type\":\""
            + type
            + "\",\"maxEntries\":\"N\",\"join\":\"AND\"}]}]}");
        return FilterConditionRenderer.Render(document.RootElement);
    }

    private static Dictionary<string, string> ExtractFrontendFrequencyPhrases()
    {
        var source = ReadFrontend("js", "steps", "filter-step.js");
        var start = source.IndexOf("function ruleSummaryLabel", StringComparison.Ordinal);
        var end = source.IndexOf("function scenarioPillsHtml", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "找不到 filter-step.js 的 ruleSummaryLabel 邊界。");

        var body = source[start..end];
        var matches = Regex.Matches(
            body,
            @"case '(?<type>custom(?:Preparer|Account)EntryCount)':\s*"
            + @"return prefix \+ '(?<phrase>[^']*)' \+ rule\.maxEntries;",
            RegexOptions.Singleline);
        var result = matches
            .Cast<Match>()
            .ToDictionary(
                match => match.Groups["type"].Value,
                match => match.Groups["phrase"].Value,
                StringComparer.Ordinal);

        Assert.Equal(2, result.Count);
        return result;
    }

    // 2026-09-07 自訂篩選條件改版：張數條件列的可見片語從 ruleControlsHtml 的固定標籤移到「樣態」
    // 對象下拉的選項文字（PATTERN_PARAM_TYPES），輸入框後面只剩單位「張」。守衛的意圖不變：
    // 使用者在列上看到的片語要逐字等於後端讀回句去掉數字的部分。
    // 第一次失敗的證據：收據 20260907-135727110（ruleControlsHtml 找不到兩個片語，Expected 2 Actual 0）。
    private static Dictionary<string, string> ExtractFrontendFrequencyControlPhrases()
    {
        var source = ReadFrontend("js", "steps", "filter-step.js");
        var start = source.IndexOf("var PATTERN_PARAM_TYPES = [", StringComparison.Ordinal);
        var end = source.IndexOf("];", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "找不到 filter-step.js 的 PATTERN_PARAM_TYPES 邊界。");

        var body = source[start..end];
        var matches = Regex.Matches(
            body,
            @"\{ value: '(?<type>custom(?:Preparer|Account)EntryCount)', label: '(?<phrase>[^']*)' \}",
            RegexOptions.Singleline);
        var result = matches
            .Cast<Match>()
            .ToDictionary(
                match => match.Groups["type"].Value,
                match => match.Groups["phrase"].Value,
                StringComparer.Ordinal);

        Assert.Equal(2, result.Count);
        return result;
    }

    private static IReadOnlyCollection<string> ExtractObjectArrayKeys(
        string source,
        string arrayName)
    {
        var body = ExtractArrayBody(source, arrayName);
        var result = Regex.Matches(body, @"\bkey:\s*'(?<key>[^']+)'")
            .Cast<Match>()
            .Select(match => match.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(result);
        return result;
    }

    private static IReadOnlyCollection<string> ExtractStringArrayValues(
        string source,
        string arrayName)
    {
        var body = ExtractArrayBody(source, arrayName);
        var result = Regex.Matches(body, @"'(?<value>[^']+)'")
            .Cast<Match>()
            .Select(match => match.Groups["value"].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(result);
        return result;
    }

    private static string ExtractFunction(
        string source,
        string functionName,
        string nextFunctionName)
    {
        var start = source.IndexOf(
            "function " + functionName,
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "function " + nextFunctionName,
            start,
            StringComparison.Ordinal);
        Assert.True(
            start >= 0 && end > start,
            $"找不到 filter-step.js 的 {functionName} 邊界。");
        return source[start..end];
    }

    private static string ExtractArrayBody(string source, string arrayName)
    {
        var match = Regex.Match(
            source,
            arrayName + @"\s*=\s*\[(?<body>.*?)\];",
            RegexOptions.Singleline);
        Assert.True(match.Success, $"ui-core.js 找不到 {arrayName} 陣列。");
        return match.Groups["body"].Value;
    }

    private static string[] MissingFrom(
        IEnumerable<string> expectedFields,
        IEnumerable<string> availableFields) =>
        expectedFields
            .Except(availableFields, StringComparer.Ordinal)
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();

    private static string ReadFrontend(params string[] relativeSegments)
    {
        var path = Path.Combine(new[] { RepoRoot(), "JET", "wwwroot" }
            .Concat(relativeSegments)
            .ToArray());
        Assert.True(File.Exists(path), $"找不到前端檔：{path}");
        return File.ReadAllText(path);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx，無法定位 repo 根。");
    }
}
