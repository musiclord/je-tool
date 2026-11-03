using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 工作底稿 Step 1-2 的空白人員組和預篩選報告、畫面一樣寫「（空白）」。
/// 列投影與資料庫無關：空白組的鍵不論是空字串或只有空白字元，都顯示同一個詞。
/// oracle：預篩選報告既有的顯示詞「（空白）」。
/// </summary>
public sealed class WorkingPaperPreparerRowTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ProjectPreparerRow_BlankPreparer_ShowsTheBlankLabel(string createdBy)
    {
        var cells = WorkingPaperWriter.ProjectPreparerRow(new PreparerSummaryExportRow(createdBy, 3, 30000, 0), moneyScale: 2);

        Assert.Equal("（空白）", cells[0].Text);
        Assert.Equal("（空白）", cells[0].RenderedText);
    }

    [Fact]
    public void ProjectPreparerRow_NamedPreparer_KeepsTheName()
    {
        var cells = WorkingPaperWriter.ProjectPreparerRow(new PreparerSummaryExportRow("合成人員甲", 1, 100, 0), moneyScale: 2);

        Assert.Equal("合成人員甲", cells[0].Text);
    }
}
