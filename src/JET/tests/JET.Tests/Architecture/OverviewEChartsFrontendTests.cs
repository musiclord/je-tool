using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// S7 的本地 ECharts lifecycle 與純鏡射邊界。這裡只鎖 renderer、mount/dispose、
/// resize 與 option 來源；實際圖表語意仍由各 BI 區塊自己的 wire mirror 守衛負責。
/// </summary>
public sealed class OverviewEChartsFrontendTests
{
    private static string ReadFrontend(params string[] relativeParts) =>
        File.ReadAllText(Path.Combine(
            new[] { AppContext.BaseDirectory, "wwwroot" }.Concat(relativeParts).ToArray()));

    [Fact]
    public void Charts_UseSvgRendererNotMergeAndSingleResizeLifecycle()
    {
        var source = ReadFrontend("js", "overview-bi.js");

        Assert.Contains(
            "global.echarts.init(element, null, { renderer: 'svg' })",
            source,
            StringComparison.Ordinal);
        Assert.Contains("chart.setOption(option, true)", source, StringComparison.Ordinal);
        Assert.Contains("global.addEventListener('resize', resizeCharts)", source, StringComparison.Ordinal);
        Assert.Contains("new global.ResizeObserver", source, StringComparison.Ordinal);
        Assert.Contains("setTimeout(resizeCharts, 30)", source, StringComparison.Ordinal);
        Assert.True(
            Regex.Matches(source, @"\banimation:\s*false\b", RegexOptions.CultureInvariant).Count >= 4,
            "四種 BI option 都必須關閉 animation。");
        Assert.True(
            Regex.Matches(source, @"\bconfine:\s*true\b", RegexOptions.CultureInvariant).Count >= 4,
            "四種 BI tooltip 都必須留在總覽 card 內。");
    }

    [Fact]
    public void OverviewCard_DisposesBeforeReplacementAndMountsAfterInsertion()
    {
        var app = ReadFrontend("js", "app.js");
        var renderer = ExtractTopLevelFunction(app, "renderOverviewCard");

        var disposeIndex = renderer.IndexOf("JetOverviewBi.dispose", StringComparison.Ordinal);
        var replaceIndex = renderer.IndexOf("card.innerHTML = overviewHtml(state)", StringComparison.Ordinal);
        var bindIndex = renderer.IndexOf("bindOverview(card)", StringComparison.Ordinal);
        var mountIndex = renderer.IndexOf("JetOverviewBi.mount", StringComparison.Ordinal);

        Assert.True(disposeIndex >= 0, "重繪前必須 dispose 舊 ECharts instances。");
        Assert.True(disposeIndex < replaceIndex, "dispose 必須發生在 innerHTML 取代之前。");
        Assert.True(bindIndex > replaceIndex, "DOM 取代後才可重新綁定互動。");
        Assert.True(mountIndex > bindIndex, "ECharts 必須在新容器插入並綁定後 mount。");
    }

    [Fact]
    public void Charts_MountOnlyInsideExpandedAnalysisAndDisposeWhenCollapsed()
    {
        var renderer = ReadFrontend("js", "overview-bi.js");
        var app = ReadFrontend("js", "app.js");

        Assert.Contains("element.closest('details')", ExtractTopLevelFunction(renderer, "mount"), StringComparison.Ordinal);
        Assert.Contains("if (section && !section.open) { return; }", ExtractTopLevelFunction(renderer, "mount"), StringComparison.Ordinal);
        Assert.Contains("function disposeWithin(", renderer, StringComparison.Ordinal);
        Assert.Contains("disposeWithin: disposeWithin", renderer, StringComparison.Ordinal);
        Assert.Contains("data-overview-analysis", app, StringComparison.Ordinal);
        Assert.Contains("addEventListener('toggle'", app, StringComparison.Ordinal);
        Assert.Contains("JetOverviewBi.disposeWithin", app, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionBuilders_OnlyMirrorDtoValuesWithoutAuditAggregation()
    {
        var source = ReadFrontend("js", "overview-bi.js");
        var builders = string.Join(
            Environment.NewLine,
            ExtractTopLevelFunction(source, "rulePeriodChartOption"),
            ExtractTopLevelFunction(source, "amountDistributionChartOption"),
            ExtractTopLevelFunction(source, "preparerChartOption"),
            ExtractTopLevelFunction(source, "rareAccountChartOption"));

        Assert.DoesNotContain(".reduce(", builders, StringComparison.Ordinal);
        Assert.DoesNotContain(".sort(", builders, StringComparison.Ordinal);
        Assert.DoesNotContain("* 100", builders, StringComparison.Ordinal);
        Assert.DoesNotContain("+=", builders, StringComparison.Ordinal);
        Assert.DoesNotContain("JetApi", builders, StringComparison.Ordinal);
        Assert.DoesNotContain("JetStore", builders, StringComparison.Ordinal);
    }

    private static string ExtractTopLevelFunction(string source, string functionName)
    {
        var marker = "  function " + functionName + "(";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到前端函式 {functionName}。");
        if (start < 0)
        {
            return string.Empty;
        }

        var nextFunction = source.IndexOf(
            "\n  function ",
            start + marker.Length,
            StringComparison.Ordinal);
        var exportBlock = source.IndexOf(
            "\n  global.JetOverviewBi",
            start + marker.Length,
            StringComparison.Ordinal);
        var end = nextFunction >= 0 ? nextFunction : exportBlock;

        Assert.True(end > start, $"無法界定前端函式 {functionName} 的範圍。");
        return end > start ? source[start..end] : string.Empty;
    }
}
