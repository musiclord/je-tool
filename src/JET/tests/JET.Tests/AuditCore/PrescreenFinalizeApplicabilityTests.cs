using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

/// <summary>
/// 預篩選收斂計畫階段九：預篩選收尾丟棄不適用規則的計數時，改讀規則需求表的判定，不再只依手列的清單。
/// 階段四查到只需要必填欄的六條規則即使判定不適用，計數也照傳；這裡讓每一條規則都照同一個判定。
/// 第一次失敗：收據 20261024-190913880-a45e823f31e745139f977c5c3481bdb3。
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
            RunId: "run",
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            LastPeriodStart: "2025-12-01",
            AvailableGlFields: ["docNum", "docDate", "amount"],
            HasHolidays: true,
            HasAccountMapping: false,
            HasRevenue: false,
            HasCounterpart: false,
            HasAuthorizedPreparers: false,
            NonWorkingDays: null);
        var plan = JetAuditProgram.Plan(request);
        var voucherCounts = PrescreenRuleKeys.FilterableKeys.ToDictionary(key => key, _ => 5L, StringComparer.Ordinal);
        var facts = new PrescreenFacts(
            PostPeriodApprovalCount: 5,
            SuspiciousKeywordsCount: 5,
            UnexpectedAccountPairCount: 5,
            TrailingZerosCount: 5,
            Creators: [new CreatorSummaryRow("u", 5, 500, 0, 0)],
            DistinctAccountCount: 5,
            Accounts: [new AccountUsageRow("A", "Synthetic", 5, 500, 0)],
            WeekendPostingCount: 5,
            WeekendApprovalCount: 5,
            HolidayPostingCount: 5,
            HolidayApprovalCount: 5,
            BlankDescriptionCount: 5,
            NonAuthorizedPreparerCount: 5,
            LowFrequencyPreparerCount: 5,
            LowFrequencyAccountCount: 5,
            RuleVoucherCounts: voucherCounts,
            TotalPreparerCount: 5,
            TotalEntryCount: 50)
        { LowFrequencyDistinctAccountCount = 5 };

        var data = JetAuditProgram.Finalize(plan, facts).Data;

        // 可以執行的三條照傳。
        Assert.Equal(5, data.PostPeriodApprovalCount);
        Assert.Equal(5, data.TrailingZerosCount);
        Assert.Equal(5, data.WeekendApprovalCount);
        Assert.Equal(5, data.HolidayApprovalCount);
        // 需要摘要、科目編號、總帳日期或建立人員的規則都不適用，計數不帶出去。
        Assert.Equal(0, data.SuspiciousKeywordsCount);
        Assert.Equal(0, data.BlankDescriptionCount);
        Assert.Equal(0, data.WeekendPostingCount);
        Assert.Equal(0, data.HolidayPostingCount);
        Assert.Equal(0, data.LowFrequencyPreparerCount);
        Assert.Equal(0, data.LowFrequencyAccountCount);
        Assert.Null(data.LowFrequencyDistinctAccountCount);
        Assert.Equal(0, data.DistinctAccountCount);
        Assert.Empty(data.Accounts);
        Assert.Empty(data.Creators);
        Assert.Equal(0, data.UnexpectedAccountPairCount);
        Assert.Equal(0, data.NonAuthorizedPreparerCount);
    }
}
