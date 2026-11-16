using JET.Application;
using JET.Domain;
using JET.Tests.TestInfrastructure;
using System.Text.Json;
using System.Text;
using Xunit;

namespace JET.Tests.Architecture;

public sealed partial class TerminologyUnificationFrontendTests
{
    // 這四項讀方案資料夾以外的桌面情境程式，
    // 所以獨立成一個測試並加上標記，只有方案資料夾時可以排除。
    [Fact]
    [Trait(TestProfileTraits.Key, TestProfileTraits.DevelopmentRepository)]
    public void GuiScenarioChecks_UseTheSameSaveWordingAsTheProduct()
    {
        // 桌面情境檢查跟著產品字樣改；舊字樣留在檢查裡會讓情境永遠找不到提示。
        var guiScenarios = ReadRepoFile("tools", "harness", "gui-driver", "GuiScenarios.cs");
        Assert.Contains("配對已修改，請按「重新確認配對」儲存。", guiScenarios, StringComparison.Ordinal);
        var guiFilter = StripComments(ReadRepoFile("tools", "harness", "gui-driver", "GuiFilterWorkflowScenarios.cs"), js: false);
        Assert.Contains(".Contains(\"已儲存\", StringComparison.Ordinal)", guiFilter, StringComparison.Ordinal);
        Assert.Contains("textContent.includes('已儲存')", guiFilter, StringComparison.Ordinal);
        Assert.DoesNotContain("已保存", guiFilter, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] segments)
        => File.ReadAllText(Path.Combine(new[] { SolutionRoot(), "..", ".." }.Concat(segments).ToArray()));
}
