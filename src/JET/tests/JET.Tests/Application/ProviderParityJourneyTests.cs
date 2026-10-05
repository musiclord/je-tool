using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

// 第 9 批中低 14：改走正式批次匯入與明示投影參數；保留原始合成資料及固定答案。
namespace JET.Tests.Application;

/// <summary>
/// 雙 provider 等價(guide §13 golden tests):同一 deterministic demo 案件,經 dispatcher 在
/// SQLite 與 SQL Server 各跑完整工作流程(建案→匯入 GL/TB→行事曆→科目配對→配對 commit→
/// 驗證→預篩選→進階篩選),斷言可觀察的 wire 結果逐項相同(金額以 scaled 整數、計數、命中身分、
/// 預覽列順序)。連線閘控:無 LocalDB/Express 即跳過。
/// </summary>
public sealed class ProviderParityJourneyTests
{
    [SqlServerFact]
    public async Task FullWorkflow_SqliteAndSqlServer_ProduceEquivalentWireResults()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 跳過
        }

        // SQLite 路徑(oracle)
        using var sqliteHost = new HandlerTestHost();
        var sqliteMetrics = await RunWorkflowAsync(sqliteHost, "sqlite");

        // SQL Server 路徑(同 demo 案件)
        using var sqlServerHost = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            var sqlServerMetrics = await RunWorkflowAsync(sqlServerHost, "sqlServer");

            // 逐項等價(Dictionary 比對在失敗時直接顯示差異欄位)。
            Assert.Equal(sqliteMetrics, sqlServerMetrics);
        }
        finally
        {
            // 即使中途失敗也清理:project.json 必然落地,以資料夾名(=projectId)逐一 DROP。
            await DropAllProjectDatabasesAsync(sqlServerHost.ProjectsRoot, connectionString);
        }
    }

    /// <summary>
    /// sqlite ↔ duckdb 旅程等價（第二本地引擎 golden test，spec §5）：同一 deterministic demo 案件經
    /// dispatcher 在 SQLite 與 DuckDB 各跑完整工作流程（建案→匯入 GL/TB→行事曆→科目配對→授權名單→
    /// 配對 commit→驗證→預篩選→進階篩選），斷言可觀察的 wire 結果逐項相同（金額 scaled 整數、計數、
    /// 命中身分、預覽列序）。DuckDB 是本地檔引擎、不需外部後端，一律 <c>[Fact]</c>——所有環境實跑、不掛閘控。
    /// </summary>
    [Fact]
    public async Task FullWorkflow_SqliteAndDuckDb_ProduceEquivalentWireResults()
    {
        using var sqliteHost = new HandlerTestHost();
        var sqliteMetrics = await RunWorkflowAsync(sqliteHost, "sqlite");

        using var duckDbHost = new HandlerTestHost();
        var duckDbMetrics = await RunWorkflowAsync(duckDbHost, "duckdb");

        // 逐項等價(Dictionary 比對在失敗時直接顯示差異欄位)。本地檔引擎皆自清（temp root dispose）。
        Assert.Equal(sqliteMetrics, duckDbMetrics);
    }

    /// <summary>
    /// keyset 分頁 provider 等價:同一刻意「不齊」的 GL/TB 母體上(多個完整性差異科目:GL-only、
    /// TB-only、金額不符),分別於 SQLite 與 SQL Server 以「小 pageSize」走訪 query.completenessDiffPage
    /// 到底(逐頁帶 nextCursor、跨多頁),斷言兩 provider 回傳的 accountCode 序列完全相同(SequenceEqual)。
    /// 這同時證明 SQL Server OFFSET/FETCH 語法合法、回傳集合與 SQLite 等價、分頁無漏無重、序穩——
    /// 直接 provider-to-provider 比對,不需手寫 recount SQL。無 LocalDB → 跳過(沿用既有閘控)。
    /// </summary>
    [SqlServerFact]
    public async Task CompletenessDiffPage_WalkAllPages_IsEquivalentAcrossProviders()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 跳過
        }

        // 小 pageSize(2)→ 多個差異科目必然跨頁,真正走到 OFFSET/FETCH 的非首頁分支。
        const int pageSize = 2;

        // SQLite 路徑(oracle)
        using var sqliteHost = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(
            sqliteHost, ConfigureCompletenessGl, configureTb: ConfigureCompletenessTb);
        var sqliteSequence = await WalkCompletenessDiffPagesAsync(sqliteHost, pageSize);

        // SQL Server 路徑(同一母體)
        using var sqlServerHost = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            await InlineWorkbookProject.SetupAsync(
                sqlServerHost, ConfigureCompletenessGl, databaseProvider: "sqlServer",
                configureTb: ConfigureCompletenessTb);
            var sqlServerSequence = await WalkCompletenessDiffPagesAsync(sqlServerHost, pageSize);

            // 母體須產生多個差異科目並跨多頁(否則「空 vs 空」或單頁會偽綠,無法證明 OFFSET 分支)。
            Assert.True(sqliteSequence.Count > pageSize,
                $"母體須產生跨頁差異(>{pageSize} 筆)才能走到 OFFSET 分支,實得 {sqliteSequence.Count} 筆。");

            // OFFSET/FETCH vs LIMIT:逐頁 accountCode 序列須完全相同(值＋順序＋筆數)。
            Assert.Equal(sqliteSequence, sqlServerSequence);
        }
        finally
        {
            await DropAllProjectDatabasesAsync(sqlServerHost.ProjectsRoot, connectionString);
        }
    }

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
    /// 以 account_code 升序、跨 pageSize=2 的多頁(1101|7001 一頁,9001 次頁)。
    /// </summary>
    private static void ConfigureCompletenessTb(InlineTbWorkbookBuilder builder) =>
        builder
            .AddRow("1101", "現金",     "80.00")
            .AddRow("4101", "銷貨收入", "50.00")
            .AddRow("5101", "成本",     "-50.00")
            .AddRow("7001", "預付款",   "30.00");

    /// <summary>逐頁帶 nextCursor 走訪 query.completenessDiffPage 到底,回傳串接後的 accountCode 序列。</summary>
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
                all.Add(r.GetProperty("accountCode").GetString()!);
            }

            var nextCursor = page.GetProperty("nextCursor");
            cursor = nextCursor.ValueKind == JsonValueKind.Null ? null : nextCursor.GetString();
        } while (cursor is not null);

        return all;
    }

    /// <summary>
    /// docBalancePage keyset 分頁 provider 等價:刻意造多張借貸不平傳票(SUM(amount_scaled)≠0),
    /// 以小 pageSize 走訪 query.docBalancePage 到底,斷言兩 provider 回傳的 documentNumber 序列完全相同
    /// (SequenceEqual)。同時證明 SQL Server OFFSET/FETCH 合法、集合等價、無漏無重、序穩。
    /// >pageSize 擋偽綠。無 LocalDB → 跳過。
    /// </summary>
    [SqlServerFact]
    public async Task DocBalancePage_WalkAllPages_IsEquivalentAcrossProviders()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 跳過
        }

        const int pageSize = 2;

        using var sqliteHost = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(sqliteHost, ConfigureUnbalancedGl);
        var sqliteSequence = await WalkDocBalancePagesAsync(sqliteHost, pageSize);

        using var sqlServerHost = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            await InlineWorkbookProject.SetupAsync(
                sqlServerHost, ConfigureUnbalancedGl, databaseProvider: "sqlServer");
            var sqlServerSequence = await WalkDocBalancePagesAsync(sqlServerHost, pageSize);

            Assert.True(sqliteSequence.Count > pageSize,
                $"母體須產生跨頁不平傳票(>{pageSize} 筆)才能走到 OFFSET 分支,實得 {sqliteSequence.Count} 筆。");
            Assert.Equal(sqliteSequence, sqlServerSequence);
        }
        finally
        {
            await DropAllProjectDatabasesAsync(sqlServerHost.ProjectsRoot, connectionString);
        }
    }

    /// <summary>
    /// nullRecordsPage keyset 分頁 provider 等價(category=nullDescription):刻意造多列空摘要,
    /// 以小 pageSize 走訪 query.nullRecordsPage 到底,斷言兩 provider 回傳序列完全相同。
    /// 證明 entry_id 游標(long 綁參)、SQLite TRIM vs SQL Server LTRIM(RTRIM(...)) 空白判定等價、
    /// OFFSET/FETCH 合法。>pageSize 擋偽綠。無 LocalDB → 跳過。
    /// </summary>
    [SqlServerFact]
    public async Task NullRecordsPage_WalkAllPages_IsEquivalentAcrossProviders()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 跳過
        }

        const int pageSize = 2;

        using var sqliteHost = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(sqliteHost, ConfigureNullDescriptionGl);
        var sqliteSequence = await WalkNullRecordsPagesAsync(sqliteHost, pageSize);

        using var sqlServerHost = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            await InlineWorkbookProject.SetupAsync(
                sqlServerHost, ConfigureNullDescriptionGl, databaseProvider: "sqlServer");
            var sqlServerSequence = await WalkNullRecordsPagesAsync(sqlServerHost, pageSize);

            Assert.True(sqliteSequence.Count > pageSize,
                $"母體須產生跨頁空摘要列(>{pageSize} 筆)才能走到 OFFSET 分支,實得 {sqliteSequence.Count} 筆。");
            Assert.Equal(sqliteSequence, sqlServerSequence);
        }
        finally
        {
            await DropAllProjectDatabasesAsync(sqlServerHost.ProjectsRoot, connectionString);
        }
    }

    /// <summary>
    /// 多張不平傳票(每張 SUM(amount_scaled)≠0):JV-001 借 300/貸 100(+200)、JV-002 借 50(+50)、
    /// JV-003 貸 70(-70)、JV-004 借 90(+90)。document_number 升序跨 pageSize=2 多頁。
    /// </summary>
    private static void ConfigureUnbalancedGl(InlineGlWorkbookBuilder builder) =>
        builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金",     "借", "300.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "貸", "100.00", 0)
            .AddRow("JV-002", "2025-03-06", "1101", "現金",     "借", "50.00",  1)
            .AddRow("JV-003", "2025-03-07", "4101", "銷貨收入", "貸", "70.00",  0)
            .AddRow("JV-004", "2025-03-08", "1101", "現金",     "借", "90.00",  1);

    /// <summary>多列空摘要(category=nullDescription):四列摘要留空,跨 pageSize=2 多頁。</summary>
    private static void ConfigureNullDescriptionGl(InlineGlWorkbookBuilder builder) =>
        builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金",     null, "10.00", 1)
            .AddRow("JV-002", "2025-03-06", "4101", "銷貨收入", null, "20.00", 0)
            .AddRow("JV-003", "2025-03-07", "1101", "現金",     null, "30.00", 1)
            .AddRow("JV-004", "2025-03-08", "4101", "銷貨收入", null, "40.00", 0);

    /// <summary>逐頁帶 nextCursor 走訪 query.docBalancePage 到底,回傳 documentNumber 序列。</summary>
    private static async Task<List<string>> WalkDocBalancePagesAsync(HandlerTestHost host, int pageSize)
    {
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await host.DispatchAsync("query.docBalancePage",
                JsonSerializer.Serialize(new { cursor, pageSize }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
            {
                all.Add(r.GetProperty("documentNumber").GetString()!);
            }

            var nextCursor = page.GetProperty("nextCursor");
            cursor = nextCursor.ValueKind == JsonValueKind.Null ? null : nextCursor.GetString();
        } while (cursor is not null);

        return all;
    }

    /// <summary>逐頁帶 nextCursor 走訪 query.nullRecordsPage(nullDescription)到底,回傳 doc|acc|date 序列。</summary>
    private static async Task<List<string>> WalkNullRecordsPagesAsync(HandlerTestHost host, int pageSize)
    {
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await host.DispatchAsync("query.nullRecordsPage",
                JsonSerializer.Serialize(new { category = "nullDescription", cursor, pageSize }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
            {
                all.Add($"{r.GetProperty("documentNumber").GetString()}|" +
                        $"{r.GetProperty("accountCode").GetString()}|" +
                        $"{r.GetProperty("postDate").GetString()}");
            }

            var nextCursor = page.GetProperty("nextCursor");
            cursor = nextCursor.ValueKind == JsonValueKind.Null ? null : nextCursor.GetString();
        } while (cursor is not null);

        return all;
    }

    /// <summary>
    /// filterHitsPage keyset 分頁 provider 等價:同一 demo 案件,commit 一個確有命中的情境
    /// (backdatedPosting),以小 pageSize 走訪 query.filterHitsPage{scenarioPosition:1} 到底,
    /// 斷言兩 provider 回傳的 doc|lineItem 序列完全相同(SequenceEqual)。證明 SQL Server OFFSET/FETCH
    /// 合法、entry_id 游標(long 綁參)等價、JOIN result_filter_run 集合等價、無漏無重序穩。
    /// >pageSize 擋偽綠。無 LocalDB → 跳過。
    /// </summary>
    [SqlServerFact]
    public async Task FilterHitsPage_WalkAllPages_IsEquivalentAcrossProviders()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 跳過
        }

        const int pageSize = 5;

        using var sqliteHost = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(sqliteHost);
        await sqliteHost.DispatchAsync("filter.commit", BackdatedScenarioPayload());
        var sqliteSequence = await WalkFilterHitsPagesAsync(sqliteHost, pageSize);

        using var sqlServerHost = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            await DemoProjectPipeline.SetupAsync(sqlServerHost, databaseProvider: "sqlServer");
            await sqlServerHost.DispatchAsync("filter.commit", BackdatedScenarioPayload());
            var sqlServerSequence = await WalkFilterHitsPagesAsync(sqlServerHost, pageSize);

            Assert.True(sqliteSequence.Count > pageSize,
                $"母體須產生跨頁命中(>{pageSize} 筆)才能走到 OFFSET 分支,實得 {sqliteSequence.Count} 筆。");
            Assert.Equal(sqliteSequence, sqlServerSequence);
        }
        finally
        {
            await DropAllProjectDatabasesAsync(sqlServerHost.ProjectsRoot, connectionString);
        }
    }

    /// <summary>
    /// tagMatrixScenarios 情境摘要 provider 等價:同一 demo 案件 commit 多情境(含命中與 0 命中),
    /// 分別於 SQLite 與 SQL Server 取 query.tagMatrixScenarios,斷言每個 position 的
    /// (voucherHitCount, rowHitCount) 兩 count 完全相同。證明 COUNT(DISTINCT)/COUNT(*)→COUNT_BIG
    /// 的 GROUP BY 即時 pivot 跨 provider 等價。命中數 >0 擋偽綠。無 LocalDB → 跳過(沿用既有閘控)。
    /// </summary>
    [SqlServerFact]
    public async Task TagMatrixScenarios_IsEquivalentAcrossProviders()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 跳過
        }

        using var sqliteHost = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(sqliteHost);
        await sqliteHost.DispatchAsync("filter.commit", TagMatrixScenarioPayload());
        var sqliteSummary = await ReadTagMatrixSummaryAsync(sqliteHost);

        using var sqlServerHost = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            await DemoProjectPipeline.SetupAsync(sqlServerHost, databaseProvider: "sqlServer");
            await sqlServerHost.DispatchAsync("filter.commit", TagMatrixScenarioPayload());
            var sqlServerSummary = await ReadTagMatrixSummaryAsync(sqlServerHost);

            // 母體須有命中(position 1/2 命中)才能證明 count 計算,而非「0==0」偽綠。
            Assert.True(sqliteSummary[1].Voucher > 0 && sqliteSummary[2].Row > 0,
                "命中情境須有命中數,否則 count 等價無從證明。");

            // 每個 position 的兩 count 跨 provider 完全等價(含 0 命中情境)。
            Assert.Equal(sqliteSummary, sqlServerSummary);
        }
        finally
        {
            await DropAllProjectDatabasesAsync(sqlServerHost.ProjectsRoot, connectionString);
        }
    }

    /// <summary>多情境(命中 backdatedPosting + 命中廣域金額 + 0 命中天價金額),供 tagMatrixScenarios parity 共用。</summary>
    private static string TagMatrixScenarioPayload() =>
        JsonSerializer.Serialize(new
        {
            scenarios = new object[]
            {
                new {
                    name = "提前過帳", rationale = "雙 provider 等價",
                    groups = new[] { new { join = "and", rules = new object[] {
                        new { join = "and", type = "prescreen", prescreenKey = "backdatedPosting" } } } } },
                new {
                    name = "廣域金額", rationale = "雙 provider 等價",
                    groups = new[] { new { join = "and", rules = new object[] {
                        new { join = "and", type = "numRange", field = "amount", from = "0" } } } } },
                new {
                    name = "天價(打不中)", rationale = "雙 provider 等價",
                    groups = new[] { new { join = "and", rules = new object[] {
                        new { join = "and", type = "numRange", field = "amount", from = "999999999999" } } } } }
            }
        });

    /// <summary>
    /// rowPage parity 專用多情境:借方行(drCrOnly=debit,行層子集——命中傳票的貸方行為非命中行)
    /// + 提前過帳 + 0 命中天價金額。借方行情境保證命中傳票內存在非命中行(matchedPositions=[]),
    /// 使 parity 同時覆蓋非命中行於兩 provider 一致列出。
    /// </summary>
    private static string TagMatrixRowScenarioPayload() =>
        JsonSerializer.Serialize(new
        {
            scenarios = new object[]
            {
                new {
                    name = "借方行", rationale = "雙 provider 等價(行層子集)",
                    groups = new[] { new { join = "and", rules = new object[] {
                        new { join = "and", type = "drCrOnly", drCr = "debit" } } } } },
                new {
                    name = "提前過帳", rationale = "雙 provider 等價",
                    groups = new[] { new { join = "and", rules = new object[] {
                        new { join = "and", type = "prescreen", prescreenKey = "backdatedPosting" } } } } },
                new {
                    name = "天價(打不中)", rationale = "雙 provider 等價",
                    groups = new[] { new { join = "and", rules = new object[] {
                        new { join = "and", type = "numRange", field = "amount", from = "999999999999" } } } } }
            }
        });

    /// <summary>取 query.tagMatrixScenarios 摘要為 position → (voucher,row) 字典(供逐 position 等價比對)。</summary>
    private static async Task<Dictionary<int, (long Voucher, long Row)>> ReadTagMatrixSummaryAsync(HandlerTestHost host)
    {
        var data = await host.DispatchAsync("query.tagMatrixScenarios");
        var summary = new Dictionary<int, (long Voucher, long Row)>();
        foreach (var s in data.GetProperty("scenarios").EnumerateArray())
        {
            summary[s.GetProperty("position").GetInt32()] = (
                s.GetProperty("voucherHitCount").GetInt64(),
                s.GetProperty("rowHitCount").GetInt64());
        }

        return summary;
    }

    /// <summary>
    /// tagMatrixVoucherPage keyset 兩段查詢 provider 等價:同一 demo 案件 commit 多情境(含命中與 0 命中),
    /// 以小 pageSize 走訪 query.tagMatrixVoucherPage 到底,斷言兩 provider 回傳的 document_number 序列完全相同
    /// (SequenceEqual)且每傳票的 matchedPositions 逐筆等價。證明 document_number 字串游標、EXISTS 命中篩、
    /// SUM(debit_amount_scaled) 聚合、查詢 2 同鍵範圍 (@lo,@hi] 取位置 + OFFSET/FETCH 跨 provider 等價、
    /// 分頁無漏無重序穩。>pageSize 擋偽綠。無 LocalDB → 跳過。
    /// </summary>
    [SqlServerFact]
    public async Task TagMatrixVoucherPage_WalkAllPages_IsEquivalentAcrossProviders()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 跳過
        }

        // 小 pageSize(5)→ demo 命中傳票必然跨頁,走到查詢 1 keyset 非首頁分支 + 查詢 2 (@lo,@hi] 範圍。
        const int pageSize = 5;

        using var sqliteHost = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(sqliteHost);
        await sqliteHost.DispatchAsync("filter.commit", TagMatrixScenarioPayload());
        var sqliteRows = await WalkTagMatrixVoucherPagesAsync(sqliteHost, pageSize);

        using var sqlServerHost = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            await DemoProjectPipeline.SetupAsync(sqlServerHost, databaseProvider: "sqlServer");
            await sqlServerHost.DispatchAsync("filter.commit", TagMatrixScenarioPayload());
            var sqlServerRows = await WalkTagMatrixVoucherPagesAsync(sqlServerHost, pageSize);

            // 母體須跨多頁(>pageSize)才能證明 OFFSET 分支與兩段查詢鍵範圍,而非單頁偽綠。
            Assert.True(sqliteRows.Count > pageSize,
                $"命中傳票須跨頁(>{pageSize} 筆)才能走到 OFFSET 分支,實得 {sqliteRows.Count} 筆。");

            // document_number 序列(含 voucherTotal 與 matchedPositions 串接)逐項完全相同。
            Assert.Equal(sqliteRows, sqlServerRows);
        }
        finally
        {
            await DropAllProjectDatabasesAsync(sqlServerHost.ProjectsRoot, connectionString);
        }
    }

    /// <summary>逐頁帶 nextCursor 走訪 query.tagMatrixVoucherPage 到底,回傳 doc|voucherTotal|positions 序列。</summary>
    private static async Task<List<string>> WalkTagMatrixVoucherPagesAsync(HandlerTestHost host, int pageSize)
    {
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await host.DispatchAsync("query.tagMatrixVoucherPage",
                JsonSerializer.Serialize(new { cursor, pageSize }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
            {
                var positions = string.Join(",", r.GetProperty("matchedPositions")
                    .EnumerateArray().Select(p => p.GetInt32()));
                all.Add($"{r.GetProperty("documentNumber").GetString()}|" +
                        $"{r.GetProperty("voucherTotal").GetRawText()}|[{positions}]");
            }

            var nextCursor = page.GetProperty("nextCursor");
            cursor = nextCursor.ValueKind == JsonValueKind.Null ? null : nextCursor.GetString();
        } while (cursor is not null);

        return all;
    }

    /// <summary>
    /// tagMatrixRowPage keyset 兩段查詢 provider 等價:同一 demo 案件 commit 多情境(含命中與 0 命中),
    /// 以小 pageSize 走訪 query.tagMatrixRowPage 到底,斷言兩 provider 回傳的 entry_id 序列完全相同
    /// (SequenceEqual,以 doc|lineItem|amount 鍵代理)且每行的 matchedPositions 逐筆等價(含非命中行的空 [])。
    /// 證明 entry_id 游標、document_number IN(命中傳票集)子查詢、查詢 2 同鍵範圍 (@lo,@hi] 取位置 +
    /// OFFSET/FETCH 跨 provider 等價、分頁無漏無重序穩、非命中行兩 provider 一致列出。
    /// >pageSize 擋偽綠。無 LocalDB → 跳過。
    /// </summary>
    [SqlServerFact]
    public async Task TagMatrixRowPage_WalkAllPages_IsEquivalentAcrossProviders()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 跳過
        }

        // 小 pageSize(7)→ demo 命中傳票之所有行必然跨頁,走查詢 1 keyset 非首頁分支 + 查詢 2 (@lo,@hi]。
        const int pageSize = 7;

        using var sqliteHost = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(sqliteHost);
        await sqliteHost.DispatchAsync("filter.commit", TagMatrixRowScenarioPayload());
        var sqliteRows = await WalkTagMatrixRowPagesAsync(sqliteHost, pageSize);

        using var sqlServerHost = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            await DemoProjectPipeline.SetupAsync(sqlServerHost, databaseProvider: "sqlServer");
            await sqlServerHost.DispatchAsync("filter.commit", TagMatrixRowScenarioPayload());
            var sqlServerRows = await WalkTagMatrixRowPagesAsync(sqlServerHost, pageSize);

            // 母體須跨多頁(>pageSize)才能證明 OFFSET 分支與兩段查詢鍵範圍,而非單頁偽綠。
            Assert.True(sqliteRows.Count > pageSize,
                $"命中傳票之所有行須跨頁(>{pageSize} 行)才能走到 OFFSET 分支,實得 {sqliteRows.Count} 行。");

            // 至少一行為非命中行(matchedPositions 空),證明非命中行確被列出且兩 provider 一致。
            Assert.Contains(sqliteRows, r => r.EndsWith("|[]", StringComparison.Ordinal));

            // 行序列(含 amount 與 matchedPositions 串接)逐項完全相同。
            Assert.Equal(sqliteRows, sqlServerRows);
        }
        finally
        {
            await DropAllProjectDatabasesAsync(sqlServerHost.ProjectsRoot, connectionString);
        }
    }

    /// <summary>逐頁帶 nextCursor 走訪 query.tagMatrixRowPage 到底,回傳 doc|lineItem|amount|positions 序列。</summary>
    private static async Task<List<string>> WalkTagMatrixRowPagesAsync(HandlerTestHost host, int pageSize)
    {
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await host.DispatchAsync("query.tagMatrixRowPage",
                JsonSerializer.Serialize(new { cursor, pageSize }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
            {
                var positions = string.Join(",", r.GetProperty("matchedPositions")
                    .EnumerateArray().Select(p => p.GetInt32()));
                all.Add($"{r.GetProperty("documentNumber").GetString()}|" +
                        $"{r.GetProperty("lineItem").GetString()}|" +
                        $"{r.GetProperty("amount").GetRawText()}|[{positions}]");
            }

            var nextCursor = page.GetProperty("nextCursor");
            cursor = nextCursor.ValueKind == JsonValueKind.Null ? null : nextCursor.GetString();
        } while (cursor is not null);

        return all;
    }

    /// <summary>
    /// fixed seed INF sample member sequence 的本機 provider 等價：同一 demo 案件 validate.run
    /// 於 SQLite 與 DuckDB 落地 INF 樣本後，以小 pageSize 走訪 query.infSamplePage 到底，
    /// 斷言完整 wire row 序列完全相同。>pageSize 擋住只走首頁的偽綠。
    /// </summary>
    [Fact]
    public async Task InfSampleMemberSequence_FixedSeed_IsEquivalentAcrossLocalProviders()
    {
        const int pageSize = 25;
        const long pinnedSeed = 48271;

        using var sqliteHost = new HandlerTestHost();
        var sqliteSequence = await RunFixedSeedInfSampleSequenceAsync(
            sqliteHost, "sqlite", pinnedSeed, pageSize);

        using var duckDbHost = new HandlerTestHost();
        var duckDbSequence = await RunFixedSeedInfSampleSequenceAsync(
            duckDbHost, "duckdb", pinnedSeed, pageSize);

        Assert.True(sqliteSequence.Count > pageSize,
            $"INF 樣本須跨頁(>{pageSize} 筆)才能走到後續頁面分支,實得 {sqliteSequence.Count} 筆。");
        Assert.Equal(sqliteSequence, duckDbSequence);
    }

    /// <summary>
    /// fixed seed INF sample member sequence 的 SQL Server 延伸 gate：重用本機 SQLite 基準，
    /// 逐頁比對 SQL Server 的完整 wire row 序列。無 SQL Server 2022+（非 Express）時具名略過；
    /// 本機 SQLite↔DuckDB parity 仍由不掛 gate 的測試獨立執行。
    /// </summary>
    [SqlServerFact]
    public async Task InfSampleMemberSequence_FixedSeed_SqlServerMatchesLocalBaseline()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 跳過
        }

        const int pageSize = 25;

        // per-project 種子（建案隨機生成）會讓兩個獨立 demo 專案抽到不同樣本成員；
        // 母體相同 + 種子相同才能逐列比對 provider 結果。
        const long pinnedSeed = 48271;

        using var sqliteHost = new HandlerTestHost();
        var sqliteSequence = await RunFixedSeedInfSampleSequenceAsync(
            sqliteHost, "sqlite", pinnedSeed, pageSize);

        using var sqlServerHost = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            var sqlServerSequence = await RunFixedSeedInfSampleSequenceAsync(
                sqlServerHost, "sqlServer", pinnedSeed, pageSize);

            Assert.True(sqliteSequence.Count > pageSize,
                $"INF 樣本須跨頁(>{pageSize} 筆)才能走到 OFFSET 分支,實得 {sqliteSequence.Count} 筆。");
            Assert.Equal(sqliteSequence, sqlServerSequence);
        }
        finally
        {
            await DropAllProjectDatabasesAsync(sqlServerHost.ProjectsRoot, connectionString);
        }
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

    /// <summary>
    /// customPreparerEntryCount filterHits 分頁 provider 等價:同一 demo 案件 commit 一個 maxEntries 夠大
    /// (= 母體列數 → demo 所有編製者全命中)的自訂低頻編製者情境,以小 pageSize 走訪
    /// query.filterHitsPage 到底,斷言兩 provider 回傳的 doc|lineItem 序列完全相同(SequenceEqual)。
    /// 證明低頻編製者自訂門檻軌的 GROUP BY/HAVING/COUNT(*) 子查詢述詞 + entry_id 游標跨 provider 等價。
    /// >pageSize 擋偽綠。無 LocalDB → 跳過。
    /// </summary>
    [SqlServerFact]
    public async Task CustomPreparerEntryCountHitsPage_WalkAllPages_IsEquivalentAcrossProviders()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 跳過
        }

        const int pageSize = 50;

        using var sqliteHost = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(sqliteHost);
        await sqliteHost.DispatchAsync("filter.commit", CustomPreparerEntryCountScenarioPayload());
        var sqliteSequence = await WalkFilterHitsPagesAsync(sqliteHost, pageSize);

        using var sqlServerHost = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            await DemoProjectPipeline.SetupAsync(sqlServerHost, databaseProvider: "sqlServer");
            await sqlServerHost.DispatchAsync("filter.commit", CustomPreparerEntryCountScenarioPayload());
            var sqlServerSequence = await WalkFilterHitsPagesAsync(sqlServerHost, pageSize);

            Assert.True(sqliteSequence.Count > pageSize,
                $"母體須產生跨頁命中(>{pageSize} 筆)才能走到 OFFSET 分支,實得 {sqliteSequence.Count} 筆。");
            Assert.Equal(sqliteSequence, sqlServerSequence);
        }
        finally
        {
            await DropAllProjectDatabasesAsync(sqlServerHost.ProjectsRoot, connectionString);
        }
    }

    /// <summary>
    /// 全編製人員彙總(E1 step1-2,不截斷)provider 等價:同一 demo 案件分別於 SQLite 與 SQL Server
    /// 取 <see cref="ICreatorSummaryExportRepository.FetchAllAsync"/>,斷言兩 provider 回傳的
    /// (createdBy|entryCount|debit|credit)序列完全相同(SequenceEqual)。證明去 LIMIT 後的
    /// GROUP BY created_by ORDER BY COUNT(*) DESC, created_by(SqlServer 用 COUNT_BIG)跨 provider 等價。
    /// 直接建 repo(比照本檔 GlAutoNumbering)指向 host 建立的同一專案庫。
    /// 名單基數 >1 擋偽綠。無 LocalDB → 跳過(沿用既有閘控)。
    /// </summary>
    [SqlServerFact]
    public async Task CreatorSummaryExport_FullList_IsEquivalentAcrossProviders()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 跳過
        }

        using var sqliteHost = new HandlerTestHost();
        var sqliteContext = await DemoProjectPipeline.SetupAsync(sqliteHost);
        var sqliteRepo = new LocalCreatorSummaryExportRepository(
            new SqliteProjectDatabase(new JetProjectFolder(sqliteHost.ProjectsRoot)));
        var sqliteList = Flatten(await sqliteRepo.FetchAllAsync(sqliteContext.ProjectId, "2025-01-01", "2025-12-31", CancellationToken.None));

        using var sqlServerHost = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            var sqlServerContext = await DemoProjectPipeline.SetupAsync(sqlServerHost, databaseProvider: "sqlServer");
            // 同一 base 連線指向同一 instance;預設落在隔離測試庫 JET_Test，CreateConnection(projectId) 對應 host 已建的該專案 schema。
            var sqlServerRepo = new SqlServerCreatorSummaryExportRepository(
                new SqlServerProjectDatabase(new SqlServerConnectionOptions(connectionString)));
            var sqlServerList = Flatten(
                await sqlServerRepo.FetchAllAsync(sqlServerContext.ProjectId, "2025-01-01", "2025-12-31", CancellationToken.None));

            // demo 五位編製者 → 名單基數 >1,證明非「單列 vs 單列」偽綠。
            Assert.True(sqliteList.Count > 1,
                $"全名單須有多位編製者才能證明排序與聚合,實得 {sqliteList.Count} 位。");
            Assert.Equal(sqliteList, sqlServerList);
        }
        finally
        {
            await DropAllProjectDatabasesAsync(sqlServerHost.ProjectsRoot, connectionString);
        }
    }

    /// <summary>把全名單壓成可逐項比對的字串序列(createdBy|entryCount|debit|credit),保留順序。</summary>
    private static List<string> Flatten(IReadOnlyList<CreatorSummaryExportRow> rows) =>
        rows.Select(r => $"{r.CreatedBy}|{r.EntryCount}|{r.DebitTotalScaled}|{r.CreditTotalScaled}").ToList();

    /// <summary>
    /// 完整性「全科目」keyset 分頁(<see cref="ICompletenessAccountPageRepository"/>,匯出底稿 step1 資料源)
    /// provider 等價:同一刻意「不齊」的 GL/TB 母體(共用 completenessDiffPage parity 的
    /// <see cref="ConfigureCompletenessGl"/>/<see cref="ConfigureCompletenessTb"/>——全科目 5 個:
    /// 1101/7001/9001 為差異、4101/5101 為 diff=0),分別於 SQLite 與 SQL Server 直接建 repo
    /// (比照本檔 <see cref="CreatorSummaryExport_FullList_IsEquivalentAcrossProviders"/>,此 query 無 action、
    /// 不走 dispatcher),以小 pageSize(2)逐頁帶 nextCursor 走訪到底,斷言兩 provider 回傳的
    /// account_code 序列(及 tb_s/gl_s/diff/not_in_tb 關鍵欄)完全相同(SequenceEqual)。
    /// 這證明 SQL Server OFFSET/FETCH 語法合法、回傳集合(含 diff=0 科目)與 SQLite 等價、分頁無漏無重序穩。
    /// 母體 >pageSize 擋偽綠;母體含 diff=0 科目使「不過濾 diff」這唯一差異也跨 provider 等價。
    /// 無 LocalDB → 跳過(沿用既有閘控)。
    /// </summary>
    [SqlServerFact]
    public async Task CompletenessAccountPage_WalkAllPages_IsEquivalentAcrossProviders()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 跳過
        }

        // 小 pageSize(2)→ 全科目 5 個必然跨頁,真正走到 OFFSET/FETCH 的非首頁分支。
        const int pageSize = 2;

        // SQLite 路徑(oracle)——直接建 repo 指向 host 建立的同一專案庫。
        using var sqliteHost = new HandlerTestHost();
        var sqliteProjectId = await InlineWorkbookProject.SetupAsync(
            sqliteHost, ConfigureCompletenessGl, configureTb: ConfigureCompletenessTb);
        var sqliteRepo = new LocalCompletenessAccountPageRepository(
            new SqliteProjectDatabase(new JetProjectFolder(sqliteHost.ProjectsRoot)));
        var sqliteSequence = await WalkCompletenessAccountPagesAsync(sqliteRepo, sqliteProjectId, pageSize);

        // SQL Server 路徑(同一母體)
        using var sqlServerHost = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            var sqlServerProjectId = await InlineWorkbookProject.SetupAsync(
                sqlServerHost, ConfigureCompletenessGl, databaseProvider: "sqlServer",
                configureTb: ConfigureCompletenessTb);
            // 同一 base 連線指向同一 instance;預設落在隔離測試庫 JET_Test，CreateConnection(projectId) 對應 host 已建的該專案 schema。
            var sqlServerRepo = new SqlServerCompletenessAccountPageRepository(
                new SqlServerProjectDatabase(new SqlServerConnectionOptions(connectionString)));
            var sqlServerSequence = await WalkCompletenessAccountPagesAsync(sqlServerRepo, sqlServerProjectId, pageSize);

            // 母體須產生跨頁全科目(>pageSize)才能走到 OFFSET 分支,而非單頁偽綠。
            Assert.True(sqliteSequence.Count > pageSize,
                $"全科目須跨頁(>{pageSize} 筆)才能走到 OFFSET 分支,實得 {sqliteSequence.Count} 筆。");

            // OFFSET/FETCH vs LIMIT:逐頁序列(account_code 與 tb_s/gl_s/diff/not_in_tb)須完全相同。
            Assert.Equal(sqliteSequence, sqlServerSequence);
        }
        finally
        {
            await DropAllProjectDatabasesAsync(sqlServerHost.ProjectsRoot, connectionString);
        }
    }

    /// <summary>逐頁帶 nextCursor 直接走訪全科目 repo 到底,回傳 account_code|tb_s|gl_s|diff|not_in_tb 序列。</summary>
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
                $"{r.AccountCode}|{r.TbAmountScaled}|{r.GlAmountScaled}|{r.DiffScaled}|{r.NotInTb}"));
            cursor = page.NextCursor;
        } while (cursor is not null);

        return all;
    }

    /// <summary>
    /// 低頻科目(C9)prescreen 計數 provider 等價:同一 demo 案件分別於 SQLite 與 SQL Server 跑 prescreen.run,
    /// 斷言 lowFrequencyAccount.count 完全相同。證明 GROUP BY/HAVING/COUNT(*) 子查詢述詞跨 provider 等價。
    /// 無 LocalDB → 跳過(沿用既有閘控)。
    /// </summary>
    [SqlServerFact]
    public async Task PrescreenLowFrequencyAccount_IsEquivalentAcrossProviders()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 跳過
        }

        using var sqliteHost = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(sqliteHost);
        var sqlitePrescreen = await sqliteHost.DispatchAsync("prescreen.run");
        var sqliteCount = sqlitePrescreen.GetProperty("lowFrequencyAccount").GetProperty("count").GetRawText();

        using var sqlServerHost = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            await DemoProjectPipeline.SetupAsync(sqlServerHost, databaseProvider: "sqlServer");
            var sqlServerPrescreen = await sqlServerHost.DispatchAsync("prescreen.run");
            var sqlServerCount = sqlServerPrescreen.GetProperty("lowFrequencyAccount").GetProperty("count").GetRawText();

            Assert.Equal(sqliteCount, sqlServerCount);
        }
        finally
        {
            await DropAllProjectDatabasesAsync(sqlServerHost.ProjectsRoot, connectionString);
        }
    }

    /// <summary>
    /// customAccountEntryCount filterHits 分頁 provider 等價:同一 demo 案件 commit 一個 maxEntries 夠大
    /// (= 母體列數 → demo 所有科目全命中)的自訂低頻科目情境,以小 pageSize 走訪
    /// query.filterHitsPage 到底,斷言兩 provider 回傳的 doc|lineItem 序列完全相同(SequenceEqual)。
    /// 證明 C9 自訂門檻軌的 GROUP BY/HAVING/COUNT(*) 子查詢述詞 + entry_id 游標跨 provider 等價。
    /// >pageSize 擋偽綠。無 LocalDB → 跳過。
    /// </summary>
    [SqlServerFact]
    public async Task CustomAccountEntryCountHitsPage_WalkAllPages_IsEquivalentAcrossProviders()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 跳過
        }

        const int pageSize = 50;

        using var sqliteHost = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(sqliteHost);
        await sqliteHost.DispatchAsync("filter.commit", CustomAccountEntryCountScenarioPayload());
        var sqliteSequence = await WalkFilterHitsPagesAsync(sqliteHost, pageSize);

        using var sqlServerHost = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            await DemoProjectPipeline.SetupAsync(sqlServerHost, databaseProvider: "sqlServer");
            await sqlServerHost.DispatchAsync("filter.commit", CustomAccountEntryCountScenarioPayload());
            var sqlServerSequence = await WalkFilterHitsPagesAsync(sqlServerHost, pageSize);

            Assert.True(sqliteSequence.Count > pageSize,
                $"母體須產生跨頁命中(>{pageSize} 筆)才能走到 OFFSET 分支,實得 {sqliteSequence.Count} 筆。");
            Assert.Equal(sqliteSequence, sqlServerSequence);
        }
        finally
        {
            await DropAllProjectDatabasesAsync(sqlServerHost.ProjectsRoot, connectionString);
        }
    }

    /// <summary>門檻取母體列數(≥ 任一科目筆數)→ demo 所有科目全命中,跨頁命中 > pageSize 擋偽綠。</summary>
    private const int AllMatchingMaxEntries = DemoDataFactory.GlVoucherCount * DemoDataFactory.LinesPerVoucher;

    /// <summary>customAccountEntryCount 情境(maxEntries=母體列數 → demo 所有科目全命中),供 C9 自訂軌 parity 共用。</summary>
    private static string CustomAccountEntryCountScenarioPayload() =>
        JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name = "低頻科目(自訂)", rationale = "雙 provider 等價",
                groups = new[] { new { join = "and", rules = new[] {
                    new { join = "and", type = "customAccountEntryCount", maxEntries = AllMatchingMaxEntries } } } } } }
        });

    /// <summary>customPreparerEntryCount 情境（maxEntries=母體列數 → demo 編製者全命中），供低頻編製者自訂軌 parity 共用。</summary>
    private static string CustomPreparerEntryCountScenarioPayload() =>
        JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name = "低頻編製者(自訂)", rationale = "雙 provider 等價",
                groups = new[] { new { join = "and", rules = new[] {
                    new { join = "and", type = "customPreparerEntryCount", maxEntries = AllMatchingMaxEntries } } } } } }
        });

    /// <summary>backdatedPosting 情境(demo 母體確有命中),供 filterHits parity 兩 provider 共用。</summary>
    private static string BackdatedScenarioPayload() =>
        JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name = "提前過帳", rationale = "雙 provider 等價",
                groups = new[] { new { join = "and", rules = new[] {
                    new { join = "and", type = "prescreen", prescreenKey = "backdatedPosting" } } } } } }
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
                all.Add($"{r.GetProperty("documentNumber").GetString()}|{r.GetProperty("lineItem").GetString()}");
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
                all.Add($"{r.GetProperty("documentNumber").GetString()}|" +
                        $"{r.GetProperty("accountCode").GetString()}|" +
                        $"{r.GetProperty("accountName").GetString()}|" +
                        $"{r.GetProperty("debit").GetRawText()}|" +
                        $"{r.GetProperty("credit").GetRawText()}|" +
                        $"{r.GetProperty("postDate").GetString()}|" +
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
            await TempSqlServerProject.DropDatabaseAsync(connectionString, Path.GetFileName(dir));
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
            // 2026-10-04 第 3 批 L12 裁定 sourceColumn 必填；保留資料庫等價比對的原斷言。
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
        metrics["validate.docBalance.unbalancedDocumentCount"] =
            validate.GetProperty("docBalanceTest").GetProperty("unbalancedDocumentCount").GetRawText();
        var nullRecords = validate.GetProperty("nullRecordsTest");
        metrics["validate.null.account"] = nullRecords.GetProperty("nullAccountCount").GetRawText();
        metrics["validate.null.document"] = nullRecords.GetProperty("nullDocumentCount").GetRawText();
        metrics["validate.null.description"] = nullRecords.GetProperty("nullDescriptionCount").GetRawText();
        metrics["validate.null.outOfRangeDate"] = nullRecords.GetProperty("outOfRangeDateCount").GetRawText();

        var prescreen = await host.DispatchAsync("prescreen.run");
        metrics["prescreen.suspiciousKeywords"] = prescreen.GetProperty("suspiciousKeywords").GetProperty("count").GetRawText();
        metrics["prescreen.postPeriodApproval"] = prescreen.GetProperty("postPeriodApproval").GetProperty("count").GetRawText();
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
        metrics["prescreen.lowFrequencyPreparer.count"] = prescreen.GetProperty("lowFrequencyPreparer").GetProperty("count").GetRawText();
        metrics["prescreen.lowFrequencyAccount.count"] = prescreen.GetProperty("lowFrequencyAccount").GetProperty("count").GetRawText();
        metrics["prescreen.backdatedPosting.count"] = prescreen.GetProperty("backdatedPosting").GetProperty("count").GetRawText();
        metrics["prescreen.rareAccounts.distinct"] = prescreen.GetProperty("rareAccounts").GetProperty("distinctAccountCount").GetRawText();
        var creators = prescreen.GetProperty("creatorSummary").GetProperty("creators").EnumerateArray().ToList();
        metrics["prescreen.creatorSummary.count"] = creators.Count.ToString();
        metrics["prescreen.creatorSummary.entryTotal"] = creators.Sum(c => c.GetProperty("entryCount").GetInt64()).ToString();
        metrics["prescreen.creatorSummary.rows"] =
            prescreen.GetProperty("creatorSummary").GetProperty("creators").GetRawText();
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
                        rules = new object[]
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
            .Select(r => $"{r.GetProperty("documentNumber").GetString()}|{r.GetProperty("lineItem").GetString()}|{r.GetProperty("amount").GetRawText()}|{r.GetProperty("drCr").GetString()}")
            .ToList();
        metrics["filter.previewRows"] = string.Join(";", previewRows);

        return metrics;
    }

    /// <summary>
    /// 自動編號本機 provider 等價：lineID 未對應的 GL 批次（多傳票、每傳票多列、交錯送入）
    /// 於 SQLite 與 DuckDB 各自投影，斷言 (document_number, source_row_number, line_item)
    /// 序列完全一致，且符合既有逐傳票 ROW_NUMBER 手算 oracle。本測試不掛外部後端 gate。
    /// </summary>
    [Fact]
    public async Task GlAutoNumbering_LineIdUnmapped_IsEquivalentAcrossLocalProviders()
    {
        using var root = new TempProjectRoot();
        var sqliteDb = new SqliteProjectDatabase(new JetProjectFolder(root.Path));
        var sqliteSequence = await ProjectUnmappedLineIdWithLocalProviderAsync(sqliteDb);

        using var duckRoot = new TempProjectRoot();
        var duckDb = new DuckDbProjectDatabase(new JetProjectFolder(duckRoot.Path));
        var duckDbSequence = await ProjectUnmappedLineIdWithLocalProviderAsync(duckDb);

        Assert.Equal(ExpectedAutoNumberingSequence(), sqliteSequence);
        Assert.Equal(sqliteSequence, duckDbSequence);
    }

    /// <summary>
    /// 自動編號的 SQL Server 延伸 gate：重用同一 staging、mapping 與既有手算 oracle，
    /// 比對 SQL Server 與 SQLite 本機基準。無 SQL Server 2022+（非 Express）時具名略過；
    /// SQLite↔DuckDB parity 仍由不掛 gate 的測試獨立執行。
    /// </summary>
    [SqlServerFact]
    public async Task GlAutoNumbering_LineIdUnmapped_SqlServerMatchesLocalBaseline()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 跳過
        }

        using var root = new TempProjectRoot();
        var sqliteDb = new SqliteProjectDatabase(new JetProjectFolder(root.Path));
        var sqliteSequence = await ProjectUnmappedLineIdWithLocalProviderAsync(sqliteDb);

        await using var sql = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(sql); // ProbeConnectionString 已成功，建庫亦應成功。

        const string batchId = "b1";
        var sqlSchemaPrefix = SqlServerProjectSchema.QualifierFor(sql.ProjectId);
        await using (var seed = sql.Database.CreateConnection(sql.ProjectId))
        {
            await seed.OpenAsync();
            await SeedGlStagingAsync(seed, batchId, AutoNumberingSeedRows(), sqlSchemaPrefix);
        }

        await new SqlServerGlRepository(sql.Database).ProjectStagingToTargetAsync(
            sql.ProjectId, batchId, DualSpec(), 10_000, DateParseOptions.Default, periodStart: DateOnly.MinValue, periodEnd: DateOnly.MaxValue,
            postingStatusMapped: false, postingStatusPolicy: null, committedUtc: DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        List<(string?, long, string?)> sqlServerSequence;
        await using (var read = sql.Database.CreateConnection(sql.ProjectId))
        {
            await read.OpenAsync();
            sqlServerSequence = await ReadLineItemSequenceAsync(read, sqlSchemaPrefix);
        }

        Assert.Equal(ExpectedAutoNumberingSequence(), sqliteSequence);
        Assert.Equal(sqliteSequence, sqlServerSequence);
    }

    private static async Task<List<(string?, long, string?)>> ProjectUnmappedLineIdWithLocalProviderAsync(
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

    /// <summary>未對應 lineID 的 GL 批次(DualSpec 用,僅借方有額)。</summary>
    private static GlMappingSpec DualSpec() => new(
        new Dictionary<string, string>
        {
            [GlMappingKeys.DocNum] = "doc",
            [GlMappingKeys.PostDate] = "date",
            [GlMappingKeys.AccNum] = "acc",
            [GlMappingKeys.AccName] = "name",
            [GlMappingKeys.Description] = "desc",
            [GlMappingKeys.DebitAmount] = "debit",
            [GlMappingKeys.CreditAmount] = "credit"
        },
        GlAmountMode.DualAmount);

    /// <summary>
    /// 播種 staging_gl_raw_row。SQL 為 ANSI、DbConnection 對兩 provider 通用;表名前綴依 provider 給:
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
                INSERT INTO {tablePrefix}staging_gl_raw_row (batch_id, row_number, source_no, source_row_number, row_json)
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
    /// 讀 (document_number, source_row_number, line_item),依文件/排序鍵還原(兩 provider 同查詢)。
    /// 表名前綴依 provider 給:SQLite 裸名(預設 <c>""</c>)、SQL Server schema 限定(<c>[prj_xxx].</c>)。
    /// </summary>
    private static async Task<List<(string?, long, string?)>> ReadLineItemSequenceAsync(
        DbConnection connection, string tablePrefix = "")
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT document_number, source_row_number, line_item
            FROM {tablePrefix}target_gl_entry
            ORDER BY document_number, source_row_number;
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
    /// 借貸不平明細與空值明細的 provider 等價：同一含不平傳票與空值列的 GL 母體，
    /// 分別投影到 SQLite 與 SQL Server，跑 validate.run 後斷言兩清單逐項相等。
    /// 無 LocalDB → 跳過（沿用既有閘控）。
    /// </summary>
    [SqlServerFact]
    public async Task ValidationDetail_UnbalancedAndNull_IsEquivalentAcrossProviders()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 跳過
        }

        // SQLite 路徑（oracle）
        using var sqliteHost = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(sqliteHost, ConfigureWorkbook);
        var sqliteResult = await sqliteHost.DispatchAsync("validate.run");

        // SQL Server 路徑（同一母體）
        using var sqlServerHost = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            await InlineWorkbookProject.SetupAsync(sqlServerHost, ConfigureWorkbook, databaseProvider: "sqlServer");
            var sqlServerResult = await sqlServerHost.DispatchAsync("validate.run");

            // UnbalancedDocuments 逐項比對（documentNumber / debit / credit / diff）
            var sqliteUnbalanced = sqliteResult.GetProperty("docBalanceTest")
                .GetProperty("unbalancedDocuments").EnumerateArray().ToList();
            var sqlServerUnbalanced = sqlServerResult.GetProperty("docBalanceTest")
                .GetProperty("unbalancedDocuments").EnumerateArray().ToList();

            Assert.Equal(sqliteUnbalanced.Count, sqlServerUnbalanced.Count);
            for (var i = 0; i < sqliteUnbalanced.Count; i++)
            {
                var s = sqliteUnbalanced[i];
                var q = sqlServerUnbalanced[i];
                Assert.Equal(s.GetProperty("documentNumber").GetString(),  q.GetProperty("documentNumber").GetString());
                Assert.Equal(s.GetProperty("debit").GetDecimal(),          q.GetProperty("debit").GetDecimal());
                Assert.Equal(s.GetProperty("credit").GetDecimal(),         q.GetProperty("credit").GetDecimal());
                Assert.Equal(s.GetProperty("diff").GetDecimal(),           q.GetProperty("diff").GetDecimal());
            }

            // NullRecordRows 逐項比對（documentNumber / accountCode / postDate / description / issues）
            var sqliteNullRows = sqliteResult.GetProperty("nullRecordsTest")
                .GetProperty("nullRows").EnumerateArray().ToList();
            var sqlServerNullRows = sqlServerResult.GetProperty("nullRecordsTest")
                .GetProperty("nullRows").EnumerateArray().ToList();

            Assert.Equal(sqliteNullRows.Count, sqlServerNullRows.Count);
            for (var i = 0; i < sqliteNullRows.Count; i++)
            {
                var s = sqliteNullRows[i];
                var q = sqlServerNullRows[i];
                Assert.Equal(s.GetProperty("documentNumber").GetString(),  q.GetProperty("documentNumber").GetString());
                Assert.Equal(s.GetProperty("accountCode").GetString(),     q.GetProperty("accountCode").GetString());
                Assert.Equal(s.GetProperty("postDate").GetString(),        q.GetProperty("postDate").GetString());
                Assert.Equal(s.GetProperty("description").GetString(),     q.GetProperty("description").GetString());
                // issues 陣列：逐元素比對（四個旗標轉字串後順序應相同）
                var sIssues = Enumerable.Range(0, s.GetProperty("issues").GetArrayLength())
                    .Select(j => s.GetProperty("issues")[j].GetString()!).ToArray();
                var qIssues = Enumerable.Range(0, q.GetProperty("issues").GetArrayLength())
                    .Select(j => q.GetProperty("issues")[j].GetString()!).ToArray();
                Assert.Equal(sIssues, qIssues);
            }

            // 純量計數也必須等價（不平傳票數、四項空值計數）
            Assert.Equal(
                sqliteResult.GetProperty("docBalanceTest").GetProperty("unbalancedDocumentCount").GetRawText(),
                sqlServerResult.GetProperty("docBalanceTest").GetProperty("unbalancedDocumentCount").GetRawText());
            Assert.Equal(
                sqliteResult.GetProperty("nullRecordsTest").GetProperty("nullAccountCount").GetRawText(),
                sqlServerResult.GetProperty("nullRecordsTest").GetProperty("nullAccountCount").GetRawText());
            Assert.Equal(
                sqliteResult.GetProperty("nullRecordsTest").GetProperty("nullDocumentCount").GetRawText(),
                sqlServerResult.GetProperty("nullRecordsTest").GetProperty("nullDocumentCount").GetRawText());
            Assert.Equal(
                sqliteResult.GetProperty("nullRecordsTest").GetProperty("nullDescriptionCount").GetRawText(),
                sqlServerResult.GetProperty("nullRecordsTest").GetProperty("nullDescriptionCount").GetRawText());
            Assert.Equal(
                sqliteResult.GetProperty("nullRecordsTest").GetProperty("outOfRangeDateCount").GetRawText(),
                sqlServerResult.GetProperty("nullRecordsTest").GetProperty("outOfRangeDateCount").GetRawText());
        }
        finally
        {
            await DropAllProjectDatabasesAsync(sqlServerHost.ProjectsRoot, connectionString);
        }
    }

    /// <summary>
    /// 共用的工作簿設定：含一張不平傳票（JV-001:借 300 貸 100）、一張平衡傳票（JV-002:借貸各 50）、
    /// 以及一列空科目（JV-003 第一列）用於觸發 nullRecords 旗標。
    /// JV-001 差額 200、JV-003 同時有 nullAccount；JV-002 不應出現在任何不平清單。
    /// </summary>
    private static void ConfigureWorkbook(InlineGlWorkbookBuilder builder) =>
        builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            // JV-001：借 300、貸 100 → 不平，差額 200
            .AddRow("JV-001", "2025-03-05", "1101", "現金",     "借方",   "300.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "貸方",   "100.00", 0)
            // JV-002：借 50、貸 50 → 平衡（不應出現在 unbalancedDocuments）
            .AddRow("JV-002", "2025-03-06", "1101", "現金",     "借方",   "50.00",  1)
            .AddRow("JV-002", "2025-03-06", "4101", "銷貨收入", "貸方",   "50.00",  0)
            // JV-003：空科目列 → 觸發 nullAccount
            .AddRow("JV-003", "2025-03-07", null,   null,       "空科目分錄", "10.00", 1)
            .AddRow("JV-003", "2025-03-07", "4101", "銷貨收入", "對方",   "10.00",  0);

    /// <summary>
    /// 科目配對全列匯出(E1 step15,匯出底稿 sheet 15,無 dispatcher action)provider 等價:
    /// 同一 demo 案件(GL/TB 已 commit)再 import.accountMapping.fromFile demo 檔(100 科目)後,
    /// 分別於 SQLite 與 SQL Server 直接建 <see cref="IAccountMappingExportRepository"/>(比照本檔
    /// <see cref="CreatorSummaryExport_FullList_IsEquivalentAcrossProviders"/>,此 query 無 action、
    /// 不走 dispatcher),取 <see cref="IAccountMappingExportRepository.FetchAllAsync"/>,斷言兩 provider
    /// 回傳的(GL_NUMBER|GL_NAME|STANDARDIZED|notInTb)序列完全相同(SequenceEqual)。其中 notInTb 旗標
    /// 即 sheet 寫字面「Not in TB」的單一事實來源,以 <see cref="ValidationProcedures.CompletenessDiffCte"/> 判定;
    /// 此測試證明該 CTE + CASE WHEN ... IN (SELECT ...) 子查詢 + account_code 排序跨 provider 等價。
    /// 科目數 >1 擋偽綠;無 LocalDB → 跳過(沿用既有閘控)。
    /// </summary>
    [SqlServerFact]
    public async Task AccountMappingExport_FullList_IsEquivalentAcrossProviders()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 跳過
        }

        // SQLite 路徑(oracle)——直接建 repo 指向 host 建立的同一專案庫。
        using var sqliteHost = new HandlerTestHost();
        var sqliteContext = await DemoProjectPipeline.SetupAsync(sqliteHost);
        await ImportDemoAccountMappingAsync(sqliteHost);
        var sqliteRepo = new LocalAccountMappingExportRepository(
            new SqliteProjectDatabase(new JetProjectFolder(sqliteHost.ProjectsRoot)));
        var sqliteList = FlattenAccountMapping(
            await sqliteRepo.FetchAllAsync(sqliteContext.ProjectId, "2025-01-01", "2025-12-31", CancellationToken.None));

        // SQL Server 路徑(同一母體)
        using var sqlServerHost = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            var sqlServerContext = await DemoProjectPipeline.SetupAsync(sqlServerHost, databaseProvider: "sqlServer");
            await ImportDemoAccountMappingAsync(sqlServerHost);
            // 同一 base 連線指向同一 instance;預設落在隔離測試庫 JET_Test，CreateConnection(projectId) 對應 host 已建的該專案 schema。
            var sqlServerRepo = new SqlServerAccountMappingExportRepository(
                new SqlServerProjectDatabase(new SqlServerConnectionOptions(connectionString)));
            var sqlServerList = FlattenAccountMapping(
                await sqlServerRepo.FetchAllAsync(sqlServerContext.ProjectId, "2025-01-01", "2025-12-31", CancellationToken.None));

            // demo 100 科目 → 基數 >1,證明非「單列 vs 單列」偽綠。
            Assert.True(sqliteList.Count > 1,
                $"全科目配對須有多列才能證明排序與 not-in-tb 判定,實得 {sqliteList.Count} 列。");
            Assert.Equal(sqliteList, sqlServerList);
        }
        finally
        {
            await DropAllProjectDatabasesAsync(sqlServerHost.ProjectsRoot, connectionString);
        }
    }

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
        rows.Select(r => $"{r.AccountCode}|{r.AccountName}|{r.Category}|{r.NotInTb}").ToList();

    /// <summary>
    /// 行事曆(假日 + 補班)逐日讀回(E1 step14,匯出底稿 sheet 14,無 dispatcher action)provider 等價:
    /// 同一 demo 案件(管線已匯入 demo 假日 + 補班至 staging_calendar_raw_day)分別於 SQLite 與 SQL Server
    /// 直接建 <see cref="ICalendarExportRepository"/>(比照本檔 account/creator export parity,此 query 無 action),
    /// 對 Holiday 與 Makeup 兩種 day_type 各取 <see cref="ICalendarExportRepository.FetchDaysAsync"/>,
    /// 斷言兩 provider 合併後的(dayType|date|name)序列完全相同(SequenceEqual)。
    /// 證明 WHERE day_type = @type + 日期排序跨 provider 等價。假日數 >1 擋偽綠;無 LocalDB → 跳過。
    /// </summary>
    [SqlServerFact]
    public async Task CalendarExport_HolidaysAndMakeupDays_IsEquivalentAcrossProviders()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 跳過
        }

        // SQLite 路徑(oracle)——demo 管線 importCalendar:true 已寫入假日 + 補班。
        using var sqliteHost = new HandlerTestHost();
        var sqliteContext = await DemoProjectPipeline.SetupAsync(sqliteHost);
        var sqliteRepo = new LocalCalendarExportRepository(
            new SqliteProjectDatabase(new JetProjectFolder(sqliteHost.ProjectsRoot)));
        var (sqliteSequence, sqliteHolidayCount) = await FlattenCalendarAsync(sqliteRepo, sqliteContext.ProjectId);

        // SQL Server 路徑(同一母體)
        using var sqlServerHost = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            var sqlServerContext = await DemoProjectPipeline.SetupAsync(sqlServerHost, databaseProvider: "sqlServer");
            var sqlServerRepo = new SqlServerCalendarExportRepository(
                new SqlServerProjectDatabase(new SqlServerConnectionOptions(connectionString)));
            var (sqlServerSequence, _) = await FlattenCalendarAsync(sqlServerRepo, sqlServerContext.ProjectId);

            // demo 假日多筆(>1)→ 證明非「單列 vs 單列」偽綠;序列含 Holiday + Makeup 兩段。
            Assert.True(sqliteHolidayCount > 1,
                $"假日須有多日才能證明日期排序,實得 {sqliteHolidayCount} 日。");
            Assert.Equal(sqliteSequence, sqlServerSequence);
        }
        finally
        {
            await DropAllProjectDatabasesAsync(sqlServerHost.ProjectsRoot, connectionString);
        }
    }

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
