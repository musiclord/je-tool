using JET.AuditCore;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class WorkflowMilestonesTests
{
    // 數值逐字承接原本主線節點與既有自動推進步驟的對照；project.create 不經這張表推進，所以不列入。
    [Theory]
    [InlineData("import.gl.fromFile", 2)]
    [InlineData("import.tb.fromFile", 2)]
    [InlineData("mapping.commit.gl", 3)]
    [InlineData("mapping.commit.tb", 3)]
    [InlineData("validate.run", 4)]
    [InlineData("prescreen.run", 4)]
    [InlineData("filter.commit", 5)]
    // 2026-10-13 新增案例（既有案例不變）：一次只存、刪或重新檢查一個情境的三個動作，和 filter.commit 一樣推進到第五步。
    [InlineData("filter.scenario.save", 5)]
    [InlineData("filter.scenario.remove", 5)]
    [InlineData("filter.scenario.recheck", 5)]
    public void For_MatchesExistingAutomaticProgress(string action, int expectedMilestone)
    {
        Assert.Equal(expectedMilestone, WorkflowMilestones.For(action));
    }

    [Fact]
    public void For_ActionWithoutAutomaticProgress_IsRejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            WorkflowMilestones.For("query.dataPreview"));

        Assert.Contains("沒有登錄", exception.Message, StringComparison.Ordinal);
    }
}
