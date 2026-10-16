using Xunit;
using JET.Tests.Infrastructure;
using System.Text.Json;

namespace JET.Tests.Architecture;

/// <summary>
/// Gui 情境的涵蓋範圍：匯出情境讀回底稿的人工或自動欄為文字；KCT 卡 A 到 E 的預設動機與 B 卡的條件、
/// 每項常用查核條件的「加入」與加入條件後的選取狀態，2026-10-15 起改由前端點擊測試核對，Gui 不再重複。
/// 不開桌面即可檢查 Gui 程式與操作次數設定，Gui 本身仍只在提交或交付前執行。
/// </summary>
public sealed class GuiScenarioCoverageTests
{
    [Theory]
    [InlineData("人工", "自動", true)]
    [InlineData("1", "0", false)]
    [InlineData("", "", false)]
    [InlineData("人工", "人工", false)]
    public void GuiWorkbookReaderRequiresBothTextFlags(string manual, string automatic, bool valid)
    {
        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Name = "step4-1 符合高風險條件傳票明細";
            ws.Cell(5, 1).Value = "人工傳票否_JE_S";
            ws.Cell(6, 1).Value = manual;
            ws.Cell(7, 1).Value = automatic;
            ws.Cell(8, 2).Value = "Synthetic blank flag";
        });
        try
        {
            void Read() => Jet.GuiDriver.GuiWorkpaperInspection.RequireManualAutoText(path, Path.GetDirectoryName(path)!);
            if (valid) Read(); else Assert.Throws<InvalidDataException>(Read);
            Assert.Throws<InvalidDataException>(() => Jet.GuiDriver.GuiWorkpaperInspection.RequireManualAutoText(path, Path.Combine(Path.GetDirectoryName(path)!, "other")));
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }
    // 原名 ExistingGuiScenariosExerciseBAndReadBackExportedManualFlags，原本斷言 Gui 程式逐張點 A 到 E、核對動機與 B 卡條件。
    // 使用者 2026-10-15 在待決事項勾選頁選 S5（另有 S2、S4）：這些核對搬到前端點擊測試，刪掉 Gui 的 10 次操作。
    // 改成確認前端測試仍在、Gui 不再重複這段。首次失敗：Focused 20261015-075841706-f76d7d50908a45bca6e8ef478cb1e041。
    [Fact]
    public void KctCardsAndCommonChecksAreClickedInFrontendTestsAndGuiReadsBackExportedManualFlags()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tools/harness/gui-driver"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var root = Path.Combine(directory.FullName, "tools/harness/gui-driver");
        var filter = File.ReadAllText(Path.Combine(root, "GuiFilterWorkflowScenarios.cs"));
        Assert.DoesNotContain("kct_a_to_e_default_rationales", filter);
        var filterBuild = File.ReadAllText(Path.Combine(directory.FullName, "tools/tests/frontend-journey-filter-build.test.cjs"));
        Assert.Contains("every common check is added by its own button with the exact type, opens only its category and can be removed", filterBuild);
        Assert.Contains("a condition added from the custom pane stays selected after every pending timer has run", filterBuild);
        var export = File.ReadAllText(Path.Combine(root, "GuiSideMonthWorkflow.cs"));
        Assert.Contains("GuiWorkpaperInspection.RequireManualAutoText", export);
        using var lanes = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "tools/harness/lanes.json")));
        var scenario = FindKct(lanes.RootElement);
        // 使用情境補測後搬移單次修改段落；首次失敗 8bf859309a2c475d9f56ac3d92c6c7a4。
        Assert.DoesNotContain("kct_duplicate_name_suffix", filter);
        Assert.DoesNotContain("matrix-after-copy", filter);
        Assert.DoesNotContain("matrix-after-remove", filter);
        var frontend = File.ReadAllText(Path.Combine(directory.FullName, "tools/tests/frontend-workflow.test.cjs"));
        Assert.Contains("F1 an automatic KCT name already used by a saved scenario gets a number and saves", frontend);
        Assert.Contains("copying identical conditions and removing the copy resets and reloads matrix columns", frontend);
        Assert.Contains("clicking each KCT card A to E alone fills its own rationale, B adds its account category pair, and clicking again removes it", frontend);
        // 使用者 2026-10-08 要求重新設計自訂篩選條件：加條件改點欄位按鈕與常用查核條件，操作從 98 次減為 93 次。
        // 首次失敗：Public 20261008-065804895-15c22e99fade4191aea38be9769c0523。93 由情境腳本逐步推算，待 Gui 實跑確認。
        // 2026-10-14 Gui 實跑為 94 次：開啟已存情境時草稿還有未儲存的內容，JET 先詢問，情境選「不儲存，直接開啟」多點一次。
        // 實跑收據 20261014-065005278-01f5c7c0fe31483b88e91d5a518a03f2。
        // 2026-10-15 Gui 改到另一個桌面後實跑為 86 次：803d538 沒有執行 Gui 就改了情境步驟，本輪又拿掉 10-14 停用的
        // 「含正負號」金額那一步；各階段的比對記在 docs/history/specs/2026-10-15-motion-and-verification-plan.md「目前進度」。
        // 首次失敗：Focused 20261015-044801837-e00e67a1be2e42aaa9258bb069133c4d。
        // 完整 Gui 實跑收據 20261015-043313016-282c137bcba34a44870540029ef333c0。
        // 2026-10-15 S5 刪掉逐張點 A 到 E 的 10 次操作，減為 76 次（首次失敗收據同上方註解），待完整 Gui 實跑確認。
        Assert.Equal(76, scenario.GetProperty("actionBudget").GetInt32());
        Assert.Equal(76, scenario.GetProperty("expectedActionCount").GetInt32());
    }

    private static JsonElement FindKct(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("name", out var name) && name.GetString() == "filter-kct-editing") return value;
            foreach (var property in value.EnumerateObject()) { var found = FindKct(property.Value); if (found.ValueKind != JsonValueKind.Undefined) return found; }
        }
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) { var found = FindKct(child); if (found.ValueKind != JsonValueKind.Undefined) return found; }
        return default;
    }
}
