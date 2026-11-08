using System.Text.Json;
using Jet.Harness.TaiwanSme;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 一整年的合成台灣中小型製造業總帳（一萬一千多張傳票、三萬多筆分錄）與 200 個科目的試算表，
/// 經正式 action 走完建案、匯入、配對、分類、驗證、預篩選、篩選與匯出。驗證摘要 14 項、預篩選 12 項
/// 與 10 個情境的命中，逐筆和產生器依規則文字獨立算出的答案比對（SmeOracle 只用 LINQ，不呼叫 JET 的計算）。
/// 同一份資料也是 Claude 瀏覽器主機的檢查案件，原始檔在 tools/harness/browser-host/TaiwanSme。
/// </summary>
public sealed class TaiwanSmeLargeCaseTests
{
    private static readonly Lazy<(SmeCase Case, SmeSourceFiles Files, SmeOracle Oracle)> Fixture = new(() =>
    {
        var smeCase = SmeCaseGenerator.Generate();
        var directory = Path.Combine(Path.GetTempPath(), "jet-taiwan-sme", Guid.NewGuid().ToString("N"));
        // 測試程序結束時刪掉這次寫出的來源檔，避免每次執行都在暫存資料夾留下約 2 MB。
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 清不掉只會留下合成檔案，不影響測試結果。
            }
        };
        return (smeCase, SmeWorkbookWriter.Write(smeCase, directory), new SmeOracle(smeCase));
    });

    [Fact]
    public void GeneratedCase_IsAYearOfBalancedVouchersWithTwoHundredTbAccounts()
    {
        var (smeCase, _, oracle) = Fixture.Value;

        // 依裁定 2026-10-11-01，合成案件至少要有 1 萬張傳票，以及 200 個科目的試算表。
        Assert.True(oracle.Population.Select(line => line.VoucherNo).Distinct().Count() >= 10_000);
        Assert.Equal(200, smeCase.Accounts.Count);
        // 期初試算表平衡；GL 內每張傳票借貸相等，所以期末試算表也平衡。
        Assert.Equal(0m, smeCase.Accounts.Sum(account => account.Opening));
        Assert.All(
            oracle.Population.GroupBy(line => line.VoucherNo),
            voucher => Assert.Equal(voucher.Sum(line => line.Debit), voucher.Sum(line => line.Credit)));
        // 匯出檔另含期間外與未過帳的列，JET 應列入 GL(未納入測試)。
        Assert.Contains(smeCase.Lines, line => !line.InPopulation && line.IsPosted);
        Assert.Contains(smeCase.Lines, line => !line.IsPosted);
    }

    [Theory]
    [InlineData("sqlite", "common")]
    // 原本自訂的十個情境由 DuckDB 測；只保留 SQLite 之後改由 SQLite 測，情境不變（裁定 2026-11-06-08）。
    [InlineData("sqlite", "custom")]
    public async Task FullWorkflow_MatchesIndependentlyComputedAnswers(string provider, string scenarioSet)
    {
        var (_, files, oracle) = Fixture.Value;
        using var host = new HandlerTestHost();
        SmeDispatch dispatch = (action, payload) => host.DispatchAsync(action, payload.GetRawText());
        var log = new SmeCaseLog($"{provider} {scenarioSet}");

        await SmePipeline.RunCompleteAsync(
            dispatch,
            "台灣中小企業測試",
            "TW-SME-TEST",
            provider,
            files,
            oracle,
            scenarioSet == "common" ? SmeScenarios.Common : SmeScenarios.Custom,
            log);

        // 失敗時訊息列出每一項的預期與 JET 結果，以及雙方各自多出來的前五筆分錄。
        Assert.True(log.AllMatched, log.ToString());
        // 驗證摘要 14 項、預篩選 10 項、情境 10 個，缺任何一項都代表比對沒有完整執行。
        // 裁定 2026-10-29-27：第四步照舊版拿掉編製分錄較少的人員與使用較少的科目，預篩選由 12 項減為 10 項；
        // 原本是 36，第一次失敗：Focused 收據 20261030-165407068-719c351124b94328bcd2dc27ed541d5b（其餘比對全部相符，只有項數差 2）。
        Assert.Equal(34, log.MatchedCount);
    }
}
