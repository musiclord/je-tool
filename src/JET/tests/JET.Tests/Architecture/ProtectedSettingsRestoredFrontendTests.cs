using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 開案時案件設定檔和案件資料庫記的設定不同，project.load 回應 settingsRestored 列出已改回的項目。
/// 畫面在訊息面板記一則需要注意的訊息，項目照畫面用字列出；訊息歷史還原會整批取代畫面訊息，所以放在還原之後。
/// </summary>
public sealed class ProtectedSettingsRestoredFrontendTests
{
    [Fact]
    public void OpeningACaseWithRestoredSettings_ListsTheItemsInScreenWords()
    {
        var core = Script("ui-core.js");
        foreach (var (item, label) in new[]
                 {
                     ("periodStart", "查核起始日"), ("periodEnd", "查核截止日"),
                     ("prepStartDate", "期末財報準備日"), ("nonWorkingDays", "每週休息日"),
                     ("operatorId", "建案者"), ("sampleSeed", "隨機種子序號"),
                     ("moneyScale", "金額存放倍率"), ("rocDateEnabled", "民國年選項")
                 })
        {
            Assert.Contains($"{item}: '{label}'", core, StringComparison.Ordinal);
        }

        Assert.Contains(
            "return '案件設定檔裡的' + labels.join('、') + '和案件資料庫記錄的不同，JET 已照案件資料庫改回。';",
            core,
            StringComparison.Ordinal);

        var seeded = core.IndexOf("Store.seedMessages(log.messages || []);", StringComparison.Ordinal);
        var restored = core.IndexOf("var restored = settingsRestoredMessage(data.settingsRestored);", StringComparison.Ordinal);
        Assert.True(seeded >= 0 && restored > seeded, "改回設定的提醒要放在訊息歷史還原之後");
        Assert.Contains("if (restored) { Store.addMessage(restored, 'warn'); }", core, StringComparison.Ordinal);
    }

    private static string Script(params string[] parts)
        => TerminologyUnificationFrontendTests.StripComments(ProductSources.Frontend(parts), js: true);
}
