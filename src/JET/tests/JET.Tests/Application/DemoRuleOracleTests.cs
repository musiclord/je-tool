using System.Text.Json;
using JET.Application;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// demo 測試案件的規則 oracle(spec 2026-06-21 §C.3):完整建置 demo 專案後跑 prescreen.run /
/// validate.run,斷言每條規則的精確命中數 == DemoDataFactory seed 常數;並以「直接查 DB 的
/// 獨立 set-based SQL」交叉重算(與述詞同義但獨立書寫,避免自證)。
///
/// 此測試即「跑測試案件 → 驗收每條規則」的固定程序:每條規則一列 oracle 斷言,命中數以
/// target_gl_entry 的「列」計(每張傳票固定 2 行,1 借 1 貸)。雙 provider:SQLite 為主 oracle,
/// 另以 [SqlServerFact] 鏡射計數等價(LocalDB 閘控、乾淨 skip;含 Count>0 假綠防呆)。
///
/// wire 形狀已逐項核對 PrescreenRunHandler / ValidateRunHandler(計數皆 long → GetInt64):
/// prescreen 計數鍵為 "<rule>.count";validate 計數鍵為 diffAccountCount /
/// unbalancedDocumentCount / null*Count(非 plan 草稿的 diffCount / unbalancedCount / nullDescription)。
/// </summary>
public sealed class DemoRuleOracleTests
{
    /// <summary>seed 張數 → 命中「列」數(每張 2 行;兩行皆 tag 的規則命中 = 張數 × 2)。</summary>
    private static long Lines(int vouchers) => vouchers * DemoDataFactory.LinesPerVoucher;

    private static long Count(JsonElement prescreen, string key)
        => prescreen.GetProperty(key).GetProperty("count").GetInt64();

    private static string Status(JsonElement prescreen, string key)
        => prescreen.GetProperty(key).GetProperty("status").GetString()!;

    private static async Task<(JsonElement Prescreen, DemoProjectPipeline.Context Ctx)> RunPrescreenAsync(
        HandlerTestHost host)
    {
        // 完整管線:GL/TB/calendar/科目配對/授權名單 + GL/TB commit(Task 6 預設)。
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        var prescreen = await host.DispatchAsync("prescreen.run");
        return (prescreen, ctx);
    }

    // ============================ 預篩選:逐規則精確命中 ============================

    [Fact]
    public async Task Prescreen_EveryRule_HitsExactOracleCount()
    {
        using var host = new HandlerTestHost();
        var (p, _) = await RunPrescreenAsync(host);

        // R1 期末後核准:兩行皆 tag(同傳票核准日)。
        Assert.Equal(Lines(DemoDataFactory.PostPeriodApprovalVouchers), Count(p, "postPeriodApproval"));
        // R2 摘要關鍵字:僅借方行命中(張數 = 列數)。
        Assert.Equal((long)DemoDataFactory.SuspiciousKeywordVouchers, Count(p, "suspiciousKeywords"));
        // R3 未預期借貸組合(否定面):貸收入無正常對方 → 只標 Revenue 貸方列(每張 1 列 = 張數)。
        Assert.Equal((long)DemoDataFactory.UnexpectedPairVouchers, Count(p, "unexpectedAccountPair"));
        // R4 連續零尾數:借貸金額皆為 2,000,000(兩行皆命中)。
        Assert.Equal(Lines(DemoDataFactory.TrailingZerosVouchers), Count(p, "trailingZeros"));

        // R7 週末:過帳/核准分屬不同 seed,各以 postingCount / approvalCount 回報。
        Assert.Equal(
            Lines(DemoDataFactory.WeekendPostingVouchers),
            p.GetProperty("weekendActivity").GetProperty("postingCount").GetInt64());
        Assert.Equal(
            Lines(DemoDataFactory.WeekendApprovalVouchers),
            p.GetProperty("weekendActivity").GetProperty("approvalCount").GetInt64());

        // R8 假日:過帳/核准分屬不同 seed。
        Assert.Equal(
            Lines(DemoDataFactory.HolidayPostingVouchers),
            p.GetProperty("holidayActivity").GetProperty("postingCount").GetInt64());
        Assert.Equal(
            Lines(DemoDataFactory.HolidayApprovalVouchers),
            p.GetProperty("holidayActivity").GetProperty("approvalCount").GetInt64());

        // descNull 摘要空白:僅借方行空白(張數 = 列數)。
        Assert.Equal((long)DemoDataFactory.BlankDescriptionVouchers, Count(p, "blankDescription"));
        // R9 回溯過帳:兩行皆命中(同傳票登錄日)。
        Assert.Equal(Lines(DemoDataFactory.BackdatedVouchers), Count(p, "backdatedPosting"));
        // R10 非授權編製人員:兩行同 created_by 皆命中。
        Assert.Equal(Lines(DemoDataFactory.NonAuthorizedVouchers), Count(p, "nonAuthorizedPreparer"));
        // R11 低頻編製者:兩行同 created_by 皆命中。
        Assert.Equal(Lines(DemoDataFactory.LowFrequencyPreparerVouchers), Count(p, "lowFrequencyPreparer"));
        // R12 低頻科目:僅稀有借方行命中(各稀有科目 RareAccountVouchersEach 張,貸方走共用科目)。
        Assert.Equal(
            (long)(DemoDataFactory.RareAccountCount * DemoDataFactory.RareAccountVouchersEach),
            Count(p, "lowFrequencyAccount"));
    }

