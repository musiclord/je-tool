using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 集中度分析（流程總覽區塊⑤）的 wire key 正本。後端回應的實際欄位集合由
/// <c>PrescreenConcentrationTests</c> 逐層對照本表；前端鏡射則由本檔的守衛對照本表。
/// 任何一邊改名或加欄位而沒有同步，就會有一條測試紅燈。
/// </summary>
internal static class ConcentrationWireKeys
{
    internal static readonly string[] Block =
        ["status", "naReason", "preparers", "rareAccounts", "distinctAccountCount"];

    internal static readonly string[] Preparers =
        ["top", "othersEntryCount", "totalPreparerCount", "totalEntryCount", "top5SharePct"];

    internal static readonly string[] TopRow =
        ["createdBy", "entryCount", "manualCount", "cumulativePct"];

    internal static readonly string[] RareAccountRow =
        ["accountCode", "accountName", "entryCount"];
}

/// <summary>
/// 集中度分析前端邊界的守衛。此區塊的所有審計數字（累積占比、「其他」彙總、前五佔比、
/// 分母）一律由後端算好，前端只鏡射與格式化；「不適用」與「舊執行結果沒有此統計」都必須
/// 以文字降級，不得以 0 冒充統計。圖表 option 只交給本地 vendored ECharts 的 SVG renderer。
/// 這裡留下 wire key 鏡射、不在前端重算、中性用語與圖表設定來源的原始碼檢查；降級說明、檢視分頁的鍵盤操作與
/// 只在統計就緒時顯示，2026-10-14 起改由 <c>tools/tests/frontend-journey-overview.test.cjs</c> 的情境 19 行為測試核對。
/// </summary>
public sealed class OverviewConcentrationFrontendTests
{
    private static string ReadFrontend(params string[] relativeParts) =>
        File.ReadAllText(Path.Combine(
            new[] { AppContext.BaseDirectory, "wwwroot" }.Concat(relativeParts).ToArray()));

    [Fact]
    public void ConcentrationRenderer_MirrorsEveryBackendWireKey()
    {
        var source = ReadFrontend("js", "overview-bi.js");

        foreach (var key in ConcentrationWireKeys.Block
            .Concat(ConcentrationWireKeys.Preparers)
            .Concat(ConcentrationWireKeys.TopRow)
            .Concat(ConcentrationWireKeys.RareAccountRow))
        {
            Assert.True(
                source.Contains("." + key, StringComparison.Ordinal),
                $"overview-bi.js 未鏡射 concentration 的 wire key '{key}'。");
        }
    }

    [Fact]
    public void ConcentrationRenderer_DoesNotRecomputeAuditAggregates()
    {
        var source = ReadFrontend("js", "overview-bi.js");

        // 百分比、累積與「其他」都由後端算好：前端不得再做百分比換算或列的加總。
        Assert.DoesNotContain("* 100", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".reduce(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("+=", source, StringComparison.Ordinal);

        // 只鏡射既有欄位；不得自行呼叫後端或改寫任何狀態。
        Assert.DoesNotContain("JetApi", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JetStore", source, StringComparison.Ordinal);

        // 總數、累積筆數與排序也照後端給的值：原本只查金額級距與圖表設定函式，併入後改查整個檔。
        Assert.DoesNotContain("totalCount", source, StringComparison.Ordinal);
        Assert.DoesNotContain("cumulativeCount", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".sort(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcentrationRenderer_StaysNeutralWithoutRiskLanguage()
    {
        var source = ReadFrontend("js", "overview-bi.js");

        Assert.Contains("此為分布描述，非風險評估。", source, StringComparison.Ordinal);
        Assert.DoesNotContain("風險評分", source, StringComparison.Ordinal);
        Assert.DoesNotContain("風險等級", source, StringComparison.Ordinal);
        Assert.DoesNotContain("異常", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OverviewVisibleCopy_UsesAuditorLanguageInsteadOfImplementationTerms()
    {
        var overview = ReadFrontend("js", "overview-bi.js");
        var visibleLiterals = string.Join(
            Environment.NewLine,
            Regex.Matches(overview, @"'(?<text>(?:\\.|[^'\\])*)'")
                .Select(match => match.Groups["text"].Value));

        foreach (var term in new[] { "後端", "前端", "ECharts", "ECDF", "null", "所需的欄位" })
        {
            Assert.DoesNotContain(term, visibleLiterals, StringComparison.OrdinalIgnoreCase);
        }

        var app = ReadFrontend("js", "app.js");
        // 使用者 2026-10-19 裁定「其他資訊」只留資料庫，不再顯示執行識別碼與規則版本。
        // 首次失敗：20261019-122628964-b12373a9d2f2420182c3d05d5982134a；仍檢查標題，並改為確認兩個內部代號不上畫面。
        Assert.Contains("'<summary>其他資訊</summary>'", app, StringComparison.Ordinal);
        Assert.DoesNotContain("overviewKvRow('執行識別碼'", app, StringComparison.Ordinal);
        Assert.DoesNotContain("overviewKvRow('規則版本'", app, StringComparison.Ordinal);
        Assert.DoesNotContain("parts.push('run '", app, StringComparison.Ordinal);
        Assert.DoesNotContain("parts.push('logic '", app, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcentrationCharts_UseEChartsOptionsWithoutInlineSvgOrExternalUrls()
    {
        var source = ReadFrontend("js", "overview-bi.js");
        var markup = ReadFrontend("index.html");

        Assert.Contains("function preparerChartOption(", source, StringComparison.Ordinal);
        Assert.Contains("function rareAccountChartOption(", source, StringComparison.Ordinal);
        Assert.Contains("type: 'bar'", source, StringComparison.Ordinal);
        Assert.Contains("type: 'line'", source, StringComparison.Ordinal);
        Assert.Contains("type: 'scatter'", source, StringComparison.Ordinal);
        Assert.Contains("connectNulls: false", source, StringComparison.Ordinal);
        Assert.Contains("chartContainerHtml('concentration'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<svg", source, StringComparison.OrdinalIgnoreCase);

        // WebView2 離線執行：允許固定本地 ECharts，仍禁止任何遠端資產。
        Assert.DoesNotContain("http://", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", source, StringComparison.OrdinalIgnoreCase);

        // ECharts 與模組都必須本地載入，且依序排在 app.js 之前。
        var echartsIndex = markup.IndexOf("./vendor/echarts/5.5.0/echarts.min.js", StringComparison.Ordinal);
        var moduleIndex = markup.IndexOf("./js/overview-bi.js", StringComparison.Ordinal);
        var appIndex = markup.IndexOf("./js/app.js", StringComparison.Ordinal);
        Assert.True(echartsIndex >= 0, "index.html 未載入本地 ECharts 5.5.0。");
        Assert.True(moduleIndex > 0, "index.html 未載入 overview-bi.js。");
        Assert.True(echartsIndex < moduleIndex, "ECharts 必須先於 overview-bi.js 載入。");
        Assert.True(moduleIndex < appIndex, "overview-bi.js 必須先於 app.js 載入。");
    }
}
