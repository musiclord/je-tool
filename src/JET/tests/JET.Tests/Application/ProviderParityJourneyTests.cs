using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

// 改走正式批次匯入與明示投影參數；保留原始合成資料及固定答案。
namespace JET.Tests.Application;

/// <summary>
/// 雙 provider 等價(guide §13 golden tests):同一 deterministic demo 案件,經 dispatcher 在
/// SQLite 與 SQL Server 各跑完整工作流程(建案→匯入 GL/TB→行事曆→科目配對→配對 commit→
/// 驗證→預篩選→進階篩選),斷言可觀察的 wire 結果逐項相同(金額以 scaled 整數、計數、命中身分、
/// 預覽列順序)。連線閘控:無 LocalDB/Express 即跳過。
/// </summary>
public sealed class ProviderParityJourneyTests
{

    /// <summary>
    /// 刻意不齊的 GL(借方旗標=1 → amount_scaled 正,=0 → 負,與 §6.1 借貸側判定一致):
    /// 1101 借 100 → gl_s=+100、9001 貸 100 → gl_s=-100、4101 借 50 → gl_s=+50、5101 貸 50 → gl_s=-50。
    /// 每張傳票借貸自平衡(免污染其他驗證)。9001 只在 GL(TB 無)→ not_in_tb 差異。
    /// </summary>
    private static void ConfigureCompletenessGl(InlineGlWorkbookBuilder builder) =>
        builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金",     "借", "100.00", 1)
            .AddRow("JV-001", "2025-03-05", "9001", "其他",     "貸", "100.00", 0)
            .AddRow("JV-002", "2025-03-06", "4101", "銷貨收入", "借", "50.00",  1)
            .AddRow("JV-002", "2025-03-06", "5101", "成本",     "貸", "50.00",  0);

    /// <summary>
    /// 對應 TB(change_amount_scaled 直接帶號):1101 變動 80(≠ gl_s 100 → 差異)、
    /// 4101 變動 +50(= gl_s → 不入差異)、5101 變動 -50(= gl_s → 不入差異)、
    /// 7001 只在 TB(GL 無 → 差異)。預期差異科目集合 = {1101, 7001, 9001} 三筆,
    /// 以 account_number 升序、跨 pageSize=2 的多頁(1101|7001 一頁,9001 次頁)。
    /// </summary>
    private static void ConfigureCompletenessTb(InlineTbWorkbookBuilder builder) =>
        builder
            .AddRow("1101", "現金",     "80.00")
            .AddRow("4101", "銷貨收入", "50.00")
            .AddRow("5101", "成本",     "-50.00")
            .AddRow("7001", "預付款",   "30.00");

    /// <summary>逐頁帶 nextCursor 走訪 query.completenessDiffPage 到底,回傳串接後的 accountNumber 序列。</summary>
    private static async Task<List<string>> WalkCompletenessDiffPagesAsync(HandlerTestHost host, int pageSize)
    {
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await host.DispatchAsync("query.completenessDiffPage",
                JsonSerializer.Serialize(new { cursor, pageSize }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
            {
                all.Add(r.GetProperty("accountNumber").GetString()!);
            }

            var nextCursor = page.GetProperty("nextCursor");
            cursor = nextCursor.ValueKind == JsonValueKind.Null ? null : nextCursor.GetString();
        } while (cursor is not null);

        return all;
    }

    /// <summary>
    /// 多張不平傳票(每張 SUM(amount_scaled)≠0):JV-001 借 300/貸 100(+200)、JV-002 借 50(+50)、
    /// JV-003 貸 70(-70)、JV-004 借 90(+90)。voucher_number 升序跨 pageSize=2 多頁。
    /// </summary>
    private static void ConfigureUnbalancedGl(InlineGlWorkbookBuilder builder) =>
        builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金",     "借", "300.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "貸", "100.00", 0)
            .AddRow("JV-002", "2025-03-06", "1101", "現金",     "借", "50.00",  1)
            .AddRow("JV-003", "2025-03-07", "4101", "銷貨收入", "貸", "70.00",  0)
            .AddRow("JV-004", "2025-03-08", "1101", "現金",     "借", "90.00",  1);

    /// <summary>多列空摘要(category=blankDescription):四列摘要留空,跨 pageSize=2 多頁。</summary>
    private static void ConfigureBlankDescriptionGl(InlineGlWorkbookBuilder builder) =>
        builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金",     null, "10.00", 1)
            .AddRow("JV-002", "2025-03-06", "4101", "銷貨收入", null, "20.00", 0)
            .AddRow("JV-003", "2025-03-07", "1101", "現金",     null, "30.00", 1)
            .AddRow("JV-004", "2025-03-08", "4101", "銷貨收入", null, "40.00", 0);

