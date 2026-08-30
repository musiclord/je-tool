using System.Text.Json;
using System.Text.RegularExpressions;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// Stage 5 filter AST frontend contract guards. The backend renderer and validator remain
/// authoritative; the runtime frontend only mirrors labels, edits wire data, and provides
/// accessible form guidance. All examples are synthetic.
/// </summary>
public sealed class FilterAstFrontendContractTests
{
    private const string RowScopeLabel = "同一分錄列";
    private const string SameVoucherScopeLabel = "同一傳票";
    private const string OutputAnchorLabel = "輸出錨點（第 1 條）";
    private const string SameVoucherExplanation = "後續條件可由同一傳票的其他分錄列符合";
    private const string ContainsAnyLabel = "包含任一值";
    private const string ExactAnyLabel = "完全符合任一值";
    private const string PreserveSpacesLabel = "保留 ASCII 空白";
    private const string RemoveSpacesLabel = "移除 ASCII 空白";

    [Fact]
    public void GroupMatchScope_OffersClosedOptionsAndAccessibleSameVoucherGuidance()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        var options = ExtractValueLabelMap(core, "FILTER_MATCH_SCOPE_OPTIONS");

        Assert.Equal(2, options.Count);
        Assert.Equal(RowScopeLabel, options["row"]);
        Assert.Equal(SameVoucherScopeLabel, options["sameVoucher"]);

        Assert.Contains("data-group-bind=\"matchScope\"", filter, StringComparison.Ordinal);
        Assert.Contains("<fieldset", filter, StringComparison.Ordinal);
        Assert.Contains("<legend", filter, StringComparison.Ordinal);
        Assert.Contains(OutputAnchorLabel, filter, StringComparison.Ordinal);
        Assert.Contains(SameVoucherExplanation, filter, StringComparison.Ordinal);
        Assert.Contains("aria-describedby", filter, StringComparison.Ordinal);

