using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

/// <summary>
/// 預篩選收尾丟棄不適用規則的計數時，改讀規則需求表的判定，不再只依手列的清單。
/// 先前查到只需要必填欄的六條規則即使判定不適用，計數也照傳；這裡讓每一條規則都照同一個判定。
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
            // 0 的個數改照 IDEA 版由 GL 算出（裁定 2026-11-05-25），固定 6 個 0 的常數拿掉，請求要帶 0 的個數；這則只核對適用性，帶 6 不影響結果。第一次失敗是建置失敗：收據 20261105-082826731-60a7ec59299d423aa561526e307e604b。
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
        // 編製分錄較少的人員與使用較少的科目照舊版拿掉（裁定 2026-10-29-27），三個計數跟著不在結果裡；第一次失敗是建置失敗：收據 20261030-163008036-bb2e987946cc4c72a351610e661109c5。
        Assert.Equal(0, data.DistinctAccountCount);
        Assert.Empty(data.Accounts);
        Assert.Empty(data.Preparers);
        Assert.Equal(0, data.UnexpectedAccountPairCount);
        Assert.Equal(0, data.NonAuthorizedPreparerCount);
    }
}
