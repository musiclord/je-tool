using System.Reflection;
using JET.AuditCore;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 步驟四預篩選重定位的 backend-authority／frontend-mirror 守衛。
/// 可見審計語意由 AuditCore renderer 產生；runtime 前端只鏡射文字與既有 DTO。
/// 兩條彙總排在逐筆輔助訊號與可選報告之前，2026-10-14 起改由 <c>tools/tests/frontend-journey-step4.test.cjs</c> 核對。
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
                // 使用者 2026-10-14 第三輪勾選頁確認 R3-40（第一次失敗：收據 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e）。
                "尚無預篩選結果，匯出時會先執行預篩選；資料量大時需要數分鐘到十餘分鐘。"
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

    private static string ReadFrontend(params string[] relativeParts) =>
        File.ReadAllText(Path.Combine(
            new[] { AppContext.BaseDirectory, "wwwroot" }.Concat(relativeParts).ToArray()));
}
