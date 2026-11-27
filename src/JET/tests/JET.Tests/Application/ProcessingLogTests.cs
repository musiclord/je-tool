using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 給使用者看的處理紀錄：GL 與 TB 各一份文字檔，放在案件資料夾。每一次處理完成後，依時間順序記下步驟名稱、時間、
/// 執行人員、用到的條件與處理後的筆數，寫法參考舊版的歷程紀錄。紀錄由獨立的模組讀操作的輸入與回應寫出，
/// 不參與審計判斷；寫不進檔案時操作照常完成，下一次寫入時補上。
/// </summary>
public sealed partial class ProcessingLogTests(ITestOutputHelper testOutput)
{
    private const string Operator = "測試人員";

    private static readonly string[] GlHeaders =
        ["傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標"];

    /// <summary>
    /// 合成案件四張傳票、每張一借一貸；前兩張的借方摘要有「調整」。跑完第一步到第六步的條件篩選報告後，
    /// GL 紀錄依序有每一步，TB 紀錄只有和 TB 有關的步驟；條件與筆數和畫面、報告上的一致。
    /// </summary>
    [Fact]
    public async Task GlAndTbLogs_RecordEachStep_WithItsConditionsAndCounts()
    {
        using var host = new HandlerTestHost(userFullName: Operator);
        var projectId = await SetupFourVoucherCaseAsync(host);
        await host.DispatchAsync("prescreen.run");
        var saved = await host.DispatchAsync("filter.scenario.save", JsonSerializer.Serialize(new
        {
            populationScope = "auditPeriod",
            scenario = KeywordScenario("調整情境", "調整"),
        }));
        var revision = saved.GetProperty("resultRef").GetProperty("revision").GetString()!;
        // 畫面打開已存情境時讀的條件文字與資料庫條件式；紀錄要寫一樣的內容。
        var screen = await host.DispatchAsync("query.filterVoucherPage", JsonSerializer.Serialize(new
        {
            scenarioPosition = 1,
            scenarioRevision = revision,
            populationScope = "auditPeriod",
            pageSize = 1,
        }));
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        var validationResultId = loaded.GetProperty("validationResult").GetProperty("resultRef").GetProperty("resultId").GetString()!;
        var exported = await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new { validationResultId, revision }));
        var reportPath = exported.GetProperty("publishedReport").GetProperty("fullPath").GetString()!;

        var caseDirectory = Path.Combine(host.ProjectsRoot, projectId);
        var glPath = Path.Combine(caseDirectory, $"{projectId}_GL處理紀錄.txt");
        var tbPath = Path.Combine(caseDirectory, $"{projectId}_TB處理紀錄.txt");
        Assert.True(File.Exists(glPath), "案件資料夾裡要有 GL 處理紀錄。");
        Assert.True(File.Exists(tbPath), "案件資料夾裡要有 TB 處理紀錄。");
        var glBytes = await File.ReadAllBytesAsync(glPath);
        var tbBytes = await File.ReadAllBytesAsync(tbPath);

        // 和舊版的歷程紀錄一樣是 UTF-8 加 BOM、Windows 換行，記事本與 Excel 都能直接開。
        foreach (var bytes in new[] { glBytes, tbBytes })
        {
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            var raw = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            Assert.DoesNotMatch(LoneLineFeed(), raw);
        }

        var gl = Normalize(Encoding.UTF8.GetString(glBytes, 3, glBytes.Length - 3));
        var tb = Normalize(Encoding.UTF8.GetString(tbBytes, 3, tbBytes.Length - 3));
        testOutput.WriteLine(gl);
        testOutput.WriteLine(tb);

        Assert.StartsWith(
            "============================================================\n"
            + "JET GL 處理紀錄\n"
            + $"案件：{projectId}\n"
            + "============================================================\n"
            + "JET 依時間順序記下這個案件 GL 的每一次處理：做了什麼、誰做的、用了哪些條件，以及處理後的筆數。\n"
            + "紀錄從 <時間> 開始，在這之前做過的處理沒有記在這裡。\n",
            gl);
        Assert.StartsWith(
            "============================================================\n"
            + "JET TB 處理紀錄\n"
            + $"案件：{projectId}\n"
            + "============================================================\n"
            + "JET 依時間順序記下這個案件 TB 的每一次處理：做了什麼、誰做的、用了哪些條件，以及處理後的筆數。\n"
            + "紀錄從 <時間> 開始，在這之前做過的處理沒有記在這裡。\n",
            tb);

        Assert.Equal(
            ["建立案件", "匯入 GL", "確認 GL 欄位配對", "資料驗證", "預篩選", "儲存篩選情境", "匯出條件篩選報告"],
            Titles(gl));
        Assert.Equal(["建立案件", "匯入 TB", "確認 TB 欄位配對", "資料驗證"], Titles(tb));

        var created =
            Heading("建立案件")
            + $"案件名稱：{projectId}\n"
            + "案件編號：INLINE-2025-001\n"
            + "客戶名稱：測試自含案件\n"
            + "查核期間：2025-01-01 至 2025-12-31\n"
            + "財報準備日：（未填）\n";
        Assert.Contains(created, gl, StringComparison.Ordinal);
        Assert.Contains(created, tb, StringComparison.Ordinal);

        Assert.Contains(
            Heading("匯入 GL")
            + "匯入方式：取代原有資料\n"
            + "來源 1：<來源檔>\n"
            + "    工作表：未指定，JET 讀第一個工作表\n"
            + "    欄名列：第 1 列\n"
            + "    讀入筆數：8\n"
            + "GL 合計：8 筆\n"
            + "欄位（8 欄）：傳票號碼、傳票日期、核准日期、科目代號、科目名稱、摘要、金額、借方旗標\n",
            gl,
            StringComparison.Ordinal);

        // 欄位配對照第三步的欄位順序；金額的算法和底稿「自動化工具-檔案欄位資訊」寫同一句。
        Assert.Contains(
            Heading("確認 GL 欄位配對")
            + "欄位配對（JET 欄位 ← GL 檔欄名）：\n"
            + "    傳票號碼 ← 傳票號碼\n"
            + "    總帳日期 ← 傳票日期\n"
            + "    傳票核准日 ← 核准日期\n"
            + "    會計科目編號 ← 科目代號\n"
            + "    會計科目名稱 ← 科目名稱\n"
            + "    傳票摘要 ← 摘要\n"
            + "    金額 ← 金額\n"
            + "    借貸別 ← 借方旗標\n"
            + "沒有配對的欄位：傳票文件項次、分錄來源模組、傳票建立人員、傳票核准人員、人工/自動分錄、過帳狀態\n"
            + "傳票文件項次：沒有配對，JET 在每張傳票內依列序自動編號。\n"
            + "金額：傳票金額_JE 由系統產生 : 金額欄位 金額、借貸方判斷欄位 借方旗標，借方為「1」，貸方為「0」\n"
            + "過帳狀態：未設定過帳狀態篩選\n"
            + "攸關資料元素：未保留額外的攸關資料元素欄位\n"
            + "往下填補空白：未往下填補空白\n"
            + "存入的分錄：8 筆，GL 與 GL(未納入測試) 合計\n",
            gl,
            StringComparison.Ordinal);

        Assert.Contains(
            Heading("資料驗證")
            + "查核期間：2025-01-01 至 2025-12-31\n"
            + "匯入的分錄：8 筆，借方合計 400，貸方合計 400\n"
            + "GL：8 筆分錄、4 張傳票，借方合計 400，貸方合計 400\n"
            + "GL(未納入測試)：0 筆；總帳日期空白、無法辨識或不在查核期間 0 筆，過帳狀態不符 0 筆\n"
            + "存入筆數與合計核對：筆數一致，合計一致\n"
            + "GL 與 TB 逐科目比對：差異科目 0 個\n"
            + "借貸不平測試：借貸不平的傳票 0 張\n"
            + "資料缺漏輔助檢視：空白傳票號碼 0 筆，空白科目編號 0 筆\n"
            + "核准日不在期間：0 筆\n"
            + "總帳日期空白：0 筆\n"
            + "同一傳票號碼出現在不同總帳日期：0 個號碼，0 筆分錄\n"
            + "資料可靠性測試：指定 59 筆，抽出 8 筆，隨機種子序號 <種子>\n",
            gl,
            StringComparison.Ordinal);

        // 預篩選每條規則一行：命中幾筆分錄、幾張傳票；不能執行的寫原因。「調整」是預設的特定描述，
        // 借方平均 50 元有兩位整數，所以尾數看連續 1 個 0，100 元的分錄都算。
        var prescreen = Entry(gl, "預篩選");
        Assert.StartsWith("母體：查核期間內的 GL 分錄 8 筆\n", prescreen, StringComparison.Ordinal);
        Assert.Contains("分錄摘要出現特定描述：2 筆分錄，2 張傳票\n", prescreen, StringComparison.Ordinal);
        Assert.Contains("分錄金額中有連續 0 的尾數：8 筆分錄，4 張傳票（尾數連續 1 個 0）\n", prescreen, StringComparison.Ordinal);
        Assert.Contains("空白摘要：0 筆分錄，0 張傳票\n", prescreen, StringComparison.Ordinal);
        Assert.Matches(PrescreenNotRunLine(), prescreen);

        // 情境的條件與資料庫條件式和畫面上的一字不差；筆數和報告摘要頁的 C、D 欄同一個口徑。
        Assert.Contains(
            Heading("儲存篩選情境")
            + "情境 1：調整情境\n"
            + "篩選動機：測試 調整情境\n"
            + $"條件：{screen.GetProperty("conditionText").GetString()}\n"
            + "資料庫條件式：\n"
            + Indent(screen.GetProperty("conditionSql").GetString()!)
            + "符合條件的傳票數量：2\n"
            + "符合條件的分錄筆數：2\n",
            gl,
            StringComparison.Ordinal);

        // 匯出時記下檔案與每張明細表的資料列數；條件篩選報告的明細表列出整張傳票，另記標了 Y 的列數。
        Assert.Contains(
            Heading("匯出條件篩選報告")
            + $"檔案：{Normalize(reportPath)}\n"
            + "各工作表的資料列數：\n"
            + "    #Criteria Select 1：4 列，其中 2 列的 Final_TAG 是 Y\n",
            gl,
            StringComparison.Ordinal);

        Assert.Contains(
            Heading("匯入 TB")
            + "匯入方式：取代原有資料\n"
            + "來源 1：<來源檔>\n"
            + "    工作表：未指定，JET 讀第一個工作表\n"
            + "    欄名列：第 1 列\n"
            + "    讀入筆數：2\n"
            + "TB 合計：2 筆\n"
            + "欄位（3 欄）：科目代號、科目名稱、變動金額\n",
            tb,
            StringComparison.Ordinal);
        Assert.Contains(
            Heading("確認 TB 欄位配對")
            + "欄位配對（JET 欄位 ← TB 檔欄名）：\n"
            + "    會計科目編號 ← 科目代號\n"
            + "    會計科目名稱 ← 科目名稱\n"
            + "    年度變動金額 ← 變動金額\n"
            + "存入的科目：2 筆\n",
            tb,
            StringComparison.Ordinal);
        Assert.Contains(
            Heading("資料驗證")
            + "查核期間：2025-01-01 至 2025-12-31\n"
            + "GL 與 TB 逐科目比對：差異科目 0 個\n",
            tb,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 確認 GL 欄位配對後有日期提醒時，每一則寫一行：欄位、來源欄、筆數、哪一種情形，以及 JET 怎麼處理。
    /// 讀不成日期、年份不尋常，以及早於 1900-03-01、在底稿寫成文字的日期，各寫各的情形。
    /// </summary>
    [Fact]
    public async Task MappingDateNotices_AreRecordedWithTheirOwnKind()
    {
        using var host = new HandlerTestHost(userFullName: Operator);
        string[] approved = ["2025-03-06", "not-a-date", "1899-12-31", "1900-01-15"];
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder =>
            {
                builder.WithColumns(GlHeaders);
                for (var v = 1; v <= approved.Length; v++)
                {
                    builder.AddRow($"JV-{v:0000}", "2025-03-05", approved[v - 1], "1101", "現金", $"借方 {v}", "100.00", 1);
                    builder.AddRow($"JV-{v:0000}", "2025-03-05", approved[v - 1], "4101", "收入", $"貸方 {v}", "100.00", 0);
                }
            });
        var path = Path.Combine(host.ProjectsRoot, projectId, $"{projectId}_GL處理紀錄.txt");
        var mapping = Entry(Normalize(await File.ReadAllTextAsync(path)), "確認 GL 欄位配對");

        Assert.Contains("日期提醒：傳票核准日（核准日期）有 2 筆讀不成日期，那一格當成沒有日期\n", mapping, StringComparison.Ordinal);
        Assert.Contains("日期提醒：傳票核准日（核准日期）有 2 筆年份不在 1900 到 2100 年之間，照來源資料保留\n", mapping, StringComparison.Ordinal);
        Assert.Contains("日期提醒：傳票核准日（核准日期）有 2 筆日期早於 1900-03-01、底稿寫成文字，照來源資料保留\n", mapping, StringComparison.Ordinal);
    }

    /// <summary>失敗或取消的操作沒有改動案件，不寫進處理紀錄；只讀的查詢也不寫。</summary>
    [Fact]
    public async Task FailedActionsAndQueries_AreNotRecorded()
    {
        using var host = new HandlerTestHost(userFullName: Operator);
        var projectId = await SetupFourVoucherCaseAsync(host);
        var path = Path.Combine(host.ProjectsRoot, projectId, $"{projectId}_GL處理紀錄.txt");
        Assert.True(File.Exists(path), "案件資料夾裡要有 GL 處理紀錄。");
        var before = await File.ReadAllTextAsync(path);

        await Assert.ThrowsAnyAsync<Exception>(() => host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
        {
            filePath = Path.Combine(host.ProjectsRoot, "不存在的檔案.xlsx"),
        })));
        await host.DispatchAsync("query.dataPreview", JsonSerializer.Serialize(new { dataset = "gl" }));

        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }

    /// <summary>
    /// 紀錄檔被別的程式鎖住時，操作照常完成；這次的紀錄先留著，下一次寫得進去時依原順序補上。
    /// </summary>
    [Fact]
    public async Task LockedLogFile_DoesNotFailTheAction_AndTheEntryIsWrittenLaterInOrder()
    {
        using var host = new HandlerTestHost(userFullName: Operator);
        var projectId = await SetupFourVoucherCaseAsync(host);
        var path = Path.Combine(host.ProjectsRoot, projectId, $"{projectId}_GL處理紀錄.txt");
        Assert.True(File.Exists(path), "案件資料夾裡要有 GL 處理紀錄。");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var holidays = await host.DispatchAsync("import.holiday", JsonSerializer.Serialize(new { dates = new[] { "2025-02-28" } }));
            Assert.Equal(1, holidays.GetProperty("count").GetInt32());
        }

        await host.DispatchAsync("calendar.setNonWorkingDays", JsonSerializer.Serialize(new { days = new[] { 6, 0 } }));

        var gl = Normalize(await File.ReadAllTextAsync(path));
        Assert.Equal(
            ["建立案件", "匯入 GL", "確認 GL 欄位配對", "資料驗證", "匯入國定假日", "設定每週休息日"],
            Titles(gl));
        Assert.Contains(Heading("匯入國定假日") + "國定假日：1 天\n" + "    2025-02-28\n", gl, StringComparison.Ordinal);
        Assert.Contains(Heading("設定每週休息日") + "每週休息日：星期六、星期日\n", gl, StringComparison.Ordinal);

        // 支援日誌記下寫不進去的是哪一份紀錄、還有幾筆沒寫，不記內容或路徑。
        var exported = await host.DispatchAsync("support.log.export", JsonSerializer.Serialize(new { projectId }));
        var failures = (await File.ReadAllLinesAsync(exported.GetProperty("filePath").GetString()!))
            .Where(line => line.Contains("\"processing_log.write_failed\"", StringComparison.Ordinal))
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("fields").Clone())
            .ToArray();
        var failure = Assert.Single(failures);
        Assert.Equal("gl", failure.GetProperty("log_target").GetString());
        Assert.Equal(1, failure.GetProperty("pending_entries").GetInt32());
        Assert.DoesNotContain(projectId, failure.GetRawText(), StringComparison.Ordinal);
    }

    private static async Task<string> SetupFourVoucherCaseAsync(HandlerTestHost host) =>
        await InlineWorkbookProject.SetupAsync(
            host,
            builder =>
            {
                builder.WithColumns(GlHeaders);
                for (var v = 1; v <= 4; v++)
                {
                    var description = v <= 2 ? $"調整 借方 {v}" : $"借方 {v}";
                    builder.AddRow($"JV-{v:0000}", "2025-03-05", "2025-03-06", "1101", "現金", description, "100.00", 1);
                    builder.AddRow($"JV-{v:0000}", "2025-03-05", "2025-03-06", "4101", "收入", $"結轉 貸方 {v}", "100.00", 0);
                }
            },
            validateForDownstream: true);

    private static string Heading(string title) =>
        "\n------------------------------------------------------------\n"
        + $"{title}    <時間>    {Operator}\n"
        + "------------------------------------------------------------\n";

    private static string[] Titles(string log) =>
        EntryTitle().Matches(log).Select(match => match.Groups["title"].Value).ToArray();

    /// <summary>一筆紀錄標題下的內容，到下一筆的分隔線為止。</summary>
    private static string Entry(string log, string title)
    {
        var start = log.IndexOf(Heading(title), StringComparison.Ordinal);
        Assert.True(start >= 0, $"紀錄裡沒有「{title}」。");
        var body = log[(start + Heading(title).Length)..];
        var end = body.IndexOf("\n------------------------------------------------------------\n", StringComparison.Ordinal);
        return end < 0 ? body : body[..(end + 1)];
    }

    private static string Indent(string text) =>
        string.Concat(text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => $"    {line}\n"));

    /// <summary>時間、暫存檔名與隨機種子序號每次不同，換成固定字樣再比對；換行統一成 \n。</summary>
    private static string Normalize(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        normalized = Timestamp().Replace(normalized, "<時間>");
        normalized = SourceFile().Replace(normalized, "<來源檔>");
        return Seed().Replace(normalized, "隨機種子序號 <種子>");
    }

    private static object KeywordScenario(string name, string keyword) => new
    {
        name,
        rationale = $"測試 {name}",
        groups = new[]
        {
            new
            {
                join = "AND",
                conditions = new[]
                {
                    new
                    {
                        join = "AND",
                        type = "fieldValue",
                        field = "description",
                        @operator = "contains",
                        values = new[] { keyword },
                    },
                },
            },
        },
    };

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}")]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"(?<=來源 \d+：).*jet-fixture-[0-9a-f]{32}\.xlsx")]
    private static partial Regex SourceFile();

    [GeneratedRegex(@"隨機種子序號 \d+")]
    private static partial Regex Seed();

    [GeneratedRegex(@"(?<!\r)\n")]
    private static partial Regex LoneLineFeed();

    [GeneratedRegex(@"\n-{60}\n(?<title>[^\n]+?)    <時間>    ")]
    private static partial Regex EntryTitle();

    [GeneratedRegex(@"期末財報準備日起核准之分錄：無法執行。.+\n")]
    private static partial Regex PrescreenNotRunLine();
}
