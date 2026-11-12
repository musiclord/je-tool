using System.Text.Json;
using JET.Application;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// demo 測試案件的規則 oracle:完整建置 demo 專案後跑 prescreen.run /
/// validate.run,斷言每條規則的精確命中數 == DemoDataFactory seed 常數;並以「直接查 DB 的
/// 獨立 set-based SQL」交叉重算(與述詞同義但獨立書寫,避免自證)。
///
/// 此測試即「跑測試案件 → 驗收每條規則」的固定程序:每條規則一列 oracle 斷言,命中數以
/// gl 的「列」計(每張傳票固定 2 行,1 借 1 貸)。
///
/// wire 形狀已逐項核對 PrescreenRunHandler / ValidateRunHandler(計數皆 long → GetInt64):
/// prescreen 計數鍵為 "&lt;rule&gt;.count";validate 計數鍵為 diffAccountCount /
/// unbalancedVoucherCount / null*Count。
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
        // 完整管線:GL/TB/calendar/科目配對/授權名單 + GL/TB commit。
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
        Assert.Equal(Lines(DemoDataFactory.PrepPeriodApprovalVouchers), Count(p, "prepPeriodApproval"));
        // R2 摘要關鍵字:僅借方行命中(張數 = 列數)。
        Assert.Equal((long)DemoDataFactory.SuspiciousKeywordVouchers, Count(p, "suspiciousKeywords"));
        // R3 未預期借貸組合(否定面):貸收入無正常對方,借方費用是正金額分錄 → 第四步照舊版列整張命中傳票(每張 2 列)。
        Assert.Equal(Lines(DemoDataFactory.UnexpectedPairVouchers), Count(p, "unexpectedAccountPair"));
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
        // 預篩選結果沒有 backdatedPosting。
        Assert.False(p.TryGetProperty("backdatedPosting", out _));
        // R10 非授權編製人員:兩行同 created_by 皆命中。
        Assert.Equal(Lines(DemoDataFactory.NonAuthorizedVouchers), Count(p, "nonAuthorizedPreparer"));
        // 第四步照舊版沒有編製分錄較少的人員與使用較少的科目這兩條規則,預篩選結果沒有 rarePreparer 與 rareAccount。
        Assert.False(p.TryGetProperty("rarePreparer", out _));
        Assert.False(p.TryGetProperty("rareAccount", out _));
    }

    [Fact]
    public async Task Prescreen_EveryRule_StatusIsGreen()
    {
        using var host = new HandlerTestHost();
        var (p, _) = await RunPrescreenAsync(host);

        foreach (var key in new[]
        {
            "prepPeriodApproval", "suspiciousKeywords", "unexpectedAccountPair", "trailingZeros",
            "blankDescription", "nonAuthorizedPreparer"
            // 第四步照舊版沒有編製分錄較少的人員與使用較少的科目這兩條規則,不列 rarePreparer 與 rareAccount。
        })
        {
            Assert.Equal("V", Status(p, key));
        }

        // 彙總/雙計數規則:狀態 V(non-count wrapper)。
        Assert.Equal("V", p.GetProperty("weekendActivity").GetProperty("status").GetString());
        Assert.Equal("V", p.GetProperty("holidayActivity").GetProperty("status").GetString());
        Assert.Equal("V", p.GetProperty("preparerSummary").GetProperty("status").GetString());
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
            "SELECT COUNT(*) FROM gl " +
            "WHERE gl_date BETWEEN '2025-01-01' AND '2025-12-31' " +
            "AND CAST(ABS(amount_scaled) / 10000 AS INTEGER) <> 0 " +
            "AND (CAST(ABS(amount_scaled) / 10000 AS INTEGER) / 1000000) * 1000000 " +
            "= CAST(ABS(amount_scaled) / 10000 AS INTEGER);");

        Assert.Equal(recount, Count(p, "trailingZeros"));
        Assert.Equal(Lines(DemoDataFactory.TrailingZerosVouchers), recount);
    }

    [Fact]
    public async Task Prescreen_NonAuthorizedPreparer_IndependentRecountMatches()
    {
        using var host = new HandlerTestHost();
        var (p, ctx) = await RunPrescreenAsync(host);

        // 述詞為「created_by 非空且 NOT IN 授權清單(authorized_preparer.name)」;此處改以
        // LEFT JOIN 名單表 + 「無匹配(name IS NULL)」的 anti-join 表達同一「不在授權清單」集合,
        // 語意同義、寫法獨立(NOT IN ↔ anti-join)。demo 唯一非授權者為「未授權者」。
        var recount = await DemoProjectPipeline.QueryScalarAsync(
            host, ctx.ProjectId,
            "SELECT COUNT(*) FROM gl e " +
            "LEFT JOIN authorized_preparer a ON TRIM(e.created_by) = a.name " +
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
    public async Task Validate_CleanExceptDesignedBlankDescription()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var v = await host.DispatchAsync("validate.run");

        // V1 完整性:TB 由 GL 推導 → 差異科目數 = 0。
        // 存入筆數與合計核對的控制總數只由 GL 投影同交易 upsert；後續 TB 投影不屬其上游，
        // 不得由共用結果失效清除。完整管線完成 GL/TB commit 後仍應核對成功。
        var completeness = v.GetProperty("completenessTest");
        Assert.Equal(0, completeness.GetProperty("diffAccountCount").GetInt64());
        var storedTotals = completeness.GetProperty("storedTotals");
        var rawPopulationRows =
            (long)DemoDataFactory.GlVoucherCount * DemoDataFactory.LinesPerVoucher;
        var excludedByPeriod =
            (long)DemoDataFactory.OutOfPeriodVouchers * DemoDataFactory.LinesPerVoucher;
        var glRows = rawPopulationRows - excludedByPeriod;
        Assert.Equal(
            glRows,
            storedTotals.GetProperty("eligibleSource").GetProperty("rowCount").GetInt64());
        Assert.Equal(
            glRows,
            storedTotals.GetProperty("gl").GetProperty("rowCount").GetInt64());
        Assert.True(storedTotals.GetProperty("rowCountMatch").GetBoolean());
        Assert.True(storedTotals.GetProperty("amountMatch").GetBoolean());

        var population = v.GetProperty("populationSummary");
        Assert.Equal(rawPopulationRows, population.GetProperty("raw").GetProperty("rowCount").GetInt64());
        Assert.Equal(
            glRows,
            population.GetProperty("gl").GetProperty("rowCount").GetInt64());
        var excluded = population.GetProperty("excluded");
        Assert.Equal(excludedByPeriod, excluded.GetProperty("rowCount").GetInt64());
        Assert.Equal(excludedByPeriod, excluded.GetProperty("byPeriodCount").GetInt64());
        Assert.Equal(0, excluded.GetProperty("byPostingStatusCount").GetInt64());

        // V2 借貸不平:每張 seed/baseline 皆平衡 → 0 筆。
        Assert.Equal(0, v.GetProperty("voucherBalanceTest").GetProperty("unbalancedVoucherCount").GetInt64());

        // V4 資料缺漏輔助檢視:刻意埋兩種 — 摘要空白(= blankDescription seed)+ 核准日離期(期末後核准種子
        // 20 張、核准日 2026-01-15 在期末後,每張 2 列 → 40 列);科目/傳票號為 0。
        // 「核准日不在期間」以核准日判定。
        var blankRecords = v.GetProperty("blankRecordsTest");
        Assert.Equal(
            (long)DemoDataFactory.BlankDescriptionVouchers,
            blankRecords.GetProperty("blankDescriptionCount").GetInt64());
        Assert.Equal(0, blankRecords.GetProperty("blankAccountNumberCount").GetInt64());
        Assert.Equal(0, blankRecords.GetProperty("blankVoucherNumberCount").GetInt64());
        Assert.Equal(
            2L * DemoDataFactory.PrepPeriodApprovalVouchers,
            blankRecords.GetProperty("approvalDateNotInPeriodCount").GetInt64());
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
}
