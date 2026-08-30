using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// validate.run amountDistribution 的 wire 與 presentation mirror 正本。
/// 順序是後端 canonical catalog 的輸出順序，前端不得新增、刪除或重排級距。
/// </summary>
internal static class AmountDistributionWireKeys
{
    internal static readonly string[] Block = ["bins"];

    internal static readonly string[] BinRow = ["key", "count", "ecdfPct"];

    internal static readonly (string Key, string Tick, string Range)[] BinMeta =
    [
        ("zero", "零元", "零元"),
        ("lt1k", "1k", "<1k"),
        ("1k-2k", "2k", "1–2k"),
        ("2k-5k", "5k", "2–5k"),
        ("5k-10k", "10k", "5–10k"),
        ("10k-20k", "20k", "10–20k"),
        ("20k-50k", "50k", "20–50k"),
        ("50k-100k", "100k", "50–100k"),
        ("100k-200k", "200k", "100–200k"),
        ("200k-500k", "500k", "200–500k"),
        ("500k-1M", "1M", "500k–1M"),
        ("1M-2M", "2M", "1–2M"),
        ("2M-5M", "5M", "2–5M"),
        ("5M-10M", "10M", "5–10M"),
        ("gt10M", ">10M", ">10M")
    ];

    internal static readonly string[] BinKeys =
        BinMeta.Select(meta => meta.Key).ToArray();
}

/// <summary>
/// S3 金額級距前端邊界守衛。Count 與 ECDF 都由 validate.run 提供；
/// 前端只鏡射 exact keys、格式化並形成 ECharts option。
/// </summary>
public sealed class OverviewAmountDistributionFrontendTests
{
    private static string ReadFrontend(params string[] relativeParts) =>
        File.ReadAllText(Path.Combine(
            new[] { AppContext.BaseDirectory, "wwwroot" }.Concat(relativeParts).ToArray()));

    [Fact]
    public void AmountDistributionRenderer_MirrorsExactWireKeysAndPresentationRegistry()
    {
        var source = ReadFrontend("js", "overview-bi.js");
        var registry = ExtractArray(source, "AMOUNT_BIN_META");
        const string rowPattern =
            @"\{\s*key:\s*'(?<key>[^']+)'\s*,\s*tick:\s*'(?<tick>[^']+)'\s*,\s*range:\s*'(?<range>[^']+)'\s*\}";
        var rows = Regex.Matches(registry, rowPattern)
            .Select(match => (
                match.Groups["key"].Value,
                match.Groups["tick"].Value,
                match.Groups["range"].Value))
            .ToArray();

        Assert.Equal(AmountDistributionWireKeys.BinMeta, rows);
        Assert.Equal(rows.Length, registry.Count(character => character == '{'));
        Assert.Equal(rows.Length, registry.Count(character => character == '}'));
        Assert.DoesNotContain(".sort(", registry, StringComparison.Ordinal);

        var renderer = ExtractTopLevelFunction(source, "amountDistributionHtml");
        Assert.Contains("amountDistribution", renderer, StringComparison.Ordinal);
        foreach (var key in AmountDistributionWireKeys.Block)
        {
            Assert.True(
                renderer.Contains(key, StringComparison.Ordinal),
                $"overview-bi.js 未鏡射 amountDistribution key '{key}'。");
        }

        var chart = ExtractTopLevelFunction(source, "amountDistributionChartOption");
        foreach (var key in AmountDistributionWireKeys.BinRow)
        {
            Assert.True(
                chart.Contains("." + key, StringComparison.Ordinal),
                $"overview-bi.js 未鏡射 amountDistribution bin key '{key}'。");
        }
    }

