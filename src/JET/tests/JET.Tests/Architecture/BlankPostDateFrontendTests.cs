using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 空白過帳日的前端歸屬守衛。2026-08-14 起它只屬於來源品質，不再是空值紀錄測試的子項；
/// 前端整合階段把這條邊界從「後端已改、前端未接」升級為「兩邊一致」。
/// </summary>
public sealed class BlankPostDateFrontendTests
{
    [Fact]
    public void ValidationUi_DoesNotReadBlankPostDateFromNullRecords()
    {
        var source = ReadFrontend("js", "steps", "validate-step.js");

        // 舊契約的 count、category 與 issue 鍵都必須消失：留任何一個都會讓畫面
        // 讀到不存在的欄位，或把空白過帳日重新算進空值紀錄的異常項次合計。
        Assert.DoesNotContain("nullPostDateCount", source, StringComparison.Ordinal);
        Assert.DoesNotContain("nullLoadMoreSpec('nullPostDate'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("['nullPostDate']", source, StringComparison.Ordinal);
        Assert.DoesNotContain("postDate: '空白總帳日期'", source, StringComparison.Ordinal);
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