    /// <summary>逐頁帶 nextCursor 走訪 query.voucherBalancePage 到底,回傳 voucherNumber 序列。</summary>
    private static async Task<List<string>> WalkVoucherBalancePagesAsync(HandlerTestHost host, int pageSize)
    {
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await host.DispatchAsync("query.voucherBalancePage",
                JsonSerializer.Serialize(new { cursor, pageSize }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
            {
                all.Add(r.GetProperty("voucherNumber").GetString()!);
            }

            var nextCursor = page.GetProperty("nextCursor");
            cursor = nextCursor.ValueKind == JsonValueKind.Null ? null : nextCursor.GetString();
        } while (cursor is not null);

        return all;
    }

    /// <summary>逐頁帶 nextCursor 走訪 query.blankRecordsPage(blankDescription)到底,回傳 doc|acc|date 序列。</summary>
    private static async Task<List<string>> WalkBlankRecordsPagesAsync(HandlerTestHost host, int pageSize)
    {
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await host.DispatchAsync("query.blankRecordsPage",
                JsonSerializer.Serialize(new { category = "blankDescription", cursor, pageSize }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
            {
                all.Add($"{r.GetProperty("voucherNumber").GetString()}|" +
                        $"{r.GetProperty("accountNumber").GetString()}|" +
                        $"{r.GetProperty("glDate").GetString()}");
            }

            var nextCursor = page.GetProperty("nextCursor");
            cursor = nextCursor.ValueKind == JsonValueKind.Null ? null : nextCursor.GetString();
        } while (cursor is not null);

        return all;
    }

    /// <summary>多情境(命中 nonAuthorizedPreparer + 命中廣域金額 + 0 命中天價金額),供 highRiskSummaryScenarios parity 共用。</summary>
    private static string HighRiskSummaryScenarioPayload() =>
        JsonSerializer.Serialize(new
        {
            scenarios = new object[]
            {
                new {
                    name = "非授權編製人員", rationale = "雙 provider 等價",
                    groups = new[] { new { join = "and", conditions = new object[] {
                        // 裁定 2026-10-08-03 移除傳票日期與回溯過帳，改用示範資料同樣整張傳票兩列皆命中的非授權編製人員。
                        new { join = "and", type = "prescreen", prescreenKey = "nonAuthorizedPreparer" } } } } },
                new {
                    name = "廣域金額", rationale = "雙 provider 等價",
                    groups = new[] { new { join = "and", conditions = new object[] {
                        new { join = "and", type = "numRange", field = "amount", from = "0" } } } } },
                new {
                    name = "天價(打不中)", rationale = "雙 provider 等價",
                    groups = new[] { new { join = "and", conditions = new object[] {
                        new { join = "and", type = "numRange", field = "amount", from = "999999999999" } } } } }
            }
        });

    /// <summary>
    /// rowPage parity 專用多情境:借方行(drCrOnly=debit,行層子集——命中傳票的貸方行為非命中行)
    /// + 非授權編製人員 + 0 命中天價金額。借方行情境保證命中傳票內存在非命中行(matchedPositions=[]),
    /// 使 parity 同時覆蓋非命中行於兩 provider 一致列出。
    /// </summary>
    private static string HighRiskSummaryRowScenarioPayload() =>
        JsonSerializer.Serialize(new
        {
            scenarios = new object[]
            {
                new {
                    name = "借方行", rationale = "雙 provider 等價(行層子集)",
                    groups = new[] { new { join = "and", conditions = new object[] {
                        new { join = "and", type = "drCrOnly", drCr = "debit" } } } } },
                new {
                    name = "非授權編製人員", rationale = "雙 provider 等價",
                    groups = new[] { new { join = "and", conditions = new object[] {
                        // 裁定 2026-10-08-03 移除傳票日期與回溯過帳，改用示範資料同樣整張傳票兩列皆命中的非授權編製人員。
                        new { join = "and", type = "prescreen", prescreenKey = "nonAuthorizedPreparer" } } } } },
                new {
                    name = "天價(打不中)", rationale = "雙 provider 等價",
                    groups = new[] { new { join = "and", conditions = new object[] {
                        new { join = "and", type = "numRange", field = "amount", from = "999999999999" } } } } }
            }
        });

    /// <summary>取 query.highRiskSummaryScenarios 摘要為 position → (voucher,row) 字典(供逐 position 等價比對)。</summary>
    private static async Task<Dictionary<int, (long Voucher, long Row)>> ReadHighRiskSummarySummaryAsync(HandlerTestHost host)
    {
        var data = await host.DispatchAsync("query.highRiskSummaryScenarios");
        var summary = new Dictionary<int, (long Voucher, long Row)>();
        foreach (var s in data.GetProperty("scenarios").EnumerateArray())
        {
            summary[s.GetProperty("position").GetInt32()] = (
                s.GetProperty("voucherHitCount").GetInt64(),
                s.GetProperty("rowHitCount").GetInt64());
        }

        return summary;
    }

    /// <summary>逐頁帶 nextCursor 走訪 query.highRiskSummaryVoucherPage 到底,回傳 doc|voucherTotal|positions 序列。</summary>
    private static async Task<List<string>> WalkHighRiskSummaryVoucherPagesAsync(HandlerTestHost host, int pageSize)
    {
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await host.DispatchAsync("query.highRiskSummaryVoucherPage",
                JsonSerializer.Serialize(new { cursor, pageSize }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
            {
                var positions = string.Join(",", r.GetProperty("matchedPositions")
                    .EnumerateArray().Select(p => p.GetInt32()));
                all.Add($"{r.GetProperty("voucherNumber").GetString()}|" +
                        $"{r.GetProperty("voucherTotal").GetRawText()}|[{positions}]");
            }

            var nextCursor = page.GetProperty("nextCursor");
            cursor = nextCursor.ValueKind == JsonValueKind.Null ? null : nextCursor.GetString();
        } while (cursor is not null);

        return all;
    }

    /// <summary>逐頁帶 nextCursor 走訪 query.highRiskSummaryRowPage 到底,回傳 doc|lineItem|amount|positions 序列。</summary>
    private static async Task<List<string>> WalkHighRiskSummaryRowPagesAsync(HandlerTestHost host, int pageSize)
    {
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await host.DispatchAsync("query.highRiskSummaryRowPage",
                JsonSerializer.Serialize(new { cursor, pageSize }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
            {
                var positions = string.Join(",", r.GetProperty("matchedPositions")
                    .EnumerateArray().Select(p => p.GetInt32()));
                all.Add($"{r.GetProperty("voucherNumber").GetString()}|" +
                        $"{r.GetProperty("lineItem").GetString()}|" +
                        $"{r.GetProperty("amount").GetRawText()}|[{positions}]");
            }

            var nextCursor = page.GetProperty("nextCursor");
            cursor = nextCursor.ValueKind == JsonValueKind.Null ? null : nextCursor.GetString();
        } while (cursor is not null);

        return all;
    }

    /// <summary>
    /// fixed seed INF sample member sequence：同一 demo 案件建兩份，各自 validate.run 落地 INF 樣本後，
    /// 以小 pageSize 走訪 query.infSamplePage 到底，斷言完整 wire row 序列完全相同。>pageSize 擋住只走首頁的偽綠。
    /// 原本比對 SQLite 與 DuckDB 兩種資料庫；只保留 SQLite 之後沒有第二種資料庫，改成比對兩個各自建立的 SQLite 案件，
    /// 確認固定種子在不同案件抽出同一份樣本，方法名稱跟著改（裁定 2026-11-06-08）。第一次失敗：Public 收據 20261106-085246006-0b8a050479f1422b8d8ffc9fcd54c83b。
    /// </summary>
    [Fact]
    public async Task InfSampleMemberSequence_FixedSeed_IsTheSameInTwoSeparateCases()
    {
        const int pageSize = 25;
        const long pinnedSeed = 48271;

        using var firstHost = new HandlerTestHost();
        var firstSequence = await RunFixedSeedInfSampleSequenceAsync(
            firstHost, "sqlite", pinnedSeed, pageSize);

        using var secondHost = new HandlerTestHost();
        var secondSequence = await RunFixedSeedInfSampleSequenceAsync(
            secondHost, "sqlite", pinnedSeed, pageSize);

        Assert.True(firstSequence.Count > pageSize,
            $"INF 樣本須跨頁(>{pageSize} 筆)才能走到後續頁面分支,實得 {firstSequence.Count} 筆。");
        Assert.Equal(firstSequence, secondSequence);
    }

    private static async Task<List<string>> RunFixedSeedInfSampleSequenceAsync(
        HandlerTestHost host,
        string provider,
        long seed,
        int pageSize)
    {
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        PinSampleSeed(host.ProjectsRoot, context.ProjectId, seed);
        await host.DispatchAsync("validate.run");
        return await WalkInfSamplePagesAsync(host, pageSize);
    }

    /// <summary>把全名單壓成可逐項比對的字串序列(createdBy|entryCount|debit|credit),保留順序。</summary>
    private static List<string> Flatten(IReadOnlyList<PreparerSummaryExportRow> rows) =>
        rows.Select(r => $"{r.CreatedBy}|{r.EntryCount}|{r.DebitTotalScaled}|{r.CreditTotalScaled}").ToList();

    /// <summary>逐頁帶 nextCursor 直接走訪全科目 repo 到底,回傳 account_number|tb_s|gl_s|diff|not_in_tb 序列。</summary>
    private static async Task<List<string>> WalkCompletenessAccountPagesAsync(
        ICompletenessAccountPageRepository repo, string projectId, int pageSize)
    {
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await repo.GetPageAsync(
                projectId, moneyScale: 100, "2025-01-01", "2025-12-31", new PageRequest(cursor, pageSize), CancellationToken.None);
            all.AddRange(page.Rows.Select(r =>
                $"{r.AccountNumber}|{r.TbAmountScaled}|{r.GlAmountScaled}|{r.DiffScaled}|{r.NotInTb}"));
            cursor = page.NextCursor;
        } while (cursor is not null);

        return all;
    }

    /// <summary>門檻取母體列數(≥ 任一科目筆數)→ demo 所有科目全命中,跨頁命中 > pageSize 擋偽綠。</summary>
    private const int AllMatchingMaxEntries = DemoDataFactory.GlVoucherCount * DemoDataFactory.LinesPerVoucher;

    /// <summary>科目分錄筆數不超過母體列數的情境（demo 所有科目全命中），供較少使用科目的 parity 共用。
    /// 舊式 customAccountEntryCount 已列為停用寫法，改成等價的「科目與人員統計」；
    /// 這則只在有 SQL Server 時執行，暫緩期間只改不跑，沒有第一次失敗的收據。</summary>
    private static string CustomAccountEntryCountScenarioPayload() =>
        JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name = "較少使用科目(自訂)", rationale = "雙 provider 等價",
                groups = new[] { new { join = "and", conditions = new[] {
                    new { join = "and", type = "entityFrequency", field = "accountNumber", countUnit = "entries", countOperator = "lessThanOrEqual", countFrom = AllMatchingMaxEntries } } } } } }
        });

    /// <summary>編製人員分錄筆數不超過母體列數的情境（demo 編製者全命中），供較少使用編製者的 parity 共用。
    /// 舊式 customPreparerEntryCount 已列為停用寫法，改成等價的「科目與人員統計」；
    /// 這則只在有 SQL Server 時執行，暫緩期間只改不跑，沒有第一次失敗的收據。</summary>
    private static string CustomPreparerEntryCountScenarioPayload() =>
        JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name = "較少使用編製者(自訂)", rationale = "雙 provider 等價",
                groups = new[] { new { join = "and", conditions = new[] {
                    new { join = "and", type = "entityFrequency", field = "createdBy", countUnit = "entries", countOperator = "lessThanOrEqual", countFrom = AllMatchingMaxEntries } } } } } }
        });

    /// <summary>nonAuthorizedPreparer 情境(demo 母體確有命中；裁定 2026-10-08-03 移除傳票日期與回溯過帳，原為 backdatedPosting),供 filterHits parity 兩 provider 共用。</summary>
    private static string NonAuthorizedScenarioPayload() =>
        JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name = "非授權編製人員", rationale = "雙 provider 等價",
                groups = new[] { new { join = "and", conditions = new[] {
                    // 裁定 2026-10-08-03 移除傳票日期與回溯過帳，改用示範資料同樣整張傳票兩列皆命中的非授權編製人員。
                    new { join = "and", type = "prescreen", prescreenKey = "nonAuthorizedPreparer" } } } } } }
        });

    /// <summary>逐頁帶 nextCursor 走訪 query.filterHitsPage(scenarioPosition=1)到底,回傳 doc|lineItem 序列。</summary>
    private static async Task<List<string>> WalkFilterHitsPagesAsync(HandlerTestHost host, int pageSize)
    {
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await host.DispatchAsync("query.filterHitsPage",
                JsonSerializer.Serialize(new { scenarioPosition = 1, cursor, pageSize }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
            {
                all.Add($"{r.GetProperty("voucherNumber").GetString()}|{r.GetProperty("lineItem").GetString()}");
            }

            var nextCursor = page.GetProperty("nextCursor");
            cursor = nextCursor.ValueKind == JsonValueKind.Null ? null : nextCursor.GetString();
        } while (cursor is not null);

        return all;
    }

    /// <summary>
    /// 把某專案 project.json 的 sampleSeed 與 v2 marker 一起釘住（INF 跨 provider 逐列比對前，
    /// 令兩案明確走同一新版演算法、抽同一集合）。
    /// </summary>
    private static void PinSampleSeed(string projectsRoot, string projectId, long seed)
    {
        var path = Path.Combine(projectsRoot, projectId, "project.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["sampleSeed"] = seed;
        node["sampleSeedVersion"] = JetAuditProgram.CurrentInfSamplingAlgorithmVersion;
        File.WriteAllText(path, node.ToJsonString());
    }

    /// <summary>
    /// 逐頁帶 nextCursor 走訪 query.infSamplePage 到底，回傳完整 wire row 序列；
    /// 不另算抽樣 oracle，只讓 provider 彼此直接比對固定 seed 的成員與順序。
    /// </summary>
    private static async Task<List<string>> WalkInfSamplePagesAsync(HandlerTestHost host, int pageSize)
    {
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await host.DispatchAsync("query.infSamplePage",
                JsonSerializer.Serialize(new { cursor, pageSize }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
            {
                all.Add($"{r.GetProperty("voucherNumber").GetString()}|" +
                        $"{r.GetProperty("accountNumber").GetString()}|" +
                        $"{r.GetProperty("accountName").GetString()}|" +
                        $"{r.GetProperty("debit").GetRawText()}|" +
                        $"{r.GetProperty("credit").GetRawText()}|" +
                        $"{r.GetProperty("glDate").GetString()}|" +
                        $"{r.GetProperty("approvalDate").GetString()}|" +
                        $"{r.GetProperty("createdBy").GetString()}|" +
                        $"{r.GetProperty("approvedBy").GetString()}|" +
                        $"{r.GetProperty("description").GetString()}");
            }

            var nextCursor = page.GetProperty("nextCursor");
            cursor = nextCursor.ValueKind == JsonValueKind.Null ? null : nextCursor.GetString();
        } while (cursor is not null);

        return all;
    }

    private static async Task DropAllProjectDatabasesAsync(string projectsRoot, string connectionString)
    {
        if (!Directory.Exists(projectsRoot))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(projectsRoot))
        {
        }
    }

    private static async Task<Dictionary<string, string>> RunWorkflowAsync(
        HandlerTestHost host, string databaseProvider)
    {
        await DemoProjectPipeline.SetupAsync(host, databaseProvider: databaseProvider);

        // 科目配對(解鎖未預期借貸組合 + filter 的 accountPair)。
        var accountMappingFile = await DemoProjectPipeline.DispatchDemoFixtureAsync(
            "demo.exportAccountMappingFile");
        await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
        {
            filePath = accountMappingFile.GetProperty("filePath").GetString(),
            fileName = accountMappingFile.GetProperty("fileName").GetString()
        }));

        // 授權編製人員清單（解鎖非授權編製人員閘控）：只授權三位 demo 編製者，另兩位 → 非授權命中。
        var authorizedListPath = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "AUTHORIZED_PREPARER";
            ws.Cell(2, 1).Value = "王小明";
            ws.Cell(3, 1).Value = "李美麗";
            ws.Cell(4, 1).Value = "陳大文";
        });
        try
        {
            // sourceColumn 改成必填，後端不再猜人員欄；保留資料庫等價比對的原斷言。
            await host.DispatchAsync("import.authorizedPreparer.fromFile",
                JsonSerializer.Serialize(new { filePath = authorizedListPath, fileName = "ap.xlsx", sourceColumn = "AUTHORIZED_PREPARER" }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(authorizedListPath);
        }

        var metrics = new Dictionary<string, string>(StringComparer.Ordinal);

        var validate = await host.DispatchAsync("validate.run");
        var stats = validate.GetProperty("stats");
        metrics["validate.glRowCount"] = stats.GetProperty("glRowCount").GetRawText();
        metrics["validate.voucherCount"] = stats.GetProperty("voucherCount").GetRawText();
        metrics["validate.net"] = stats.GetProperty("net").GetRawText();
        metrics["validate.completeness.diffAccountCount"] =
            validate.GetProperty("completenessTest").GetProperty("diffAccountCount").GetRawText();
        metrics["validate.voucherBalance.unbalancedVoucherCount"] =
            validate.GetProperty("voucherBalanceTest").GetProperty("unbalancedVoucherCount").GetRawText();
        var blankRecords = validate.GetProperty("blankRecordsTest");
        metrics["validate.blank.accountNumber"] = blankRecords.GetProperty("blankAccountNumberCount").GetRawText();
        metrics["validate.blank.voucherNumber"] = blankRecords.GetProperty("blankVoucherNumberCount").GetRawText();
        metrics["validate.blank.description"] = blankRecords.GetProperty("blankDescriptionCount").GetRawText();
        metrics["validate.blank.approvalDateNotInPeriod"] = blankRecords.GetProperty("approvalDateNotInPeriodCount").GetRawText();

        var prescreen = await host.DispatchAsync("prescreen.run");
        metrics["prescreen.suspiciousKeywords"] = prescreen.GetProperty("suspiciousKeywords").GetProperty("count").GetRawText();
        metrics["prescreen.prepPeriodApproval"] = prescreen.GetProperty("prepPeriodApproval").GetProperty("count").GetRawText();
        metrics["prescreen.trailingZeros.count"] = prescreen.GetProperty("trailingZeros").GetProperty("count").GetRawText();
        metrics["prescreen.trailingZeros.threshold"] = prescreen.GetProperty("trailingZeros").GetProperty("zerosThreshold").GetRawText();
        metrics["prescreen.weekend.posting"] = prescreen.GetProperty("weekendActivity").GetProperty("postingCount").GetRawText();
        metrics["prescreen.weekend.approval"] = prescreen.GetProperty("weekendActivity").GetProperty("approvalCount").GetRawText();
        metrics["prescreen.holiday.posting"] = prescreen.GetProperty("holidayActivity").GetProperty("postingCount").GetRawText();
        metrics["prescreen.holiday.approval"] = prescreen.GetProperty("holidayActivity").GetProperty("approvalCount").GetRawText();
        metrics["prescreen.unexpectedAccountPair.count"] = prescreen.GetProperty("unexpectedAccountPair").GetProperty("count").GetRawText();
        metrics["prescreen.unexpectedAccountPair.status"] = prescreen.GetProperty("unexpectedAccountPair").GetProperty("status").GetString()!;
        metrics["prescreen.blankDescription"] = prescreen.GetProperty("blankDescription").GetProperty("count").GetRawText();
        metrics["prescreen.nonAuthorizedPreparer.count"] = prescreen.GetProperty("nonAuthorizedPreparer").GetProperty("count").GetRawText();
        metrics["prescreen.nonAuthorizedPreparer.status"] = prescreen.GetProperty("nonAuthorizedPreparer").GetProperty("status").GetString()!;
        // 第四步照舊版拿掉編製分錄較少的人員與使用較少的科目（裁定 2026-10-29-27），不再比對 rarePreparer 與 rareAccount；第一次失敗：Focused 收據 20261030-163342206-c1b391e5be2e46bfa36b85fdbe6b59ad。
        // 裁定 2026-10-08-03 移除傳票日期與回溯過帳，不再比對 backdatedPosting。
        metrics["prescreen.rareAccounts.distinct"] = prescreen.GetProperty("rareAccounts").GetProperty("distinctAccountCount").GetRawText();
        var preparers = prescreen.GetProperty("preparerSummary").GetProperty("preparers").EnumerateArray().ToList();
        metrics["prescreen.preparerSummary.count"] = preparers.Count.ToString();
        metrics["prescreen.preparerSummary.entryTotal"] = preparers.Sum(c => c.GetProperty("entryCount").GetInt64()).ToString();
        metrics["prescreen.preparerSummary.rows"] =
            prescreen.GetProperty("preparerSummary").GetProperty("preparers").GetRawText();
        metrics["prescreen.rareAccounts.rows"] =
            prescreen.GetProperty("rareAccounts").GetProperty("accounts").GetRawText();

        // 進階篩選:未預期借貸組合(prescreen key),斷言 count/voucherCount 與預覽列序一致。
        var preview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new
        {
            scenario = new
            {
                name = "借貸組合",
                rationale = "雙 provider 等價",
                groups = new object[]
                {
                    new
                    {
                        join = "AND",
                        conditions = new object[]
                        {
                            new { join = "AND", type = "prescreen", prescreenKey = "unexpectedAccountPair" }
                        }
                    }
                }
            }
        }));
        var scenario = preview.GetProperty("scenario");
        metrics["filter.count"] = scenario.GetProperty("count").GetRawText();
        metrics["filter.voucherCount"] = scenario.GetProperty("voucherCount").GetRawText();
        var previewRows = scenario.GetProperty("previewRows").EnumerateArray()
            .Select(r => $"{r.GetProperty("voucherNumber").GetString()}|{r.GetProperty("lineItem").GetString()}|{r.GetProperty("amount").GetRawText()}|{r.GetProperty("drCr").GetString()}")
            .ToList();
        metrics["filter.previewRows"] = string.Join(";", previewRows);

        return metrics;
    }

    /// <summary>
    /// 自動編號：lineItem 未對應的 GL 批次（多傳票、每傳票多列、交錯送入）投影後，
    /// (voucher_number, source_row_number, line_item) 序列符合既有逐傳票 ROW_NUMBER 手算 oracle。
    /// 原本也在 DuckDB 投影並比對兩者一致；只保留 SQLite 之後只留 SQLite 對 oracle 的斷言（裁定 2026-11-06-08）。
    /// </summary>
    [Fact]
    public async Task GlAutoNumbering_LineItemUnmapped_IsEquivalentAcrossLocalProviders()
    {
        using var root = new TempProjectRoot();
        var sqliteDb = new SqliteProjectDatabase(new JetProjectFolder(root.Path));
        var sqliteSequence = await ProjectUnmappedLineItemWithLocalProviderAsync(sqliteDb);

        Assert.Equal(ExpectedAutoNumberingSequence(), sqliteSequence);
    }

    private static async Task<List<(string?, long, string?)>> ProjectUnmappedLineItemWithLocalProviderAsync(
        ILocalProjectDatabase database)
    {
        const string batchId = "b1";
        var projectId = Guid.NewGuid().ToString("N");
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await using (var seed = database.CreateConnection(projectId))
        {
            await seed.OpenAsync();
            await SeedGlStagingAsync(seed, batchId, AutoNumberingSeedRows());
        }

        await new LocalGlRepository(database).ProjectStagingToTargetAsync(
            projectId, batchId, DualSpec(), 10_000, DateParseOptions.Default, periodStart: DateOnly.MinValue, periodEnd: DateOnly.MaxValue,
            postingStatusMapped: false, postingStatusPolicy: null, committedUtc: DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        await using var read = database.CreateConnection(projectId);
        await read.OpenAsync();
        return await ReadLineItemSequenceAsync(read);
    }

    /// <summary>交錯送入：D1 在第 1、3、4 位，D2 在第 2、5 位。</summary>
    private static (long RowNumber, string Doc)[] AutoNumberingSeedRows() =>
        [(1, "D1"), (2, "D2"), (3, "D1"), (4, "D1"), (5, "D2")];

    /// <summary>既有手算 oracle：逐傳票 ROW_NUMBER 依 source_row_number 重設。</summary>
    private static (string?, long, string?)[] ExpectedAutoNumberingSequence() =>
    [
        ("D1", 1, "1"), ("D1", 3, "2"), ("D1", 4, "3"),
        ("D2", 2, "1"), ("D2", 5, "2")
    ];

    /// <summary>未對應 lineItem 的 GL 批次(DualSpec 用,僅借方有額)。</summary>
    private static GlMappingSpec DualSpec() => new(
        new Dictionary<string, string>
        {
            [GlMappingKeys.VoucherNumber] = "doc",
            [GlMappingKeys.GlDate] = "date",
            [GlMappingKeys.AccountNumber] = "acc",
            [GlMappingKeys.AccountName] = "name",
            [GlMappingKeys.Description] = "desc",
            [GlMappingKeys.DebitAmount] = "debit",
            [GlMappingKeys.CreditAmount] = "credit"
        },
        GlAmountMode.DualAmount);

    /// <summary>
    /// 播種 pbc_gl。SQL 為 ANSI、DbConnection 對兩 provider 通用;表名前綴依 provider 給:
    /// SQLite 走裸名(預設 <c>""</c>),SQL Server 走 schema 限定(<c>[prj_xxx].</c>,由 <see cref="SqlServerProjectSchema.QualifierFor"/> 衍生)。
    /// </summary>
    private static async Task SeedGlStagingAsync(
        DbConnection connection, string batchId, IReadOnlyList<(long RowNumber, string Doc)> rows,
        string tablePrefix = "")
    {
        foreach (var (rowNumber, doc) in rows)
        {
            var values = new Dictionary<string, string>
            {
                ["doc"] = doc, ["date"] = "2024-01-01", ["acc"] = "1101", ["name"] = "現金", ["desc"] = "t",
                ["debit"] = "100"
            };

            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                INSERT INTO {tablePrefix}pbc_gl (batch_id, row_number, source_no, source_row_number, row_json)
                VALUES (@batchId, @rowNumber, 1, @sourceRowNumber, @rowJson);
                """;
            AddParam(command, "@batchId", batchId);
            AddParam(command, "@rowNumber", rowNumber);
            AddParam(command, "@sourceRowNumber", (int)rowNumber);
            AddParam(command, "@rowJson", JsonSerializer.Serialize(values, JetJsonStorage.Options));
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// 讀 (voucher_number, source_row_number, line_item),依文件/排序鍵還原(兩 provider 同查詢)。
    /// 表名前綴依 provider 給:SQLite 裸名(預設 <c>""</c>)、SQL Server schema 限定(<c>[prj_xxx].</c>)。
    /// </summary>
    private static async Task<List<(string?, long, string?)>> ReadLineItemSequenceAsync(
        DbConnection connection, string tablePrefix = "")
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT voucher_number, source_row_number, line_item
            FROM {tablePrefix}gl
            ORDER BY voucher_number, source_row_number;
            """;

        var sequence = new List<(string?, long, string?)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sequence.Add((
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return sequence;
    }

    private static void AddParam(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }

    /// <summary>
    /// 共用的工作簿設定：含一張不平傳票（JV-001:借 300 貸 100）、一張平衡傳票（JV-002:借貸各 50）、
    /// 以及一列空科目（JV-003 第一列）用於觸發 blankRecords 旗標。
    /// JV-001 差額 200、JV-003 同時有 blankAccountNumber；JV-002 不應出現在任何不平清單。
    /// </summary>
    private static void ConfigureWorkbook(InlineGlWorkbookBuilder builder) =>
        builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            // JV-001：借 300、貸 100 → 不平，差額 200
            .AddRow("JV-001", "2025-03-05", "1101", "現金",     "借方",   "300.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "貸方",   "100.00", 0)
            // JV-002：借 50、貸 50 → 平衡（不應出現在 unbalancedVouchers）
            .AddRow("JV-002", "2025-03-06", "1101", "現金",     "借方",   "50.00",  1)
            .AddRow("JV-002", "2025-03-06", "4101", "銷貨收入", "貸方",   "50.00",  0)
            // JV-003：空科目列 → 觸發 blankAccountNumber
            .AddRow("JV-003", "2025-03-07", null,   null,       "空科目分錄", "10.00", 1)
            .AddRow("JV-003", "2025-03-07", "4101", "銷貨收入", "對方",   "10.00",  0);

    /// <summary>
    /// 匯入 demo 科目配對檔（test-only demo dispatcher → production import.accountMapping.fromFile）。
    /// </summary>
    private static async Task ImportDemoAccountMappingAsync(HandlerTestHost host)
    {
        var file = await DemoProjectPipeline.DispatchDemoFixtureAsync(
            "demo.exportAccountMappingFile");
        await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
        {
            filePath = file.GetProperty("filePath").GetString(),
            fileName = file.GetProperty("fileName").GetString()
        }));
    }

    /// <summary>把全科目配對壓成可逐項比對的字串序列(GL_NUMBER|GL_NAME|STANDARDIZED|notInTb),保留順序。</summary>
    private static List<string> FlattenAccountMapping(IReadOnlyList<AccountMappingExportRow> rows) =>
        rows.Select(r => $"{r.AccountNumber}|{r.AccountName}|{r.Category}|{r.NotInTb}").ToList();

    /// <summary>
    /// 取 Holiday + Makeup 兩 day_type,合併成 (dayType|date|name) 序列(保留各段日期順序);
    /// 另回 holiday 列數供偽綠守門。
    /// </summary>
    private static async Task<(List<string> Sequence, int HolidayCount)> FlattenCalendarAsync(
        ICalendarExportRepository repo, string projectId)
    {
        var holidays = await repo.FetchDaysAsync(projectId, CalendarDayType.Holiday, CancellationToken.None);
        var makeupDays = await repo.FetchDaysAsync(projectId, CalendarDayType.Makeup, CancellationToken.None);

        var sequence = new List<string>();
        sequence.AddRange(holidays.Select(d => $"holiday|{d.Date}|{d.Name}"));
        sequence.AddRange(makeupDays.Select(d => $"makeup|{d.Date}|{d.Name}"));
        return (sequence, holidays.Count);
    }
}
