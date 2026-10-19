using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 低嚴重度事項 D2（使用者 2026-10-19 要求處理）：工作底稿 Step 1-2 的空白人員組和預篩選報告、畫面一樣寫「（空白）」。
/// 列投影與資料庫無關：空白組的鍵不論是空字串或只有空白字元，都顯示同一個詞。
/// oracle：預篩選報告既有的顯示詞「（空白）」。
/// </summary>
public sealed class WorkpaperCreatorRowTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ProjectCreatorRow_BlankCreator_ShowsTheBlankLabel(string createdBy)
    {
        var cells = WorkpaperWriter.ProjectCreatorRow(new CreatorSummaryExportRow(createdBy, 3, 30000, 0), moneyScale: 2);

        Assert.Equal("（空白）", cells[0].Text);
        Assert.Equal("（空白）", cells[0].RenderedText);
    }

    [Fact]
    public void ProjectCreatorRow_NamedCreator_KeepsTheName()
    {
        var cells = WorkpaperWriter.ProjectCreatorRow(new CreatorSummaryExportRow("合成人員甲", 1, 100, 0), moneyScale: 2);

        Assert.Equal("合成人員甲", cells[0].Text);
    }
}
