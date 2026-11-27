using System.Reflection;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 動作成功後案件要推進到哪一步，由推進它的 handler 自己宣告（常數 AdvancesTo，型別是步驟列舉 WorkflowStep），
/// 不用動作名字串在另一張表查。改了動作名或漏登錄，建置與這則測試會先擋下，不是執行到才丟例外。
/// </summary>
public sealed class WorkflowMilestoneOwnershipTests
{
    [Fact]
    public void WorkflowSteps_AreDeclaredByTheHandlersThatAdvanceThem()
    {
        Assert.Null(ProductSources.ProductType("JET.AuditCore.WorkflowMilestones"));

        // 列舉的值是畫面步驟從 0 起算的位置，和案件設定檔的 currentStep 相同。
        var stepType = ProductSources.ProductType("JET.Domain.WorkflowStep");
        Assert.True(stepType is { IsEnum: true }, "JET.Domain.WorkflowStep 不是列舉。");
        Assert.Equal(
            new[] { "Create", "Import", "Mapping", "Validate", "Filter", "Export" },
            Enum.GetNames(stepType!));
        Assert.Equal(
            new[] { 0, 1, 2, 3, 4, 5 },
            Enum.GetValues(stepType!).Cast<object>().Select(Convert.ToInt32).ToArray());

        var expected = new (string Handler, string Step)[]
        {
            ("JET.Application.ImportGlFromFileHandler", "Mapping"),
            ("JET.Application.ImportTbFromFileHandler", "Mapping"),
            ("JET.Application.MappingCommitGlHandler", "Validate"),
            ("JET.Application.MappingCommitTbHandler", "Validate"),
            ("JET.Application.ValidateRunHandler", "Filter"),
            ("JET.Application.PrescreenRunHandler", "Filter"),
            ("JET.Application.FilterCommitHandler", "Export"),
            ("JET.Application.FilterScenarioSaveHandler", "Export"),
            ("JET.Application.FilterScenarioRemoveHandler", "Export"),
            ("JET.Application.FilterScenarioRecheckHandler", "Export")
        };

        foreach (var (handler, step) in expected)
        {
            var type = ProductSources.ProductType(handler);
            Assert.NotNull(type);
            var field = type!.GetField("AdvancesTo",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            Assert.True(field is not null, handler + " 沒有宣告 AdvancesTo。");
            Assert.Equal(stepType, field!.FieldType);
            Assert.Equal(step, Enum.GetName(stepType!, field.GetRawConstantValue()!));
        }
    }
}
