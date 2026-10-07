using Xunit;
using JET.Tests.Infrastructure;
using System.Text.Json;

namespace JET.Tests.Architecture;

/// <summary>
/// Gui 情境的涵蓋範圍：KCT 情境實際操作 B 卡與 A 到 E 的預設動機，匯出情境讀回底稿的人工或自動欄為文字。
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
    [Fact]
    public void ExistingGuiScenariosExerciseBAndReadBackExportedManualFlags()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tools/harness/gui-driver"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var root = Path.Combine(directory.FullName, "tools/harness/gui-driver");
        var filter = File.ReadAllText(Path.Combine(root, "GuiFilterWorkflowScenarios.cs"));
        Assert.Contains("kct_a_to_e_default_rationales", filter);
        Assert.Contains("draft.groups[0].rules[0].__kctLetter==='B'", filter);
        Assert.Contains("借記固定資產科目(如不動產、廠房和設備(PPE))", filter);
        Assert.Contains("await Check(\"draft.rationale===\" + JsonSerializer.Serialize(letter + \"：\" + rationale))", filter);
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
        Assert.Equal(98, scenario.GetProperty("actionBudget").GetInt32());
        Assert.Equal(98, scenario.GetProperty("expectedActionCount").GetInt32());
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