        Assert.Matches(
            new Regex(
                @"(?:ri|ruleIndex)\s*===\s*0[\s\S]{0,500}"
                + @"OUTPUT_ANCHOR_LABEL",
                RegexOptions.CultureInvariant),
            filter);
        Assert.Matches(
            new Regex(
                @"matchScope\s*===\s*'sameVoucher'[\s\S]{0,1400}\.join\s*=\s*'AND'",
                RegexOptions.CultureInvariant),
            filter);
        var combinator = ExtractFunction(filter, "comboSegment", "matchScopeHtml");
        Assert.Contains("disabled aria-disabled=\"true\"", combinator, StringComparison.Ordinal);
        Assert.Matches(@"sameVoucher\s*\?\s*'OR'\s*:\s*null", filter);
    }

    [Fact]
    public void TextSet_UsesLineDelimitedTextareaAndClosedModeOptions()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        var modes = ExtractValueLabelMap(core, "TEXT_SET_MODE_OPTIONS");
        var normalizations = ExtractValueLabelMap(core, "TEXT_SET_NORMALIZATION_OPTIONS");

        Assert.Equal(2, modes.Count);
        Assert.Equal(ContainsAnyLabel, modes["contains"]);
        Assert.Equal(ExactAnyLabel, modes["exact"]);
        Assert.Equal(2, normalizations.Count);
        Assert.Equal(PreserveSpacesLabel, normalizations["preserve"]);
        Assert.Equal(RemoveSpacesLabel, normalizations["removeAsciiSpaces"]);

        var controls = ExtractFunction(filter, "ruleControlsHtml", "ruleSummaryLabel");
        var textSetCase = ExtractSwitchCase(controls, "textSet");
        Assert.Contains("<textarea", textSetCase, StringComparison.Ordinal);
        Assert.Contains("data-rule-bind=\"values\"", textSetCase, StringComparison.Ordinal);
        Assert.Contains("每行一個值", textSetCase, StringComparison.Ordinal);
        Assert.Contains("aria-describedby", textSetCase, StringComparison.Ordinal);
        Assert.Contains("Array.isArray(rule.values)", textSetCase, StringComparison.Ordinal);
        Assert.Matches(@"\.join\(\s*'\\n'\s*\)", textSetCase);

        Assert.Matches(
            new Regex(@"\.split\(\s*/[^/]*(?:\\r|\\n)[^/]*/\s*\)", RegexOptions.CultureInvariant),
            filter);
        Assert.DoesNotContain("split(',')", textSetCase, StringComparison.Ordinal);
        var limitMatch = Regex.Match(
            core,
            @"var\s+TEXT_SET_MAX_VALUES\s*=\s*(?<value>\d+)\s*;",
            RegexOptions.CultureInvariant);
        Assert.True(limitMatch.Success, "ui-core.js 內找不到 TEXT_SET_MAX_VALUES。 ");
        Assert.Equal(
            FilterScenarioLimits.MaxTextSetValuesPerRule,
            int.Parse(limitMatch.Groups["value"].Value));
        Assert.Contains("TEXT_SET_MAX_VALUES: TEXT_SET_MAX_VALUES", core, StringComparison.Ordinal);
        Assert.Contains("Ui.TEXT_SET_MAX_VALUES", textSetCase, StringComparison.Ordinal);

        var limitGuard = ExtractFunction(filter, "hasOversizedTextSet", "scenarioGate");
        Assert.Contains("rule.values.length > Ui.TEXT_SET_MAX_VALUES", limitGuard, StringComparison.Ordinal);
        var gate = ExtractFunction(filter, "scenarioGate", "softRefreshGate");
        Assert.Contains("hasOversizedTextSet(draft)", gate, StringComparison.Ordinal);
        Assert.Contains("文字值清單每組最多", gate, StringComparison.Ordinal);
        var softGate = ExtractFunction(filter, "softRefreshGate", "softRefreshReadback");
        Assert.Contains("hasOversizedTextSet(draft)", softGate, StringComparison.Ordinal);
        Assert.Contains("if (!scenarioGate(container, false))", filter, StringComparison.Ordinal);
        Assert.Contains("if (!scenarioGate(container, true))", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedWireProjection_PreservesMatchScopeAndValuesArrayWhileRemovingUiMetadata()
    {
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        var projection = ExtractFunction(filter, "toWireScenario", "groupCombinator");

        Assert.Matches(@"matchScope\s*:\s*g(?:roup)?\.matchScope", projection);
        Assert.Contains("Object.keys(r).forEach", projection, StringComparison.Ordinal);
        Assert.Contains("k.indexOf('__') !== 0", projection, StringComparison.Ordinal);
        Assert.Contains("Array.isArray(r[k])", projection, StringComparison.Ordinal);
        Assert.Contains("r[k].slice()", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("values.join", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("values.toString", projection, StringComparison.Ordinal);

        // Every live preview/replace-all path must retain the existing shared projection seam.
        Assert.Contains("scenario: toWireDraft(draft)", filter, StringComparison.Ordinal);
        Assert.Contains("scenario: toWireScenario(scenario)", filter, StringComparison.Ordinal);
        Assert.Contains("state.filter.savedScenarios.map(toWireScenario)", filter, StringComparison.Ordinal);
        Assert.Contains("current.savedScenarios.map(toWireScenario)", filter, StringComparison.Ordinal);
        Assert.Contains(".concat([toWireDraft(current.draft)])", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void BackendRendererLabels_AreMirroredExactlyByTheRuntimeFrontend()
    {
        var frontend = ReadFrontend("js", "ui-core.js")
            + ReadFrontend("js", "steps", "filter-step.js");

        var rowContains = RenderTextSet("row", "contains", "preserve");
        Assert.Contains(ContainsAnyLabel, rowContains, StringComparison.Ordinal);
        Assert.Contains(PreserveSpacesLabel, rowContains, StringComparison.Ordinal);

        var rowExact = RenderTextSet("row", "exact", "removeAsciiSpaces");
        Assert.Contains(ExactAnyLabel, rowExact, StringComparison.Ordinal);
        Assert.Contains(RemoveSpacesLabel, rowExact, StringComparison.Ordinal);

        var sameVoucher = RenderSameVoucherTextSet();
        Assert.Contains(OutputAnchorLabel, sameVoucher, StringComparison.Ordinal);
        Assert.Contains(SameVoucherExplanation, sameVoucher, StringComparison.Ordinal);
        Assert.Contains("OUTPUT_ANCHOR_LABEL + '：' + labels[0]", frontend, StringComparison.Ordinal);
        Assert.Contains("SAME_VOUCHER_EXPLANATION + '：' + labels[1]", frontend, StringComparison.Ordinal);
        Assert.Contains("textSetValues.join('、')", frontend, StringComparison.Ordinal);

        foreach (var label in new[]
        {
            ContainsAnyLabel,
            ExactAnyLabel,
            PreserveSpacesLabel,
            RemoveSpacesLabel,
            OutputAnchorLabel,
            SameVoucherExplanation
        })
        {
            Assert.Contains(label, frontend, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("data-bind=\"scenario-name\"")]
    [InlineData("data-bind=\"scenario-rationale\"")]
    [InlineData("data-bind=\"scenario-notice\"")]
    [InlineData("data-bind=\"preview-pane-body\"")]
    [InlineData("data-action=\"add-rule\"")]
    [InlineData("data-action=\"add-set\"")]
    [InlineData("data-action=\"remove-rule\"")]
    [InlineData("data-action=\"preview-scenario\"")]
    [InlineData("data-action=\"save-scenario\"")]
    [InlineData("data-action=\"resave-scenarios\"")]
    [InlineData("data-action=\"toggle-scenario\"")]
    [InlineData("data-action=\"remove-scenario\"")]
    public void ExpandedFilterBuilder_PreservesStableRuntimeHooks(string hook)
    {
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        Assert.Contains(hook, filter, StringComparison.Ordinal);
    }

    private static string RenderTextSet(string matchScope, string mode, string normalization)
    {
        var scenario = new
        {
            groups = new[]
            {
                new
                {
                    join = "AND",
                    matchScope,
                    rules = new[]
                    {
                        new
                        {
                            join = "AND",
                            type = "textSet",
                            field = "description",
                            mode,
                            normalization,
                            values = new[] { "synthetic_alpha", "synthetic beta" }
                        }
                    }
                }
            }
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(scenario));
        return FilterConditionRenderer.Render(document.RootElement);
    }

    private static string RenderSameVoucherTextSet()
    {
        var scenario = new
        {
            groups = new[]
            {
                new
                {
                    join = "AND",
                    matchScope = "sameVoucher",
                    rules = new object[]
                    {
                        new
                        {
                            join = "AND",
                            type = "textSet",
                            field = "description",
                            mode = "contains",
                            normalization = "preserve",
                            values = new[] { "synthetic_anchor" }
                        },
                        new
                        {
                            join = "AND",
                            type = "textSet",
                            field = "accName",
                            mode = "exact",
                            normalization = "removeAsciiSpaces",
                            values = new[] { "synthetic evidence" }
                        }
                    }
                }
            }
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(scenario));
        return FilterConditionRenderer.Render(document.RootElement);
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

    private static string ExtractSwitchCase(string switchBody, string caseName)
    {
        var match = Regex.Match(
            switchBody,
            @"case\s+'" + Regex.Escape(caseName) + @"':(?<body>[\s\S]*?)(?=\n\s*case\s+'|\n\s*default:)");
        Assert.True(match.Success, $"filter-step.js 找不到 {caseName} 控制項 case。");
        return match.Groups["body"].Value;
    }

    private static string ExtractFunction(string source, string functionName, string nextFunctionName)
    {
        var start = source.IndexOf("function " + functionName, StringComparison.Ordinal);
        var end = source.IndexOf("function " + nextFunctionName, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"找不到 filter-step.js 的 {functionName} 邊界。");
        return source[start..end];
    }

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
