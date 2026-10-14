using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 只能看原始碼的前端規則。其他前端行為一律由 <c>tools/tests/frontend-journey-*.test.cjs</c> 執行正式腳本來測；
/// 這裡只留行為測試替代不了的少數檢查，每個方法的註解寫明原因。
/// 依據：使用者 2026-10-13 對「147 個字串比對測試行為化」選「照建議做」，選項說明寫明
/// 「少數只能看原始碼的規則，例如整個前端不得出現四位小數的金額格式，保留成很小的原始碼檢查」。
/// </summary>
public sealed class FrontendSourceOnlyRulesTests
{
    [Fact]
    public void MoneyDisplay_NeverUsesFourDecimalPlacesAnywhereInTheFrontend()
    {
        // 2026-10-14 從 ScreenBehaviorReviewFrontendTests.Money_ShowsTwoDecimalsWithThousandsSeparatorThroughOneSharedFormatter 原樣搬來。
        // 使用者 2026-10-02 裁定 P3-4：畫面統一兩位小數加千分位；報表與底稿不變。
        // 為什麼行為測試替代不了：tools/tests/frontend-journey-tables.test.cjs 只檢查已知畫面上的金額，
        // 之後新加的格式化程式不會被涵蓋；整個前端都不得出現四位小數，只能掃描全部原始碼。
        foreach (var file in Directory.GetFiles(Path.Combine(FrontendRoot(), "js"), "*.js", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("minimumFractionDigits: 4", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FilterConditionValues_AreNeverMoneyFormatted()
    {
        // 2026-10-14 從 ScreenBehaviorReviewFrontendTests.Money_ShowsTwoDecimalsWithThousandsSeparatorThroughOneSharedFormatter 原樣搬來。
        // 輸入框與篩選條件值照審計員輸入的樣子顯示，不經過金額格式化。
        // 為什麼行為測試替代不了：條件值的輸入框種類很多，行為測試只測了讀回句子的一種；
        // filter-values.js 任何地方呼叫 Ui.money( 都會改掉審計員打的值，只能看原始碼擋下。
        Assert.DoesNotContain("Ui.money(", ReadFrontend("js", "filter-values.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void DataPreviewCompactBoundary_MatchesTheStylesheetBreakpoint()
    {
        // 2026-10-14 從 FrontendUsabilityContractTests.Preview_Uses1360CompactBoundaryAndPreservesManualChoiceUntilViewportCrossesAgain
        // 原樣搬來 JS 斷點那一條，另加 app.css 同一個斷點（計畫第 5 節「資料預覽收合斷點一致」）。
        // 收合規則本身由 tools/tests/frontend-journey-tables.test.cjs 的
        // 「the preview folds at 1360 pixels and keeps the auditor's own choice…」測。
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
