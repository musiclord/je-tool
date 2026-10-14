using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using JET.Bridge;

namespace Jet.Harness.TaiwanSme;

/// <summary>
/// 瀏覽器主機的台灣中小企業案件種子：同一份總帳與試算表建立三個案件。
/// SQLite 案件存 KCT 十項情境，DuckDB 案件存十項自訂情境，兩者都走完六個步驟並匯出底稿；
/// 第三個案件只匯入檔案，停在欄位配對。比對結果寫在主機資料夾的 seed-report。
/// </summary>
internal static class TaiwanSmeSeeder
{
    public const string KctCase = "台灣中小企業-KCT 情境（SQLite）";
    public const string CustomCase = "台灣中小企業-自訂情境（DuckDB）";
    public const string MappingCase = "台灣中小企業-待配對（SQLite）";

    public static async Task<bool> EnsureSeededAsync(ActionDispatcher dispatcher, string projectsRoot, string runRoot)
    {
        if (Directory.EnumerateFileSystemEntries(projectsRoot).Any())
        {
            Console.WriteLine("[seed] 沿用既有的台灣中小企業案件；要重建請以 --reset 啟動。");
            return true;
        }

        var stopwatch = Stopwatch.StartNew();
        var smeCase = SmeCaseGenerator.Generate();
        var statistics = Statistics(smeCase);
        Console.WriteLine(statistics);
        var files = SmeWorkbookWriter.Write(smeCase, Path.Combine(runRoot, "source-files"));
        Console.WriteLine($"[seed] 已寫出來源檔（{stopwatch.Elapsed.TotalSeconds:0.0} 秒）。");

        // 和瀏覽器 bridge 相同的 Web 命名規則序列化回應，比對時讀到的欄位名稱和畫面收到的一樣。
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        SmeDispatch dispatch = async (action, payload) =>
        {
            var result = await dispatcher.DispatchAsync(action, payload, CancellationToken.None).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(result, web);
        };
        var oracle = new SmeOracle(smeCase);
        var reportDirectory = Path.Combine(runRoot, "seed-report");
        var logs = new List<SmeCaseLog>();
        async Task<bool> RunAsync(string caseName, Func<SmeCaseLog, Task> body)
        {
            Console.WriteLine($"[seed] 建立 {caseName} …");
            var caseWatch = Stopwatch.StartNew();
            var log = new SmeCaseLog(caseName, Path.Combine(reportDirectory, Sanitize(caseName)), Console.WriteLine);
            logs.Add(log);
            try
            {
                await body(log).ConfigureAwait(false);
                var comparison = log.MatchedCount == 0 && log.AllMatched ? string.Empty : $"，比對 {log.Summary}";
                Console.WriteLine($"[seed] {caseName} 完成（{caseWatch.Elapsed.TotalSeconds:0} 秒）{comparison}。");
            }
            catch (Exception exception)
            {
                log.Fail(exception);
                await TryReleaseAsync(dispatch).ConfigureAwait(false);
            }

            return log.AllMatched;
        }

        var ok = await RunAsync(KctCase, log => SmePipeline.RunCompleteAsync(
            dispatch, KctCase, "TW-SME-KCT", "sqlite", files, oracle, SmeScenarios.Kct, log)).ConfigureAwait(false);
        ok &= await RunAsync(CustomCase, log => SmePipeline.RunCompleteAsync(
            dispatch, CustomCase, "TW-SME-CUSTOM", "duckdb", files, oracle, SmeScenarios.Custom, log)).ConfigureAwait(false);
        ok &= await RunAsync(MappingCase, log => SmePipeline.RunMappingPendingAsync(
            dispatch, MappingCase, "TW-SME-MAP", files, log)).ConfigureAwait(false);

        Directory.CreateDirectory(reportDirectory);
        var text = new StringBuilder();
        text.AppendLine(ok ? "結果：全部步驟完成，所有比對一致。" : "結果：有步驟失敗或比對不一致，見下方。");
        text.AppendLine(statistics);
        foreach (var log in logs)
        {
            text.AppendLine();
            text.AppendLine(log.ToString());
        }

        File.WriteAllText(Path.Combine(reportDirectory, "summary.txt"), text.ToString(), new UTF8Encoding(true));
        Console.WriteLine($"[seed] 台灣中小企業案件{(ok ? "全部完成" : "有步驟失敗或比對不一致")}，共 {stopwatch.Elapsed.TotalSeconds:0} 秒；"
            + $"比對結果見 {Path.GetRelativePath(runRoot, reportDirectory)}。");
        return ok;
    }

    public static string Statistics(SmeCase smeCase)
    {
        var population = smeCase.Population.ToList();
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"[seed] 總帳匯出 {smeCase.Lines.Count:N0} 列、{smeCase.Lines.Select(l => l.VoucherNo).Distinct().Count():N0} 張傳票；"
            + $"查核期間內已過帳 {population.Count:N0} 列、{population.Select(l => l.VoucherNo).Distinct().Count():N0} 張。");
        builder.AppendLine(CultureInfo.InvariantCulture, $"[seed] 試算表 {smeCase.Accounts.Count} 個科目，總帳實際使用 {population.Select(l => l.AccountCode).Distinct().Count()} 個。");
        var balances = smeCase.Accounts.ToDictionary(a => a.Code, a => a.Opening);
        foreach (var line in population)
        {
            balances[line.AccountCode] += line.Debit - line.Credit;
        }

        string Show(string code) => $"{code} {smeCase.Accounts.Single(a => a.Code == code).Name} {balances[code]:N0}";
        builder.AppendLine("[seed] 期末餘額抽看：" + string.Join("；", new[]
        {
            "111301", "111302", "111303", "111304", "111305", "1170", "1171", "1232", "1234", "1235", "2170", "2101", "1269",
        }.Select(Show)));
        var revenue = population.Where(l => l.AccountCode.StartsWith('4')).Sum(l => l.Credit - l.Debit);
        var costs = population.Where(l => l.AccountCode[0] is '5' or '6' or '7' or '8').Sum(l => l.Debit - l.Credit);
        builder.Append(CultureInfo.InvariantCulture, $"[seed] 營業收入淨額 {revenue:N0}，成本費用與營業外淨額 {costs:N0}，稅後淨利 {revenue - costs:N0}。");
        var negative = balances.Where(pair => pair.Key.StartsWith("111", StringComparison.Ordinal) && pair.Value < 0m).ToList();
        if (negative.Count > 0)
        {
            builder.Append(" 期末為負的銀行帳戶：" + string.Join("、", negative.Select(pair => pair.Key)));
        }

        return builder.ToString();
    }

    private static async Task TryReleaseAsync(SmeDispatch dispatch)
    {
        try
        {
            await dispatch("project.releaseLock", JsonSerializer.SerializeToElement(new { })).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 建案前就失敗時沒有可釋放的鎖。
        }
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}
