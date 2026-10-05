using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 空白過帳日的前端歸屬守衛。2026-08-14 起它只屬於來源品質，不再是空值紀錄測試的子項；
/// 前端整合階段把這條邊界從「後端已改、前端未接」升級為「兩邊一致」。
/// </summary>
public sealed class BlankPostDateFrontendTests
{
    [Fact]
    public void ValidationUi_PresentsBlankPostDateAsSourceQualityOnly()
    {
        var source = ReadFrontend("js", "steps", "validate-step.js");

        // 來源品質是 validate.run 的獨立區塊，全量明細走它自己的分頁 action。
        Assert.Contains("v.sourceQuality", source, StringComparison.Ordinal);
        Assert.Contains("querySourceQualityPage", source, StringComparison.Ordinal);
        Assert.Contains("LOAD_MORE_SPECS.sourceQuality", source, StringComparison.Ordinal);
        // C8 follows the user's D21 word order; the source-quality boundary and action stay unchanged.
        // First failure: 20261004-091831760-a762b9ef96db4d61a4bb2bff7cdb0022.
        Assert.Contains("category === 'nullPostDate' ? '總帳入帳日空白'", source, StringComparison.Ordinal);
        Assert.Contains("loadMore: 'sourceQuality'", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationUi_DoesNotReadBlankPostDateFromNullRecords()
    {
        var source = ReadFrontend("js", "steps", "validate-step.js");

        // 舊契約的 count、category 與 issue 鍵都必須消失：留任何一個都會讓畫面
        // 讀到不存在的欄位，或把空白過帳日重新算進空值紀錄的異常項次合計。
        Assert.DoesNotContain("nullPostDateCount", source, StringComparison.Ordinal);
        Assert.DoesNotContain("nullLoadMoreSpec('nullPostDate'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("['nullPostDate']", source, StringComparison.Ordinal);
        Assert.DoesNotContain("postDate: '空白總帳入帳日'", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationUi_ReportsSourceQualityCountSeparatelyFromNullRecordTotal()
    {
        var source = ReadFrontend("js", "steps", "validate-step.js");

        // 2026-10-02 整體複審 T11：「異常項次合計」改名「空值項目合計（同一分錄可能重複計入）」；舊名稱不得再出現。
        Assert.Contains("空值項目合計（同一分錄可能重複計入）", source, StringComparison.Ordinal);
        Assert.DoesNotContain("異常項次合計", source, StringComparison.Ordinal);
        Assert.Contains("同一分錄可能重複計入", source, StringComparison.Ordinal);
        Assert.Contains("獨立計數，不與上一項相加", source, StringComparison.Ordinal);
        Assert.Contains("data.sourceQuality ? data.sourceQuality.findingCount : 0", source, StringComparison.Ordinal);
    }

    private static string ReadFrontend(params string[] segments)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot(), "JET", "wwwroot" }.Concat(segments).ToArray()));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("向上尋不到 JET.slnx。");
    }
}