    [Fact]
    public async Task Prescreen_EveryRule_StatusIsGreen()
    {
        using var host = new HandlerTestHost();
        var (p, _) = await RunPrescreenAsync(host);

        foreach (var key in new[]
        {
            "postPeriodApproval", "suspiciousKeywords", "unexpectedAccountPair", "trailingZeros",
            "blankDescription", "backdatedPosting", "nonAuthorizedPreparer",
            "lowFrequencyPreparer", "lowFrequencyAccount"
        })
        {
            Assert.Equal("V", Status(p, key));
        }

        // 彙總/雙計數規則:狀態 V(non-count wrapper)。
        Assert.Equal("V", p.GetProperty("weekendActivity").GetProperty("status").GetString());
        Assert.Equal("V", p.GetProperty("holidayActivity").GetProperty("status").GetString());
        Assert.Equal("V", p.GetProperty("creatorSummary").GetProperty("status").GetString());
        Assert.Equal("V", p.GetProperty("rareAccounts").GetProperty("status").GetString());
    }

    // ============================ 獨立重算交叉驗證(SQLite) ============================
    // 重算 SQL 與述詞同義但獨立書寫:不照抄 GlRulePredicates 的字面,改以等價但不同形式的查詢,
    // 兼證 handler 計數正確「且」seed 確實落地為設計命中數。

    [Fact]
    public async Task Prescreen_TrailingZeros_IndependentRecountMatches()
    {
        using var host = new HandlerTestHost();
        var (p, ctx) = await RunPrescreenAsync(host);

        // 權威述詞先取得主單位整數再取模；此處用整數商回乘表達「主單位整數為 10^6 倍數」，
        // 寫法獨立且刻意不要求小數尾數為 0。0 排除，母體固定查核期間。
        var recount = await DemoProjectPipeline.QueryScalarAsync(
            host, ctx.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry " +
            "WHERE post_date BETWEEN '2025-01-01' AND '2025-12-31' " +
            "AND CAST(ABS(amount_scaled) / 10000 AS INTEGER) <> 0 " +
            "AND (CAST(ABS(amount_scaled) / 10000 AS INTEGER) / 1000000) * 1000000 " +
            "= CAST(ABS(amount_scaled) / 10000 AS INTEGER);");

        Assert.Equal(recount, Count(p, "trailingZeros"));
        Assert.Equal(Lines(DemoDataFactory.TrailingZerosVouchers), recount);
    }

    [Fact]
    public async Task Prescreen_Backdated_IndependentRecountMatches()
    {
        using var host = new HandlerTestHost();
        var (p, ctx) = await RunPrescreenAsync(host);

        // 述詞用「post_date < voucher_date」;此處改以 julianday 日差 > 0 表達「登錄日晚於過帳日」,
        // 語意同義、寫法獨立(seed 為過帳日 +3 天)。
        var recount = await DemoProjectPipeline.QueryScalarAsync(
            host, ctx.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry " +
            "WHERE voucher_date IS NOT NULL AND julianday(voucher_date) - julianday(post_date) > 0;");

        Assert.Equal(recount, Count(p, "backdatedPosting"));
        Assert.Equal(Lines(DemoDataFactory.BackdatedVouchers), recount);
    }

