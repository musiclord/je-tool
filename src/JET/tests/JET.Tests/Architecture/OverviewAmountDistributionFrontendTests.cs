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
/// 前端只鏡射 exact keys、格式化並形成 ECharts option。這裡只核對 wire key 與級距表的鏡射；
/// 缺值與空白點怎麼畫、圖表設定與在總覽裡的位置，2026-10-14 起改由 <c>tools/tests/frontend-journey-overview.test.cjs</c> 的情境 19 行為測試核對。
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
