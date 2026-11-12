using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 空白過帳日的前端歸屬守衛。它只屬於總帳日期空白，不是資料缺漏輔助檢視的子項；
/// 前端與後端在這條邊界上一致。
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
        // 函式的舊名稱 nullPageSpec 也不得登記空白過帳日。
        Assert.DoesNotContain("nullPageSpec('blankGlDate'", source, StringComparison.Ordinal);
        // 函式目前的名稱是 blankPageSpec，同樣不得登記空白過帳日。
        Assert.DoesNotContain("blankPageSpec('blankGlDate'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("['blankGlDate']", source, StringComparison.Ordinal);
        Assert.DoesNotContain("glDate: '空白總帳日期'", source, StringComparison.Ordinal);
    }

    private static string ReadFrontend(params string[] segments)
        => File.ReadAllText(Path.Combine(new[] { SolutionRoot(), "JET", "wwwroot" }.Concat(segments).ToArray()));

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("向上尋不到 JET.slnx。");
    }
}
