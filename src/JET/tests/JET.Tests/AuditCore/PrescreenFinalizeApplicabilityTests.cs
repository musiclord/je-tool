using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

/// <summary>
/// 預篩選收尾丟棄不適用規則的計數時，讀規則需求表的判定，不只依手列的清單。
/// 每一條規則都照同一個判定，只需要必填欄的六條規則也一樣。
/// </summary>
public sealed class PrescreenFinalizeApplicabilityTests
{
    [Fact]
    public void Finalize_DropsTheCountsOfEveryRuleTheRequirementTableMarksNotApplicable()
    {
        // 可用的 GL 欄位只剩傳票號碼、核准日與金額：摘要、科目編號、總帳日期、建立人員都不可用，科目配對與授權名單也沒有。
        var request = new PrescreenRequest(
            ProjectId: "synthetic",
            HasGlMapping: true,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            MoneyScale: 2,
            SampleSeed: 1,
            ResultId: "run",
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            PrepStartDate: "2025-12-01",
            AvailableGlFields: ["voucherNumber", "approvalDate", "amount"],
            HasHolidays: true,
            HasAccountMapping: false,
            HasRevenue: false,
            HasCounterpart: false,
            HasAuthorizedPreparers: false,
            NonWorkingDays: null)
        {
            // 0 的個數照 IDEA 版由 GL 算出，請求要帶 0 的個數；這則只核對適用性，帶 6 不影響結果。
            ZerosThreshold = 6
        };
        var plan = JetAuditProgram.Plan(request);
        var voucherCounts = PrescreenRuleKeys.FilterableKeys.ToDictionary(key => key, _ => 5L, StringComparer.Ordinal);
        var facts = new PrescreenFacts(
            PrepPeriodApprovalCount: 5,
            SuspiciousKeywordsCount: 5,
            UnexpectedAccountPairCount: 5,
            TrailingZerosCount: 5,
            Preparers: [new PreparerSummaryRow("u", 5, 500, 0, 0)],
            DistinctAccountCount: 5,
            Accounts: [new AccountUsageRow("A", "Synthetic", 5, 500, 0)],
            WeekendPostingCount: 5,
            WeekendApprovalCount: 5,
            HolidayPostingCount: 5,
            HolidayApprovalCount: 5,
            BlankDescriptionCount: 5,
            NonAuthorizedPreparerCount: 5,
            RuleVoucherCounts: voucherCounts,
            TotalPreparerCount: 5,
            TotalEntryCount: 50);

        var data = JetAuditProgram.Finalize(plan, facts).Data;

        // 可以執行的三條照傳。
        Assert.Equal(5, data.PrepPeriodApprovalCount);
        Assert.Equal(5, data.TrailingZerosCount);
        Assert.Equal(5, data.WeekendApprovalCount);
        Assert.Equal(5, data.HolidayApprovalCount);
        // 需要摘要、科目編號、總帳日期或建立人員的規則都不適用，計數不帶出去。
        Assert.Equal(0, data.SuspiciousKeywordsCount);
        Assert.Equal(0, data.BlankDescriptionCount);
        Assert.Equal(0, data.WeekendPostingCount);
        Assert.Equal(0, data.HolidayPostingCount);
        // 照舊版 JET 的作法，沒有編製分錄較少的人員與使用較少的科目，結果裡不帶這兩條的計數。
        Assert.Equal(0, data.DistinctAccountCount);
        Assert.Empty(data.Accounts);
        Assert.Empty(data.Preparers);
        Assert.Equal(0, data.UnexpectedAccountPairCount);
        Assert.Equal(0, data.NonAuthorizedPreparerCount);
    }
}
