using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// Claude Design 前端整合的邊界守衛。設計稿把流程畫成「六步含『完成』、且欄位配對併入匯入」，
/// runtime 的六步模型不同（最後一步＝匯出底稿，欄位配對為獨立步驟）。這些測試把整合時
/// 刻意未採用的設計結構固定下來，避免後續改動悄悄把原型結構搬進 runtime。
/// </summary>
public sealed class DesignIntegrationFrontendTests
{
    private static string FrontendPath(params string[] relativeParts) =>
        Path.Combine(new[] { AppContext.BaseDirectory, "wwwroot" }.Concat(relativeParts).ToArray());

    private static string ReadFrontend(params string[] relativeParts) =>
        File.ReadAllText(FrontendPath(relativeParts));

    [Fact]
    public void Store_KeepsSixStepModel_WithMappingStepAndExportLast()
    {
        var source = ReadFrontend("js", "state.js");

        // 欄位配對是獨立步驟，不得併入匯入。
        Assert.Contains("{ id: 'mapping', label: '欄位配對' }", source, StringComparison.Ordinal);

        // 最後一步是匯出底稿；設計原型的第六步「完成」不得成為 STEPS 成員。
        Assert.Contains("{ id: 'export', label: '匯出底稿' }", source, StringComparison.Ordinal);
        Assert.DoesNotContain("id: 'complete'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("label: '完成' }", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CaseSummary_SeparatesCurrentStepPositionFromCompletedProgress()
    {
        var markup = ReadFrontend("index.html");

        // 頁首列的是「目前步驟」（位置），不是「進度」（已完成步驟數）——兩者數值不同，標籤不可互換。
        Assert.Contains("<span class=\"case-summary__label\">目前步驟</span>", markup, StringComparison.Ordinal);
        Assert.Contains("data-bind=\"case-progress\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"case-summary__label\">進度</span>", markup, StringComparison.Ordinal);

        // 查核期間欄位與既有固定 data-bind 一併保留。
        Assert.Contains("data-bind=\"case-period\"", markup, StringComparison.Ordinal);
        Assert.Contains("data-bind=\"case-id\"", markup, StringComparison.Ordinal);
        Assert.Contains("data-bind=\"case-client\"", markup, StringComparison.Ordinal);
        Assert.Contains("data-bind=\"case-step\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_StaysReadOnly_AndDeclaresNotApplicableSemantics()
    {
        var source = ReadFrontend("js", "app.js") + ReadFrontend("js", "ui-core.js");

        // 總覽的解讀文字要保留三個不可混淆的語意：符合條件不等於錯誤、不適用不等於零、判斷由審計員做。
        // 2026-09-22 使用者要求改成白話後，前兩個語意合併在同一句，所以這裡檢查整句，不拆開檢查片段。
        // 2026-10-03 用語統一 T1：畫面不再使用「母體」，與 AuditCore 正本一起改（第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a）。
        Assert.Contains("預篩選呈現查核期間分錄的分布與符合條件的分錄，是否需進一步查核由審計員判斷。", source, StringComparison.Ordinal);
        Assert.DoesNotContain("母體分布", source, StringComparison.Ordinal);
        // 2026-10-02 整體複審 W16：「不適用不等於零」改成完整句，仍鎖住「不適用不代表沒有符合的分錄」這個語意；舊短句不得再出現。
        Assert.Contains("列在「不適用規則」的條件因缺少所需資料或設定而沒有執行，不代表沒有符合的分錄。", source, StringComparison.Ordinal);
        Assert.DoesNotContain("不適用不等於零", source, StringComparison.Ordinal);
        Assert.DoesNotContain("不含綜合風險分數或審計判斷", source, StringComparison.Ordinal);

        // 總覽不得出現風險分級／評分或引導判斷的措辭。
        Assert.DoesNotContain("風險評分", source, StringComparison.Ordinal);
        Assert.DoesNotContain("高風險專案", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_PopulationBlock_MirrorsValidateStatsWithoutFrontendAggregation()
    {
        var source = ReadFrontend("js", "app.js");

        // 母體 KPI 一律直接鏡射 validate.run 的 stats 欄位。
        Assert.Contains("state.lastRuns.validate", source, StringComparison.Ordinal);
        Assert.Contains("stats.glRowCount", source, StringComparison.Ordinal);
        Assert.Contains("stats.voucherCount", source, StringComparison.Ordinal);
        Assert.Contains("stats.totalDebit", source, StringComparison.Ordinal);

        // 未執行驗證時走空狀態，不得以 0 冒充「已核對為零」。
        Assert.Contains("尚無分錄統計", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportStep_RendersCompletionSummary_WithoutAddingAStep()
    {
        var source = ReadFrontend("js", "steps", "export-step.js");

        // 「完成」只是匯出成功後的步驟內狀態。
        Assert.Contains("案件流程已完成", source, StringComparison.Ordinal);
        Assert.Contains("completionSummaryHtml", source, StringComparison.Ordinal);

        // 出現條件綁定既有 artifact，不另行判定審計結論。
        Assert.Contains("criteriaArtifact", source, StringComparison.Ordinal);
        Assert.Contains("workpapers", source, StringComparison.Ordinal);

        // 完成摘要不得改寫步驟索引或進度持久化。
        Assert.DoesNotContain("setStepIndex", source, StringComparison.Ordinal);
        Assert.DoesNotContain("projectSaveProgress", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontend_LoadsOnlyVendoredDesignAssets_WithTheirLicenses()
    {
        var markup = ReadFrontend("index.html");
        var css = ReadFrontend("css", "app.css");
        var loadableMarkup = markup + Environment.NewLine + css;

        // WebView2 離線執行：任何 runtime 載入面都不得出現外部 URL。
        Assert.DoesNotContain("http://", loadableMarkup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", loadableMarkup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"//", loadableMarkup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"//", loadableMarkup, StringComparison.OrdinalIgnoreCase);

        const string echartsPath = "./vendor/echarts/5.5.0/echarts.min.js";
        const string overviewPath = "./js/overview-bi.js";
        const string appPath = "./js/app.js";
        var echartsIndex = markup.IndexOf(echartsPath, StringComparison.Ordinal);
        var overviewIndex = markup.IndexOf(overviewPath, StringComparison.Ordinal);
        var appIndex = markup.IndexOf(appPath, StringComparison.Ordinal);
        Assert.True(echartsIndex >= 0, "index.html 未載入 vendored ECharts 5.5.0。");
        Assert.True(echartsIndex < overviewIndex, "ECharts 必須先於 overview-bi.js 載入。");
        Assert.True(overviewIndex < appIndex, "overview-bi.js 必須先於 app.js 載入。");

        Assert.Contains("@font-face", css, StringComparison.Ordinal);
        Assert.Contains(
            "../assets/fonts/noto-sans-tc/NotoSansTC-VariableFont_wght.ttf",
            css,
            StringComparison.Ordinal);
        Assert.Contains(
            "../assets/fonts/noto-serif-tc/NotoSerifTC-VariableFont_wght.ttf",
            css,
            StringComparison.Ordinal);

        var assets = new[]
        {
            ("vendor/echarts/5.5.0/echarts.min.js", 500_000L),
            ("vendor/echarts/5.5.0/LICENSE.txt", 500L),
            ("vendor/echarts/5.5.0/NOTICE.txt", 100L),
            ("vendor/echarts/5.5.0/LICENSE-d3.txt", 500L),
            ("assets/fonts/noto-sans-tc/NotoSansTC-VariableFont_wght.ttf", 1_000_000L),
            ("assets/fonts/noto-sans-tc/OFL.txt", 1_000L),
            ("assets/fonts/noto-serif-tc/NotoSerifTC-VariableFont_wght.ttf", 1_000_000L),
            ("assets/fonts/noto-serif-tc/OFL.txt", 1_000L)
        };
        foreach (var (relativePath, minimumBytes) in assets)
        {
            var path = FrontendPath(relativePath.Split('/'));
            Assert.True(File.Exists(path), $"缺少 vendored asset：{relativePath}");
            Assert.True(
                new FileInfo(path).Length >= minimumBytes,
                $"Vendored asset 過小或不完整：{relativePath}");
        }

        Assert.Contains(
            "5.5.0",
            ReadFrontend("vendor", "echarts", "5.5.0", "echarts.min.js"),
            StringComparison.Ordinal);
        Assert.Contains(
            "Apache License",
            ReadFrontend("vendor", "echarts", "5.5.0", "LICENSE.txt"),
            StringComparison.Ordinal);
        Assert.Contains(
            "SIL OPEN FONT LICENSE",
            ReadFrontend("assets", "fonts", "noto-sans-tc", "OFL.txt"),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "SIL OPEN FONT LICENSE",
            ReadFrontend("assets", "fonts", "noto-serif-tc", "OFL.txt"),
            StringComparison.OrdinalIgnoreCase);
    }
}
