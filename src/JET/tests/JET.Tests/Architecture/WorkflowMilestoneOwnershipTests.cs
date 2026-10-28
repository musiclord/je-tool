using System.Reflection;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 動作成功後案件要推進到第幾步，由推進它的 handler 自己宣告（常數 WorkflowStep），不再用動作名字串在另一張表查
/// （技術債一輪，2026-10-22）。改了動作名或漏登錄，建置與這則測試會先擋下，不是執行到才丟例外。
/// </summary>
public sealed class WorkflowMilestoneOwnershipTests
{
    [Fact]
    public void WorkflowSteps_AreDeclaredByTheHandlersThatAdvanceThem()
    {
        Assert.Null(ProductSources.ProductType("JET.AuditCore.WorkflowMilestones"));

        var expected = new (string Handler, int Step)[]
        {
            ("JET.Application.ImportGlFromFileHandler", 2),
            ("JET.Application.ImportTbFromFileHandler", 2),
            ("JET.Application.MappingCommitGlHandler", 3),
            ("JET.Application.MappingCommitTbHandler", 3),
            ("JET.Application.ValidateRunHandler", 4),
            ("JET.Application.PrescreenRunHandler", 4),
            ("JET.Application.FilterCommitHandler", 5),
            ("JET.Application.FilterScenarioSaveHandler", 5),
            ("JET.Application.FilterScenarioRemoveHandler", 5),
            ("JET.Application.FilterScenarioRecheckHandler", 5)
        };

        foreach (var (handler, step) in expected)
        {
            var type = ProductSources.ProductType(handler);
            Assert.NotNull(type);
            var field = type!.GetField("WorkflowStep",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            Assert.True(field is not null, handler + " 沒有宣告 WorkflowStep。");
            Assert.Equal(step, field!.GetRawConstantValue());
        }
    }
}
