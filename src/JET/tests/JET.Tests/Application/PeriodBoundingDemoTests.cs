using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// §2 母體期間界定（2026-07-08 第二輪）：demo 的期外對照組（過帳日 2026-01-05 的兩張不平傳票，
/// 見 <see cref="JET.Application.DemoDataFactory.OutOfPeriodVouchers"/>）一律不進驗證/預篩選/INF/完整性母體。
///
/// oracle：期外對照組為固定 4 列（2 傳票 × 2 行，post_date=2026-01-05），刻意帶多重「若在母體會命中」的訊號
/// （2 張借貸不平、1 列摘要空白、1 列 suspicious 關鍵字、4 列核准日期外/期末後核准）。本測試先確認這 4 列
/// 確實落在投影（否則排除斷言為偽綠），再斷言各母體命中數維持期內原值——若移除期間界定（PeriodBounds 退化
/// 為恆真）則全數轉紅（red→green 證據，落檔見 scratchpad round2-period-bounding-redgreen.md）。
/// 與 <see cref="DemoRuleOracleTests"/> 的期內命中數互補，合起來即全母體 oracle 重推。
/// </summary>
public sealed class PeriodBoundingDemoTests
{
    [Fact]
    public async Task OutOfPeriodRows_ExistInProjection_ButExcludedFromEveryPopulation()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        // 前置（反偽綠）：期外對照組確實落地投影 —— 4 列 post_date=2026-01-05。
        var outOfPeriodRows = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry WHERE post_date > '2025-12-31';");
        Assert.Equal(4, outOfPeriodRows); // 2 張 × 2 行

        // 且其中含「若在母體會命中借貸不平」的訊號：期外 2 張傳票各自借≠貸（不受期間界定時 recount=2）。
        var unbalancedOutOfPeriod = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId,
            "SELECT COUNT(*) FROM (SELECT document_number FROM target_gl_entry " +
            "WHERE post_date > '2025-12-31' GROUP BY document_number HAVING SUM(amount_scaled) <> 0) x;");
        Assert.Equal(2, unbalancedOutOfPeriod);

        var validate = await host.DispatchAsync("validate.run");
        var nullRecords = validate.GetProperty("nullRecordsTest");
        // 借貸不平：期外 2 張不平傳票被排除 → 0（未界定則為 2）。
        Assert.Equal(0, validate.GetProperty("docBalanceTest").GetProperty("unbalancedDocumentCount").GetInt64());
        // 完整性：期外 GL 不入本期彙總、TB 亦不含（BuildTbRows 限本期）→ 無假性差異 → 0（未界定則出現差異科目）。
        Assert.Equal(0, validate.GetProperty("completenessTest").GetProperty("diffAccountCount").GetInt64());
        // 空值摘要：期外空白摘要列被排除 → 維持 18（BlankDescriptionVouchers；未界定則 19）。
        Assert.Equal(18, nullRecords.GetProperty("nullDescriptionCount").GetInt64());
        // 期外日期：以核准日判定但母體限本期 post_date → R1（過帳期內、核准 2026-01-15 期外）40 列命中；
        //           期外對照組（過帳期外、核准期外）不入母體 → 不計（未界定則 44）。這正是 §2 spec 定案語意
        //          「過帳日在期內、核准日在期外者屬母體且命中；過帳日期外者一律不入母體」。
        Assert.Equal(40, nullRecords.GetProperty("outOfRangeDateCount").GetInt64());

        var prescreen = await host.DispatchAsync("prescreen.run");
        // suspicious 關鍵字：期外那 1 列被排除 → 維持 25（未界定則 26）。
        Assert.Equal(25, prescreen.GetProperty("suspiciousKeywords").GetProperty("count").GetInt64());
        // 期末後核准：期外對照組核准日 2026-01-20 ≥ 期末，但過帳期外不入母體 → 維持 40（未界定則 44）。
        Assert.Equal(40, prescreen.GetProperty("postPeriodApproval").GetProperty("count").GetInt64());

        // INF 抽樣：母體限本期 → 樣本不含任何期外列（未界定則可能抽中期外列）。
        var sampleOutOfPeriod = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId,
            "SELECT COUNT(*) FROM result_inf_sampling_test_sample s " +
            "JOIN target_gl_entry g ON g.entry_id = s.entry_id " +
            "WHERE g.post_date > '2025-12-31';");
        Assert.Equal(0, sampleOutOfPeriod);
    }
}