    [Fact]
    public async Task Prescreen_LowFrequencyAccount_IndependentRecountMatches()
    {
        using var host = new HandlerTestHost();
        var (p, ctx) = await RunPrescreenAsync(host);

        // 述詞用「account_code IN (... HAVING COUNT(*) <= 11)」;此處改以 JOIN 一個「列數 < 12」的
        // 分組子查詢表達同一「低頻科目」集合,語意同義、寫法獨立。
        var recount = await DemoProjectPipeline.QueryScalarAsync(
            host, ctx.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry e " +
            "JOIN (SELECT account_code FROM target_gl_entry GROUP BY account_code HAVING COUNT(*) < 12) rare " +
            "  ON e.account_code = rare.account_code;");

        Assert.Equal(recount, Count(p, "lowFrequencyAccount"));
        Assert.Equal(
            (long)(DemoDataFactory.RareAccountCount * DemoDataFactory.RareAccountVouchersEach),
            recount);
    }

    [Fact]
    public async Task Prescreen_NonAuthorizedPreparer_IndependentRecountMatches()
    {
        using var host = new HandlerTestHost();
        var (p, ctx) = await RunPrescreenAsync(host);

        // 述詞為「created_by 非空且 NOT IN 授權清單(target_authorized_preparer.name)」;此處改以
        // LEFT JOIN 名單表 + 「無匹配(name IS NULL)」的 anti-join 表達同一「不在授權清單」集合,
        // 語意同義、寫法獨立(NOT IN ↔ anti-join)。demo 唯一非授權者為「未授權者」。
        var recount = await DemoProjectPipeline.QueryScalarAsync(
            host, ctx.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry e " +
            "LEFT JOIN target_authorized_preparer a ON TRIM(e.created_by) = a.name " +
            "WHERE e.created_by IS NOT NULL AND TRIM(e.created_by) <> '' AND a.name IS NULL;");

        Assert.Equal(recount, Count(p, "nonAuthorizedPreparer"));
        Assert.Equal(Lines(DemoDataFactory.NonAuthorizedVouchers), recount);
    }

    // ============================ 反向防呆:非目標 seed 不污染他規則 ============================

    [Fact]
    public async Task Prescreen_WeekendAndHoliday_AreIndependent_NoCrossContamination()
    {
        using var host = new HandlerTestHost();
        var (p, _) = await RunPrescreenAsync(host);

        // 週末 seed(過帳/核准)用非假日週六;假日 seed 用平日假日 → 兩者不得互相加成。
        // 若 weekend 種子誤落在假日、或 holiday 種子誤落在週末,以下任一等式會破。
        var weekend = p.GetProperty("weekendActivity");
        var holiday = p.GetProperty("holidayActivity");

        Assert.Equal(Lines(DemoDataFactory.WeekendPostingVouchers), weekend.GetProperty("postingCount").GetInt64());
        Assert.Equal(Lines(DemoDataFactory.HolidayPostingVouchers), holiday.GetProperty("postingCount").GetInt64());
        Assert.Equal(Lines(DemoDataFactory.WeekendApprovalVouchers), weekend.GetProperty("approvalCount").GetInt64());
        Assert.Equal(Lines(DemoDataFactory.HolidayApprovalVouchers), holiday.GetProperty("approvalCount").GetInt64());
    }

    // ============================ 驗證(validate.run)oracle ============================

    [Fact]
    public async Task Validate_CleanExceptDesignedNullDescription()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var v = await host.DispatchAsync("validate.run");

        // V1 完整性:TB 由 GL 推導 → 差異科目數 = 0(spec C.3 V1)。
        // part(a) 控制總數只由 GL 投影同交易 upsert；後續 TB 投影不屬其上游，
        // 不得由共用結果失效清除。完整管線完成 GL/TB commit 後仍應核對成功。
        var completeness = v.GetProperty("completenessTest");
        Assert.Equal(0, completeness.GetProperty("diffAccountCount").GetInt64());
        var partA = completeness.GetProperty("partA");
        var rawPopulationRows =
            (long)DemoDataFactory.GlVoucherCount * DemoDataFactory.LinesPerVoucher;
        var excludedByPeriod =
            (long)DemoDataFactory.OutOfPeriodVouchers * DemoDataFactory.LinesPerVoucher;
        var effectivePopulationRows = rawPopulationRows - excludedByPeriod;
        Assert.Equal(
            effectivePopulationRows,
            partA.GetProperty("eligibleSource").GetProperty("rowCount").GetInt64());
        Assert.Equal(
            effectivePopulationRows,
            partA.GetProperty("effectiveTarget").GetProperty("rowCount").GetInt64());
        Assert.True(partA.GetProperty("rowCountMatch").GetBoolean());
        Assert.True(partA.GetProperty("amountMatch").GetBoolean());

