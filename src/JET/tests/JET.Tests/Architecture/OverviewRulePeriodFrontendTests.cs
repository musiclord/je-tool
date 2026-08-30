using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 預篩選規則全期命中分布的 wire key 正本。Application contract 測試與前端鏡射守衛
/// 共用本表；N/A 不另設 status，而由 naReason 與三個 null 數值表達。
/// </summary>
internal static class RulePeriodWireKeys
{
    internal static readonly string[] Block = ["population", "rules"];

    internal static readonly string[] RuleRow =
        ["key", "naReason", "hitLines", "hitVouchers", "ratePct"];
}

/// <summary>
/// S2b 降級版「規則 × 全期」前端邊界守衛。所有命中數、去重傳票數與比率都由
/// prescreen.run 的 rulePeriod 提供；前端只套用中文 mirror、格式化與 ECharts option。
/// </summary>
public sealed class OverviewRulePeriodFrontendTests
{
    private static string ReadFrontend(params string[] relativeParts) =>
        File.ReadAllText(Path.Combine(
            new[] { AppContext.BaseDirectory, "wwwroot" }.Concat(relativeParts).ToArray()));

    [Fact]
    public void RulePeriodRenderer_MirrorsEveryWireKeyAndCanonicalChineseLabels()
    {
        var source = ReadFrontend("js", "overview-bi.js");

        Assert.Contains("rulePeriod", source, StringComparison.Ordinal);
        foreach (var key in RulePeriodWireKeys.Block.Concat(RulePeriodWireKeys.RuleRow))
        {
            Assert.True(
                source.Contains("." + key, StringComparison.Ordinal),
                $"overview-bi.js 未鏡射 rulePeriod wire key '{key}'。");
        }

        Assert.Contains("Ui.PRESCREEN_KEY_OPTIONS", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JetApi", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JetStore", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RulePeriodRenderer_SeparatesMissingResumeNaZeroAndZeroDenominator()
    {
        var source = ReadFrontend("js", "overview-bi.js");
        var renderer = ExtractTopLevelFunction(source, "rulePeriodHtml");
        var percentFormatter = ExtractTopLevelFunction(source, "fmtRulePct");

        // 未執行與舊 summary 必須是 S2b 自己的分流，不能誤命中集中度既有文案而假綠。
        Assert.Contains("尚未執行預篩選（選用）", renderer, StringComparison.Ordinal);
        Assert.Contains("逐筆輔助訊號的全期命中分布", renderer, StringComparison.Ordinal);
        Assert.Contains("需重新執行預篩選以產生此統計", renderer, StringComparison.Ordinal);
        Assert.Contains(
            "舊版本執行結果，尚未包含逐筆輔助訊號分布",
            renderer,
            StringComparison.Ordinal);

        // N/A 只由 naReason 判斷；status='na' 也可能只是適用但零命中，不能拿來裁定。
        Assert.True(
            Regex.IsMatch(source, @"rule\.naReason\s*!==?\s*null", RegexOptions.CultureInvariant),
            "rulePeriod 的逐列 N/A 必須以 rule.naReason != null 判斷。");
        Assert.DoesNotContain("rule.status", source, StringComparison.Ordinal);
        Assert.Contains(
            "rulePeriod.rules.filter(function (rule) { return rule.naReason == null; })",
            source,
            StringComparison.Ordinal);
        Assert.Contains("value: rule.ratePct", source, StringComparison.Ordinal);
        Assert.Contains("rulePeriodNaHtml(rulePeriod)", source, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"不適用規則\"", source, StringComparison.Ordinal);
        Assert.Contains("不適用", source, StringComparison.Ordinal);

        // 有效 0 仍交給百分比 formatter 顯示 0.0%；只有 null 分母顯示「—」。
        Assert.Contains("fmtRulePct(item.ratePct)", source, StringComparison.Ordinal);
        Assert.Contains("return fmtRulePct(value)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fmtPct(rule.ratePct)", source, StringComparison.Ordinal);
        Assert.Contains("pct == null", percentFormatter, StringComparison.Ordinal);
        Assert.Contains("value === 0", percentFormatter, StringComparison.Ordinal);
        Assert.Contains("value.toFixed(1) + '%'", percentFormatter, StringComparison.Ordinal);
        Assert.Contains("value.toFixed(2) + '%'", percentFormatter, StringComparison.Ordinal);
        Assert.Contains("value.toFixed(6)", percentFormatter, StringComparison.Ordinal);
        Assert.Contains("value.toExponential(2) + '%'", percentFormatter, StringComparison.Ordinal);
        Assert.DoesNotContain("!pct", percentFormatter, StringComparison.Ordinal);
        Assert.DoesNotContain("rule.ratePct || 0", source, StringComparison.Ordinal);

        Assert.Contains("rulePeriod.population === 0", source, StringComparison.Ordinal);
        Assert.Contains("查核期間母體為 0", source, StringComparison.Ordinal);
        Assert.Contains("不代表 0%", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_ComposesRulePeriodBetweenPopulationAndConcentration_WithValidateStatsGate()
    {
        var source = ReadFrontend("js", "app.js");
        var overview = ExtractTopLevelFunction(source, "overviewHtml");
        var analyses = ExtractTopLevelFunction(source, "overviewAnalysisHtml");

        var populationIndex = overview.IndexOf("overviewPopulationHtml(state)", StringComparison.Ordinal);
        var analysesIndex = overview.IndexOf("overviewAnalysisHtml(state)", StringComparison.Ordinal);
        var rulePeriodIndex = analyses.IndexOf("overviewRulePeriodHtml(state)", StringComparison.Ordinal);
        var concentrationIndex = analyses.IndexOf("overviewConcentrationHtml(state)", StringComparison.Ordinal);

        Assert.True(populationIndex >= 0, "流程總覽未組裝母體概況。");
        Assert.True(analysesIndex > populationIndex, "母體分析必須排在母體概況之後。");
        Assert.True(rulePeriodIndex >= 0, "母體分析未組裝 rulePeriod。");
        Assert.True(rulePeriodIndex > concentrationIndex, "逐筆輔助訊號分布必須排在常用母體彙總之後。");
        Assert.Contains("state.lastRuns.validate", analyses, StringComparison.Ordinal);
        Assert.Contains("validate.stats", analyses, StringComparison.Ordinal);
        Assert.Contains("return ''", analyses, StringComparison.Ordinal);

        var gate = ExtractTopLevelFunction(source, "overviewRulePeriodHtml");
        Assert.Contains("state.lastRuns.validate", gate, StringComparison.Ordinal);
        Assert.Contains("validate.stats", gate, StringComparison.Ordinal);
        Assert.Contains("return ''", gate, StringComparison.Ordinal);
        Assert.Contains(
            "JetOverviewBi.rulePeriodHtml(state.lastRuns.prescreen",
            gate,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RulePeriodChart_UsesEChartsOptionAndNeutralFullPeriodDenominatorLanguage()
    {
        var source = ReadFrontend("js", "overview-bi.js");
        var chart = ExtractTopLevelFunction(source, "rulePeriodChartOption");

        Assert.Contains("animation: false", chart, StringComparison.Ordinal);
        Assert.Contains("type: 'bar'", chart, StringComparison.Ordinal);
        Assert.Contains("rule.ratePct", chart, StringComparison.Ordinal);
        Assert.Contains("rule.hitLines", chart, StringComparison.Ordinal);
        Assert.Contains("rule.hitVouchers", chart, StringComparison.Ordinal);
        Assert.Contains("rule.naReason", chart, StringComparison.Ordinal);
        Assert.Contains("chartContainerHtml('rule-period'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<svg", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("全查核期間", source, StringComparison.Ordinal);
        Assert.Contains(
            "命中率＝命中分錄數 ÷ 查核期間母體",
            source,
            StringComparison.Ordinal);
        Assert.Contains("此為分布描述，非風險評估。", source, StringComparison.Ordinal);

        Assert.DoesNotContain("http://", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("風險評分", source, StringComparison.Ordinal);
        Assert.DoesNotContain("風險等級", source, StringComparison.Ordinal);
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
