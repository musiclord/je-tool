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
/// 這裡只核對前端鏡射了每個 wire key 與中文標籤；不適用、真的零與極小比率怎麼顯示、圖表設定與在總覽裡的位置，
/// 2026-10-14 起改由 <c>tools/tests/frontend-journey-overview.test.cjs</c> 的情境 19 行為測試核對。
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
}