        var population = v.GetProperty("populationSummary");
        Assert.Equal(rawPopulationRows, population.GetProperty("raw").GetProperty("rowCount").GetInt64());
        Assert.Equal(
            effectivePopulationRows,
            population.GetProperty("effective").GetProperty("rowCount").GetInt64());
        var excluded = population.GetProperty("excluded");
        Assert.Equal(excludedByPeriod, excluded.GetProperty("rowCount").GetInt64());
        Assert.Equal(excludedByPeriod, excluded.GetProperty("byPeriodCount").GetInt64());
        Assert.Equal(0, excluded.GetProperty("byPostingStatusCount").GetInt64());

        // V2 借貸不平:每張 seed/baseline 皆平衡 → 0 筆。
        Assert.Equal(0, v.GetProperty("docBalanceTest").GetProperty("unbalancedDocumentCount").GetInt64());

        // V4 空值紀錄:刻意埋兩種 — 摘要空白(= blankDescription seed)+ 核准日離期(期末後核准種子
        // 20 張、核准日 2026-01-15 在期末後,每張 2 列 → 40 列);科目/傳票號為 0。
        // 「日期區間外」第四旗標以核准日判定(2026-06-23 決策)。
        var nullRecords = v.GetProperty("nullRecordsTest");
        Assert.Equal(
            (long)DemoDataFactory.BlankDescriptionVouchers,
            nullRecords.GetProperty("nullDescriptionCount").GetInt64());
        Assert.Equal(0, nullRecords.GetProperty("nullAccountCount").GetInt64());
        Assert.Equal(0, nullRecords.GetProperty("nullDocumentCount").GetInt64());
        Assert.Equal(
            2L * DemoDataFactory.PostPeriodApprovalVouchers,
            nullRecords.GetProperty("outOfRangeDateCount").GetInt64());
    }

    [Fact]
    public async Task Validate_InfSampling_ReturnsRequestedSampleSize()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var v = await host.DispatchAsync("validate.run");

        // V3 INF 抽樣:母體(14,000 列)≫ 樣本 → 樣本數 = 正式報告固定 59。
        var inf = v.GetProperty("infSamplingTest");
        Assert.Equal("V", inf.GetProperty("status").GetString());
        Assert.Equal(59, inf.GetProperty("sampleSize").GetInt32());
    }

    // ============================ 雙 provider 平價(SQL Server) ============================

    [SqlServerFact]
    public async Task Prescreen_EveryRule_HitsExactOracleCount_SqlServer()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server LocalDB → 乾淨 skip(由 [SqlServerFact] 標記)。
        }

        using var host = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            var ctx = await DemoProjectPipeline.SetupAsync(host, databaseProvider: "sqlServer");

            // 假綠防呆:先證母體確實落地(14,000 列),否則「0 == 0」會偽綠。
            // 注意:DemoProjectPipeline.QueryScalarAsync 寫死走 SQLite,SQL Server 路徑須直接對
            // SqlServerProjectDatabase 查詢(同一 base 連線 → host 已建的 JET_{projectId} 庫)。
            // schema-per-project：直接查 SQL Server 須以 [schema]. 限定 target_gl_entry（裸名會解析到 dbo）。
            var populationRows = await QuerySqlServerScalarAsync(
                connectionString, ctx.ProjectId,
                $"SELECT COUNT_BIG(*) FROM {SqlServerProjectSchema.QualifierFor(ctx.ProjectId)}target_gl_entry;");
            Assert.Equal((long)DemoDataFactory.GlVoucherCount * DemoDataFactory.LinesPerVoucher, populationRows);

            var p = await host.DispatchAsync("prescreen.run");

            // 鏡射 SQLite oracle 的關鍵子集(涵蓋四類述詞:日期、科目對、金額、名單反查、子查詢門檻)。
            Assert.Equal(Lines(DemoDataFactory.PostPeriodApprovalVouchers), Count(p, "postPeriodApproval"));
            Assert.Equal((long)DemoDataFactory.SuspiciousKeywordVouchers, Count(p, "suspiciousKeywords"));
            Assert.Equal((long)DemoDataFactory.UnexpectedPairVouchers, Count(p, "unexpectedAccountPair"));
            Assert.Equal(Lines(DemoDataFactory.TrailingZerosVouchers), Count(p, "trailingZeros"));
            Assert.Equal(Lines(DemoDataFactory.BackdatedVouchers), Count(p, "backdatedPosting"));
            Assert.Equal(Lines(DemoDataFactory.NonAuthorizedVouchers), Count(p, "nonAuthorizedPreparer"));
            Assert.Equal(Lines(DemoDataFactory.LowFrequencyPreparerVouchers), Count(p, "lowFrequencyPreparer"));
            Assert.Equal(
                (long)(DemoDataFactory.RareAccountCount * DemoDataFactory.RareAccountVouchersEach),
                Count(p, "lowFrequencyAccount"));
            Assert.Equal(
                Lines(DemoDataFactory.WeekendPostingVouchers),
                p.GetProperty("weekendActivity").GetProperty("postingCount").GetInt64());
            Assert.Equal(
                Lines(DemoDataFactory.HolidayPostingVouchers),
                p.GetProperty("holidayActivity").GetProperty("postingCount").GetInt64());
        }
        finally
        {
            // project.json 必然落地;以資料夾名(= projectId)逐一 DROP(沿用既有平價測試清理慣例)。
            await DropAllProjectDatabasesAsync(host.ProjectsRoot, connectionString);
        }
    }

    [SqlServerFact]
    public async Task Validate_CleanExceptDesignedNullDescription_SqlServer()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server LocalDB → 乾淨 skip。
        }

        using var host = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            var ctx = await DemoProjectPipeline.SetupAsync(host, databaseProvider: "sqlServer");

            // schema-per-project：直接查 SQL Server 須以 [schema]. 限定 target_gl_entry（裸名會解析到 dbo）。
            var populationRows = await QuerySqlServerScalarAsync(
                connectionString, ctx.ProjectId,
                $"SELECT COUNT_BIG(*) FROM {SqlServerProjectSchema.QualifierFor(ctx.ProjectId)}target_gl_entry;");
            Assert.Equal((long)DemoDataFactory.GlVoucherCount * DemoDataFactory.LinesPerVoucher, populationRows);

            var v = await host.DispatchAsync("validate.run");

            Assert.Equal(0, v.GetProperty("completenessTest").GetProperty("diffAccountCount").GetInt64());
            Assert.Equal(0, v.GetProperty("docBalanceTest").GetProperty("unbalancedDocumentCount").GetInt64());

            var nullRecords = v.GetProperty("nullRecordsTest");
            Assert.Equal(
                (long)DemoDataFactory.BlankDescriptionVouchers,
                nullRecords.GetProperty("nullDescriptionCount").GetInt64());
            Assert.Equal(0, nullRecords.GetProperty("nullAccountCount").GetInt64());
            Assert.Equal(0, nullRecords.GetProperty("nullDocumentCount").GetInt64());
            // 核准日離期 = 期末後核准種子張數 × 每張 2 列(「日期區間外」旗標以核准日判定,2026-06-23 決策)。
            Assert.Equal(
                2L * DemoDataFactory.PostPeriodApprovalVouchers,
                nullRecords.GetProperty("outOfRangeDateCount").GetInt64());
        }
        finally
        {
            await DropAllProjectDatabasesAsync(host.ProjectsRoot, connectionString);
        }
    }

    // ============================ 第二本地引擎鏡射(DuckDB;不掛閘控、所有環境實跑) ============================
    // 比照上方 SqlServer 鏡射的寫法(同 oracle 常數),但 DuckDB 是本地檔引擎、不需外部後端,故 [Fact]。

    [Fact]
    public async Task Prescreen_EveryRule_HitsExactOracleCount_DuckDb()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host, databaseProvider: "duckdb");

        // 假綠防呆:先證母體確實落地(14,000 列)。DuckDB 路徑直接對 DuckDbProjectDatabase 查(裸表名、本地檔)。
        var populationRows = await QueryDuckDbScalarAsync(
            host, ctx.ProjectId, "SELECT COUNT(*) FROM target_gl_entry;");
        Assert.Equal((long)DemoDataFactory.GlVoucherCount * DemoDataFactory.LinesPerVoucher, populationRows);

        var p = await host.DispatchAsync("prescreen.run");

        // 鏡射 SQLite oracle 的關鍵子集(涵蓋四類述詞:日期、科目對、金額、名單反查、子查詢門檻)。
        Assert.Equal(Lines(DemoDataFactory.PostPeriodApprovalVouchers), Count(p, "postPeriodApproval"));
        Assert.Equal((long)DemoDataFactory.SuspiciousKeywordVouchers, Count(p, "suspiciousKeywords"));
        Assert.Equal((long)DemoDataFactory.UnexpectedPairVouchers, Count(p, "unexpectedAccountPair"));
        Assert.Equal(Lines(DemoDataFactory.TrailingZerosVouchers), Count(p, "trailingZeros"));
        Assert.Equal(Lines(DemoDataFactory.BackdatedVouchers), Count(p, "backdatedPosting"));
        Assert.Equal(Lines(DemoDataFactory.NonAuthorizedVouchers), Count(p, "nonAuthorizedPreparer"));
        Assert.Equal(Lines(DemoDataFactory.LowFrequencyPreparerVouchers), Count(p, "lowFrequencyPreparer"));
        Assert.Equal(
            (long)(DemoDataFactory.RareAccountCount * DemoDataFactory.RareAccountVouchersEach),
            Count(p, "lowFrequencyAccount"));
        Assert.Equal(
            Lines(DemoDataFactory.WeekendPostingVouchers),
            p.GetProperty("weekendActivity").GetProperty("postingCount").GetInt64());
        Assert.Equal(
            Lines(DemoDataFactory.HolidayPostingVouchers),
            p.GetProperty("holidayActivity").GetProperty("postingCount").GetInt64());
    }

    [Fact]
    public async Task Validate_CleanExceptDesignedNullDescription_DuckDb()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host, databaseProvider: "duckdb");

        var populationRows = await QueryDuckDbScalarAsync(
            host, ctx.ProjectId, "SELECT COUNT(*) FROM target_gl_entry;");
        Assert.Equal((long)DemoDataFactory.GlVoucherCount * DemoDataFactory.LinesPerVoucher, populationRows);

        var v = await host.DispatchAsync("validate.run");

        Assert.Equal(0, v.GetProperty("completenessTest").GetProperty("diffAccountCount").GetInt64());
        Assert.Equal(0, v.GetProperty("docBalanceTest").GetProperty("unbalancedDocumentCount").GetInt64());

        var nullRecords = v.GetProperty("nullRecordsTest");
        Assert.Equal(
            (long)DemoDataFactory.BlankDescriptionVouchers,
            nullRecords.GetProperty("nullDescriptionCount").GetInt64());
        Assert.Equal(0, nullRecords.GetProperty("nullAccountCount").GetInt64());
        Assert.Equal(0, nullRecords.GetProperty("nullDocumentCount").GetInt64());
        Assert.Equal(
            2L * DemoDataFactory.PostPeriodApprovalVouchers,
            nullRecords.GetProperty("outOfRangeDateCount").GetInt64());
    }

    /// <summary>
    /// DuckDB 端的獨立 scalar 防呆查詢:直接對 DuckDbProjectDatabase 開連線(本地檔、裸表名),回第一欄為 long。
    /// DemoProjectPipeline.QueryScalarAsync 寫死 SQLite,故 DuckDB 路徑另走此處(鏡射 QuerySqlServerScalarAsync)。
    /// </summary>
    private static async Task<long> QueryDuckDbScalarAsync(HandlerTestHost host, string projectId, string sql)
    {
        var database = new DuckDbProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    /// <summary>
    /// SQL Server 端的獨立 scalar 重算/防呆查詢:直接對 SqlServerProjectDatabase 開連線
    /// (同一 base 連線、預設落在隔離測試庫 JET_Test，對應 host 已建的該專案 schema),回第一欄為 long。
    /// DemoProjectPipeline.QueryScalarAsync 寫死 SQLite,故 SQL Server 路徑另走此處。
    /// </summary>
    private static async Task<long> QuerySqlServerScalarAsync(
        string connectionString, string projectId, string sql)
    {
        var database = new SqlServerProjectDatabase(new SqlServerConnectionOptions(connectionString));
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    /// <summary>逐一 DROP host 在本 instance 建立的所有專案庫(沿用 ProviderParityJourneyTests 清理慣例)。</summary>
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
}
