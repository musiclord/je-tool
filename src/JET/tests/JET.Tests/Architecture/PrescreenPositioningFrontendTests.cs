using System.Reflection;
using JET.AuditCore;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 步驟四預篩選重定位的 backend-authority／frontend-mirror 守衛。
/// 可見審計語意由 AuditCore renderer 產生；runtime 前端只鏡射文字與既有 DTO，
/// 並把兩條彙總放在逐筆輔助訊號與可選報告之前。
/// </summary>
public sealed class PrescreenPositioningFrontendTests
{
    private static readonly IReadOnlyDictionary<string, string> ExpectedCopy =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AggregateGuidance"] =
                "查看編製人員與較少使用科目的分錄筆數及金額。",
            ["SignalGuidance"] =
                "符合條件的分錄供初步查核；請在「進階條件篩選」設定本案的測試範圍。",
            ["ReportGuidance"] =
                "可在此產生預篩選報告，或在匯出底稿時一併產生。不產生也可繼續篩選與匯出底稿。",
            ["OverviewGuidance"] =
                // 2026-10-03 用語統一 T1：畫面不再使用「母體」（第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a）。
                "預篩選呈現查核期間分錄的分布與符合條件的分錄，是否需進一步查核由審計員判斷。",
            ["ExportDefaultGuidance"] =
                "預設一併產出預篩選報告；取消勾選不影響其他報告及底稿內容。",
            ["ExportPendingRunGuidance"] =
                "尚無預篩選結果，匯出時會先執行預篩選。大型案件可能需要數分鐘到十餘分鐘。"
        };

    [Fact]
    public void PositioningCopy_ComesFromAuditCoreAndIsMirroredExactlyByTheFrontend()
    {
        var method = typeof(JetAuditProgram).GetMethod(
            "RenderPrescreenPositioning",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var rendered = method!.Invoke(null, null);
        Assert.NotNull(rendered);

        var frontend = ReadFrontend("js", "ui-core.js")
            + ReadFrontend("js", "steps", "validate-step.js")
            + ReadFrontend("js", "steps", "export-step.js")
            + ReadFrontend("js", "overview-bi.js")
            + ReadFrontend("js", "app.js");
        foreach (var pair in ExpectedCopy)
        {
            var property = rendered!.GetType().GetProperty(pair.Key, BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(property);
            Assert.Equal(pair.Value, property!.GetValue(rendered));
            Assert.Contains(pair.Value, frontend, StringComparison.Ordinal);
        }

        Assert.Contains("p.positioning", frontend, StringComparison.Ordinal);
    }

    [Fact]
    public void StepFour_PresentsAggregatesBeforeSecondarySignalsAndOptionalReport()
    {
        var source = ReadFrontend("js", "steps", "validate-step.js");
        var card = ExtractFunction(source, "prescreenCardHtml");

        Assert.Contains("PRESCREEN_AGGREGATE_ITEMS", source, StringComparison.Ordinal);
        Assert.Contains("PRESCREEN_SIGNAL_ITEMS", source, StringComparison.Ordinal);
        Assert.Contains("id=\"prescreen-primary-title\">預篩選</h3>", card, StringComparison.Ordinal);
        Assert.Contains("<span>篩選結果</span>", card, StringComparison.Ordinal);
        Assert.Contains("<details class=\"prescreen-signals\"", card, StringComparison.Ordinal);
        Assert.Contains("預篩選報告", source, StringComparison.Ordinal);
        Assert.Contains("data-action=\"export-prescreen-report\"", source, StringComparison.Ordinal);

        var aggregateIndex = card.IndexOf("PRESCREEN_AGGREGATE_ITEMS", StringComparison.Ordinal);
        var signalsIndex = card.IndexOf("PRESCREEN_SIGNAL_ITEMS", StringComparison.Ordinal);
        var reportIndex = card.IndexOf("prescreenArtifactHtml", StringComparison.Ordinal);
        Assert.True(aggregateIndex >= 0, "步驟四缺少兩條彙總主要區。 ");
        Assert.True(signalsIndex > aggregateIndex, "逐筆輔助訊號必須排在彙總主要區之後。 ");
        Assert.True(reportIndex > signalsIndex, "可選報告入口必須排在輔助訊號之後。 ");

        var bind = ExtractFunction(source, "bind");
        Assert.Contains("Ui.run('執行預篩選'", bind, StringComparison.Ordinal);
        Assert.DoesNotContain("執行預篩選並產生報告", bind, StringComparison.Ordinal);
        Assert.DoesNotContain("return exportPrescreenReport(data)", bind, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_PrioritizesConcentrationAndUsesAuxiliarySignalAndRecordLanguage()
    {
        var app = ReadFrontend("js", "app.js");
        var analyses = ExtractFunction(app, "overviewAnalysisHtml");
        var concentrationIndex = analyses.IndexOf("overviewConcentrationHtml(state)", StringComparison.Ordinal);
        var signalIndex = analyses.IndexOf("overviewRulePeriodHtml(state)", StringComparison.Ordinal);

        Assert.True(concentrationIndex >= 0, "流程總覽缺少彙總分析。 ");
        Assert.True(signalIndex > concentrationIndex, "逐筆訊號分布必須排在彙總分析之後。 ");
        Assert.Contains("預篩選與執行紀錄", app, StringComparison.Ordinal);
        // 改名前的「輔助訊號」用語不得殘留；單獨檢查「預篩選」三字會被上一行涵蓋，等於沒檢查。
        Assert.DoesNotContain("輔助訊號", app, StringComparison.Ordinal);

        var overview = ReadFrontend("js", "overview-bi.js");
        Assert.Contains("預篩選結果分布", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("常用母體彙總", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("輔助訊號", overview, StringComparison.Ordinal);
    }

    private static string ReadFrontend(params string[] relativeParts) =>
        File.ReadAllText(Path.Combine(
            new[] { AppContext.BaseDirectory, "wwwroot" }.Concat(relativeParts).ToArray()));

    private static string ExtractFunction(string source, string functionName)
    {
        var marker = "  function " + functionName + "(";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到前端函式 {functionName}。");
        if (start < 0)
        {
            return string.Empty;
        }

        var next = source.IndexOf("\n  function ", start + marker.Length, StringComparison.Ordinal);
        var end = next >= 0 ? next : source.Length;
        return source[start..end];
    }
}
