using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 步驟五匯出面把 Pre-screening Report 納入預設輸出家族的守衛。
/// 三個語意各鎖一條：預設勾選且可取消；有現行 prescreen run 時匯出序列含 Pre-screening Report；
/// 沒有現行 run 時先明示會補跑，確認後依序 prescreen.run → export.prescreenReport →
/// export.workpaperStream。文案一律鏡射 AuditCore renderer（逐字比對見
/// <see cref="PrescreenPositioningFrontendTests"/>），前端不自行改寫審計定位。
/// </summary>
public sealed class PrescreenDefaultExportFrontendTests
{
    [Fact]
    public void ExportPanel_IncludesPrescreenReportByDefault_AndLetsTheUserCancelIt()
    {
        var source = ReadFrontend("js", "steps", "export-step.js");

        Assert.Contains("var includePrescreenReport = true;", source, StringComparison.Ordinal);
        Assert.Contains("includePrescreenReport = true;", source, StringComparison.Ordinal);

        var option = ExtractFunction(source, "prescreenExportOptionHtml");
        Assert.Contains("data-bind=\"include-prescreen-report\"", option, StringComparison.Ordinal);
        Assert.Contains("includePrescreenReport ? ' checked' : ''", option, StringComparison.Ordinal);

        var toggle = ExtractFunction(source, "bindPrescreenExportOption");
        Assert.Contains("includePrescreenReport = toggle.checked;", toggle, StringComparison.Ordinal);

        // 取消勾選後只少這一份：走純 Working Paper 分支，序列不含 Pre-screening Report。
        var bind = ExtractFunction(source, "bind");
        Assert.Contains("if (!plan.included)", bind, StringComparison.Ordinal);
        Assert.Contains("Ui.run('產生 WorkingPaper'", bind, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportWithCurrentPrescreenRun_ProducesPrescreenReportBeforeTheWorkpaper()
    {
        var source = ReadFrontend("js", "steps", "export-step.js");
        var bind = ExtractFunction(source, "bind");

        var sequence = SequenceBranch(bind);
        var reportIndex = sequence.IndexOf("exportPrescreenReportForRun(sourceRunId)", StringComparison.Ordinal);
        var workpaperIndex = sequence.IndexOf("exportWorkpaper(payload)", StringComparison.Ordinal);
        Assert.True(reportIndex >= 0, "匯出序列必須呼叫 Pre-screening Report 匯出。 ");
        Assert.True(workpaperIndex > reportIndex, "Pre-screening Report 必須排在 Working Paper 之前。 ");

        // 既有 action 重用，不新增 wire。
        Assert.Contains("global.JetApi.exportPrescreenReport({ runId: sourceRunId })", source, StringComparison.Ordinal);
        Assert.Contains("global.JetApi.exportWorkpaperStream(payload)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportWithoutCurrentPrescreenRun_AnnouncesTheCatchUpRun_ThenRunsItFirst()
    {
        var source = ReadFrontend("js", "steps", "export-step.js");

        var plan = ExtractFunction(source, "prescreenExportPlan");
        Assert.Contains(
            "needsRun: includePrescreenReport && !currentRunId && eligibility.isEligible",
            plan,
            StringComparison.Ordinal);

        // 明示：補跑文案來自後端 renderer，且在按下之前就顯示。
        var option = ExtractFunction(source, "prescreenExportOptionHtml");
        Assert.Contains("plan.positioning.exportPendingRunGuidance", option, StringComparison.Ordinal);
        Assert.Contains("data-bind=\"prescreen-export-notice\"", option, StringComparison.Ordinal);

        var label = ExtractFunction(source, "exportButtonLabel");
        Assert.Contains("plan.needsRun", label, StringComparison.Ordinal);
        Assert.Contains("先執行預篩選並產生底稿", label, StringComparison.Ordinal);

        // 一鍵確認後的序列：prescreen.run 先於報告與底稿。
        var sequence = SequenceBranch(ExtractFunction(source, "bind"));
        Assert.Contains("plan.needsRun ? runPrescreenForExport() : plan.currentRunId", sequence, StringComparison.Ordinal);
        var runIndex = sequence.IndexOf("runPrescreenForExport()", StringComparison.Ordinal);
        var reportIndex = sequence.IndexOf("exportPrescreenReportForRun(sourceRunId)", StringComparison.Ordinal);
        Assert.True(runIndex >= 0 && reportIndex > runIndex, "補跑必須排在報告匯出之前。 ");

        var run = ExtractFunction(source, "runPrescreenForExport");
        Assert.Contains("global.JetApi.prescreenRun({})", run, StringComparison.Ordinal);
        Assert.Contains("Store.setLastRun('prescreen', data)", run, StringComparison.Ordinal);

        // 完整性不適格時無法補跑：勾選不生效，只產出 Working Paper。
        Assert.Contains(
            "included: includePrescreenReport && (!!currentRunId || eligibility.isEligible)",
            plan,
            StringComparison.Ordinal);
    }

    /// <summary>取 bind 內「含 Pre-screening Report」那一段序列（純 Working Paper 分支在它之前）。</summary>
    private static string SequenceBranch(string bind)
    {
        var marker = "Ui.run('產生底稿與 Pre-screening Report'";
        var start = bind.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到含 Pre-screening Report 的匯出序列。 ");
        return bind[start..];
    }

    private static string ReadFrontend(params string[] relativeParts) =>
        File.ReadAllText(Path.Combine(
            new[] { AppContext.BaseDirectory, "wwwroot" }.Concat(relativeParts).ToArray()));

    private static string ExtractFunction(string source, string functionName)
    {
        var marker = "  function " + functionName + "(";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到前端函式 {functionName}。");

        var next = source.IndexOf("\n  function ", start + marker.Length, StringComparison.Ordinal);
        var end = next >= 0 ? next : source.Length;
        return source[start..end];
    }
}
