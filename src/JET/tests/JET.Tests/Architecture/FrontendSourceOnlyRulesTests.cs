using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 只能看原始碼的前端規則。其他前端行為一律由前端測試執行正式腳本來測；
/// 這裡只留行為測試替代不了的少數檢查，每個方法的註解寫明原因。
/// 例如整個前端不得出現四位小數的金額格式，這類規則保留成很小的原始碼檢查。
/// </summary>
public sealed class FrontendSourceOnlyRulesTests
{
    [Fact]
    public void MoneyDisplay_NeverFormatsNumbersToFourDecimalPlaces()
    {
        // 畫面統一兩位小數加千分位；報表與底稿不變。
        // 為什麼行為測試替代不了：前端測試只檢查已知畫面上的金額，
        // 之後新加的格式化程式不會被涵蓋；整個前端都不得出現四位小數，只能掃描全部原始碼。
        // 右側資料預覽的金額固定顯示四位，其他畫面不變。
        // data-preview.js 的 previewMoney 照後端給的十進位字串補零，不把金額轉成數字再格式化，所以不會碰到這個掃描；
        // 這裡擋住的是其他畫面用數字格式化出四位小數。
        foreach (var file in Directory.GetFiles(Path.Combine(FrontendRoot(), "js"), "*.js", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("minimumFractionDigits: 4", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FilterConditionValues_AreNeverMoneyFormatted()
    {
        // 輸入框與篩選條件值照審計員輸入的樣子顯示，不經過金額格式化。
        // 為什麼行為測試替代不了：條件值的輸入框種類很多，行為測試只測了讀回句子的一種；
        // filter-values.js 任何地方呼叫 Ui.money( 都會改掉審計員打的值，只能看原始碼擋下。
        Assert.DoesNotContain("Ui.money(", ReadFrontend("js", "filter-values.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void DataPreviewCompactBoundary_MatchesTheStylesheetBreakpoint()
    {
        // 核對 JS 的斷點與 app.css 同一個斷點，讓資料預覽在 JS 與 CSS 的收合斷點一致。
        // 收合規則本身由前端測試核對。
        // 為什麼行為測試替代不了：測試環境沒有版面與 CSS 計算，JS 的收合斷點和 app.css 的版面斷點是否同一個數字，只能看原始碼。
        Assert.Contains("matchMedia('(max-width: 1360px)')", ReadFrontend("js", "data-preview.js"), StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 1360px)", ReadFrontend("css", "app.css"), StringComparison.Ordinal);
    }

    private static string ReadFrontend(params string[] segments)
        => File.ReadAllText(Path.Combine(new[] { FrontendRoot() }.Concat(segments).ToArray()));

    private static string FrontendRoot() => Path.Combine(RepoRoot(), "JET", "wwwroot");

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx，無法定位 repo 根。");
    }
}
