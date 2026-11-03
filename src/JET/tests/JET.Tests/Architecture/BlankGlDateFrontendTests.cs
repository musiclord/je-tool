using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 空白過帳日的前端歸屬守衛。2026-08-14 起它只屬於總帳日期空白，不再是資料缺漏輔助檢視的子項；
/// 前端整合階段把這條邊界從「後端已改、前端未接」升級為「兩邊一致」。
/// </summary>
public sealed class BlankGlDateFrontendTests
{
    [Fact]
    public void ValidationUi_DoesNotReadBlankGlDateFromBlankRecords()
    {
        var source = ReadFrontend("js", "steps", "validate-step.js");

        // 舊契約的 count、category 與 issue 鍵都必須消失：留任何一個都會讓畫面
        // 讀到不存在的欄位，或把總帳日期空白重新算進資料缺漏輔助檢視的異常項次合計。
        Assert.DoesNotContain("blankGlDateCount", source, StringComparison.Ordinal);
        Assert.DoesNotContain("nullLoadMoreSpec('blankGlDate'", source, StringComparison.Ordinal);
        // 2026-10-16 移除「載入更多」後，同一個函式改名為 nullPageSpec，新名稱也不得登記空白過帳日。
        Assert.DoesNotContain("nullPageSpec('blankGlDate'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("['blankGlDate']", source, StringComparison.Ordinal);
        Assert.DoesNotContain("glDate: '空白總帳日期'", source, StringComparison.Ordinal);
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