    [Fact]
    public void AmountDistributionRenderer_DoesNotRecomputeCountsOrEcdf()
    {
        var source = ReadFrontend("js", "overview-bi.js");
        var renderer = string.Join(
            Environment.NewLine,
            ExtractTopLevelFunction(source, "amountDistributionChartOption"),
            ExtractTopLevelFunction(source, "amountDistributionPanelHtml"),
            ExtractTopLevelFunction(source, "amountDistributionHtml"));

        Assert.DoesNotContain(".reduce(", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("+=", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("* 100", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("JetApi", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("JetStore", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("totalCount", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("cumulativeCount", renderer, StringComparison.Ordinal);
    }

    [Fact]
    public void AmountDistributionRenderer_SeparatesMissingResumeZeroAndNullEcdf()
    {
        var source = ReadFrontend("js", "overview-bi.js");
        var renderer = ExtractTopLevelFunction(source, "amountDistributionHtml");
        var chart = ExtractTopLevelFunction(source, "amountDistributionChartOption");

        Assert.Contains("需重新執行資料驗證以產生此統計", renderer, StringComparison.Ordinal);
        Assert.Contains(
            "舊版本執行結果，尚未包含金額級距與累積分布",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "bin.ecdfPct == null ? null : bin.ecdfPct",
            chart,
            StringComparison.Ordinal);
        Assert.Contains("connectNulls: false", chart, StringComparison.Ordinal);
        Assert.DoesNotContain("bin.ecdfPct || 0", chart, StringComparison.Ordinal);

        var panel = ExtractTopLevelFunction(source, "amountDistributionPanelHtml");
        Assert.Contains("零元另計", panel, StringComparison.Ordinal);
        Assert.Contains("非零元分錄為分母", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void AmountDistributionChart_UsesUpperTicksFullRangeTooltipsAndECharts()
    {
        var source = ReadFrontend("js", "overview-bi.js");
        var chart = ExtractTopLevelFunction(source, "amountDistributionChartOption");
        var panel = ExtractTopLevelFunction(source, "amountDistributionPanelHtml");
        var registry = ExtractArray(source, "AMOUNT_BIN_META");
        var css = ReadFrontend("css", "app.css");

        Assert.Contains("animation: false", chart, StringComparison.Ordinal);
        Assert.Contains("type: 'bar'", chart, StringComparison.Ordinal);
        Assert.Contains("type: 'line'", chart, StringComparison.Ordinal);
        Assert.Contains("chartContainerHtml('amount'", source, StringComparison.Ordinal);
        Assert.Contains("分錄金額級距與累積分布", chart, StringComparison.Ordinal);
        Assert.Contains(">10M", registry, StringComparison.Ordinal);
        Assert.Contains("500k–1M", registry, StringComparison.Ordinal);
        Assert.Contains("fmtAxisCount", chart, StringComparison.Ordinal);
        Assert.Contains("meta.range", chart, StringComparison.Ordinal);
        Assert.Contains("未設重要性門檻線", panel, StringComparison.Ordinal);
        Assert.Contains(".overview-bi__chart--amount", css, StringComparison.Ordinal);
        Assert.Contains("min-height: 360px", css, StringComparison.Ordinal);

        Assert.DoesNotContain("<svg", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", chart, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", chart, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("風險評分", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("風險等級", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_ComposesAmountDistributionBetweenConcentrationAndAuxiliarySignals()
    {
        var source = ReadFrontend("js", "app.js");
        var overview = ExtractTopLevelFunction(source, "overviewHtml");
        var analyses = ExtractTopLevelFunction(source, "overviewAnalysisHtml");

        var populationIndex = overview.IndexOf("overviewPopulationHtml(state)", StringComparison.Ordinal);
        var analysesIndex = overview.IndexOf("overviewAnalysisHtml(state)", StringComparison.Ordinal);
        var rulePeriodIndex = analyses.IndexOf("overviewRulePeriodHtml(state)", StringComparison.Ordinal);
        var amountIndex = analyses.IndexOf("overviewAmountDistributionHtml(state)", StringComparison.Ordinal);
        var concentrationIndex = analyses.IndexOf("overviewConcentrationHtml(state)", StringComparison.Ordinal);

        Assert.True(populationIndex >= 0, "流程總覽未組裝母體概況。");
        Assert.True(analysesIndex > populationIndex, "母體分析必須排在母體概況之後。");
        Assert.True(concentrationIndex >= 0, "母體分析未組裝常用母體彙總。");
        Assert.True(amountIndex > concentrationIndex, "金額級距必須排在常用母體彙總之後。");
        Assert.True(rulePeriodIndex > amountIndex, "逐筆輔助訊號分布必須排在金額級距之後。");
        Assert.Contains("state.lastRuns.validate", analyses, StringComparison.Ordinal);
        Assert.Contains("validate.stats", analyses, StringComparison.Ordinal);
        Assert.Contains("return ''", analyses, StringComparison.Ordinal);

        var gate = ExtractTopLevelFunction(source, "overviewAmountDistributionHtml");
        Assert.Contains("state.lastRuns.validate", gate, StringComparison.Ordinal);
        Assert.Contains("validate.stats", gate, StringComparison.Ordinal);
        Assert.Contains("return ''", gate, StringComparison.Ordinal);
        Assert.Contains("JetOverviewBi.amountDistributionHtml(validate)", gate, StringComparison.Ordinal);
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
        var end = nextFunction >= 0 ? nextFunction : source.Length;

        Assert.True(end > start, $"無法界定前端函式 {functionName} 的範圍。");
        return end > start ? source[start..end] : string.Empty;
    }

    private static string ExtractArray(string source, string arrayName)
    {
        var marker = "  var " + arrayName + " = [";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到前端陣列 {arrayName}。");
        if (start < 0)
        {
            return string.Empty;
        }

        var end = source.IndexOf("\n  ];", start + marker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"無法界定前端陣列 {arrayName} 的範圍。");
        return end > start ? source[start..(end + 5)] : string.Empty;
    }
}
