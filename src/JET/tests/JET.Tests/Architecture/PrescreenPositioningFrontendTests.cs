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
                "先看依分錄編製者與較少使用科目的全期彙總；這兩項是常用的母體判讀面。",
            ["SignalGuidance"] =
                "逐筆命中只供初步判讀，不是高風險裁定；要形成測試範圍，請到「進階條件篩選」組合 KCT 與其他條件。",
            ["ReportGuidance"] =
                "Pre-screening Report 預設隨匯出底稿一併產出，這裡可以先單獨產生；不產生也不影響進階條件篩選、Criteria Selection Report 或 Working Paper。",
            ["OverviewGuidance"] =
                "彙總只描述母體分布；逐筆命中不等於錯誤，也不是高風險裁定；兩者都不代替審計判斷。",
            ["ExportDefaultGuidance"] =
                "匯出底稿時預設一併產出 Pre-screening Report；取消勾選只會少這一份，其餘報告與底稿內容都不受影響。",
            ["ExportPendingRunGuidance"] =
                "目前沒有可用的預篩選結果。維持勾選並按下產生，系統會先執行一次預篩選再產出這份報告；大型案件的預篩選可能需要數分鐘到十餘分鐘。"
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
        Assert.Contains("<span>分錄檢查</span>", card, StringComparison.Ordinal);
        Assert.Contains("<details class=\"prescreen-signals\"", card, StringComparison.Ordinal);
        Assert.Contains("Pre-screening Report", source, StringComparison.Ordinal);
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
        Assert.Contains("輔助訊號與分析執行紀錄", app, StringComparison.Ordinal);
        Assert.Contains("逐筆輔助訊號", app, StringComparison.Ordinal);

        var overview = ReadFrontend("js", "overview-bi.js");
        Assert.Contains("常用母體彙總", overview, StringComparison.Ordinal);
        Assert.Contains("逐筆輔助訊號分布", overview, StringComparison.Ordinal);
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
