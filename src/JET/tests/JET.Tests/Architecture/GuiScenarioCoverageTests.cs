using Xunit;
using JET.Tests.Infrastructure;
using System.Text.Json;
using JET.Tests.TestInfrastructure;

namespace JET.Tests.Architecture;

/// <summary>
/// 桌面情境的涵蓋範圍：匯出情境讀回底稿的人工或自動欄為文字；常用篩選條件前五張卡的預設動機與固定資產那張卡的條件、
/// 每項依審計目的條件的「加入」與加入條件後的選取狀態，由前端點擊測試核對，桌面情境不重複。
/// 不開桌面即可檢查桌面情境的程式與操作次數設定。
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
            void Read() => Jet.GuiDriver.GuiWorkingPaperInspection.RequireManualAutoText(path, Path.GetDirectoryName(path)!);
            if (valid) Read(); else Assert.Throws<InvalidDataException>(Read);
            Assert.Throws<InvalidDataException>(() => Jet.GuiDriver.GuiWorkingPaperInspection.RequireManualAutoText(path, Path.Combine(Path.GetDirectoryName(path)!, "other")));
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }
    // 確認前端測試涵蓋這些點擊，桌面情境不重複這一段。
    [Fact]
    [Trait(TestProfileTraits.Key, TestProfileTraits.DevelopmentRepository)]
    public void CommonCardsAndFieldShortcutsAreClickedInFrontendTestsAndGuiReadsBackExportedManualFlags()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tools/harness/gui-driver"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var root = Path.Combine(directory.FullName, "tools/harness/gui-driver");
        var filter = File.ReadAllText(Path.Combine(root, "GuiFilterWorkflowScenarios.cs"));
        // 桌面情境的程式不論前綴，都不能有這兩種階段。
        Assert.DoesNotContain("default_rationales", filter);
        var filterBuild = File.ReadAllText(Path.Combine(directory.FullName, "src/JET/tests/frontend/frontend-journey-filter-build.test.cjs"));
        // 比對 test(' 開頭的名稱：測試真的存在才算，名稱只留在註解時會失敗。
        Assert.Contains("test('every field shortcut sits on its field row, is added by its own button with the exact type and can be removed'", filterBuild);
        Assert.Contains("test('a condition added from the field tab stays selected after every pending timer has run'", filterBuild);
        var export = File.ReadAllText(Path.Combine(root, "GuiSideMonthWorkflow.cs"));
        Assert.Contains("GuiWorkingPaperInspection.RequireManualAutoText", export);
        using var lanes = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "tools/harness/lanes.json")));
        var scenario = FindCommonEditing(lanes.RootElement);
        // 單次修改的段落不在桌面情境的程式裡。
        Assert.DoesNotContain("duplicate_name_suffix", filter);
        Assert.DoesNotContain("matrix-after-copy", filter);
        Assert.DoesNotContain("matrix-after-remove", filter);
        var frontend = File.ReadAllText(Path.Combine(directory.FullName, "src/JET/tests/frontend/frontend-workflow.test.cjs"));
        Assert.Contains("an automatic card name already used by a saved scenario gets a number and saves", frontend);
        Assert.Contains("copying identical conditions and removing the copy resets and reloads the high-risk summary columns", frontend);
        Assert.Contains("clicking each card with a default rationale alone fills its own rationale, the fixed-asset card adds its account category pair, and clicking again removes it", frontend);
        // 情境的操作次數是 77 次：開啟已存情境時草稿還有未儲存的內容，JET 先詢問，情境選「不儲存，直接開啟」多點一次；
        // 預篩選條件從條件總覽的「預篩選」分頁直接加入；有常用查法的欄位的一般條件收在「加入」打開的選單，
        // 加入金額與摘要各要先按一次「加入」。
        Assert.Equal(77, scenario.GetProperty("actionBudget").GetInt32());
        Assert.Equal(77, scenario.GetProperty("expectedActionCount").GetInt32());
    }

    private static JsonElement FindCommonEditing(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("name", out var name) && name.GetString() == "filter-common-editing") return value;
            foreach (var property in value.EnumerateObject()) { var found = FindCommonEditing(property.Value); if (found.ValueKind != JsonValueKind.Undefined) return found; }
        }
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) { var found = FindCommonEditing(child); if (found.ValueKind != JsonValueKind.Undefined) return found; }
        return default;
    }
}
