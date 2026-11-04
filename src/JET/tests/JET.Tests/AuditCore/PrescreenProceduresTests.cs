using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

// 第四步的原因改由規則需求表產生，原因常數已拿掉：原本「常數等於固定文字」的檢查
// 改成直接拿固定文字比對規則的原因；快照的核准日與建立人員兩個旗標換成可用欄位清單。
// 第一次失敗：收據 20261023-031856475-04f811a69e3346428de91091f0d02655（編譯失敗）。
// 同一階段第 3、4 項要求第四步與第五步對同一條規則給同一個原因，畫面只顯示後端一句寫出缺什麼、到哪裡補的原因，
// 預期值因此換成規則需求表的句子；缺幾項就依表列順序各寫一句。第一次失敗：Focused 收據
// 20261023-032846329-b8feeb7decf44e589688cc075a857504。
public sealed class PrescreenProceduresTests
{
    private const string ApprovalDateMissing = "「傳票核准日」尚未選擇來源欄，請回第三步欄位配對補上。";
    // 裁定 2026-11-03-10 改畫面用字：第一次失敗：Focused 收據 20261103-075405771-4def72d8e7964cfa9dce2fccff5e8630。
    private const string HolidayCalendarMissing = "尚未上傳國定假日清單，請先在第二步「國定假日與補班日」上傳。";
    private const string PrepStartDateMissing = "尚未填寫期末財報準備日，請先在第一步按「修改案件資料」填寫。";

    [Fact]
    public void Plan_WithoutAccountMapping_MarksUnexpectedPairNotApplicable()
    {
        var verdict = Verdict(Plan(Snapshot(hasAccountMapping: false)), "unexpected_account_pair");

        Assert.False(verdict.IsApplicable);
        Assert.Equal("科目配對中尚無任何科目設定分類，請先在第四步設定。", verdict.NaReason);
    }

    // 缺收入分類與缺對方分類原本共用一句，現在和第五步一樣各寫各的分類名稱，所以每組資料多帶預期的原因。
    [Theory]
    [InlineData(false, true, "科目配對中尚無歸類為「Revenue」的科目，請先在第四步設定。")]
    [InlineData(true, false, "科目配對中尚無歸類為「Receivables」「Cash」或「Receipt in advance」的科目，請先在第四步設定。")]
    public void Plan_WithoutRequiredAccountCategories_MarksUnexpectedPairNotApplicable(
        bool hasRevenue,
        bool hasCounterpart,
        string expectedReason)
    {
        var verdict = Verdict(
            Plan(Snapshot(hasRevenue: hasRevenue, hasCounterpart: hasCounterpart)),
            "unexpected_account_pair");

        Assert.False(verdict.IsApplicable);
        Assert.Equal(expectedReason, verdict.NaReason);
    }

    [Fact]
    public void Plan_WithRevenueAndCounterpart_RunsUnexpectedPair()
    {
        var verdict = Verdict(Plan(Snapshot()), "unexpected_account_pair");

        Assert.True(verdict.IsApplicable);
        Assert.Null(verdict.NaReason);
    }

    [Fact]
    public void Plan_WithoutHolidayCalendar_MarksHolidayProceduresNotApplicable()
    {
        var plan = Plan(Snapshot(hasApprovalDate: false, hasHolidays: false));

        // 假日核准一條同時缺假日檔與傳票核准日的配對，兩個原因依表列順序各寫一句。
        var posting = Verdict(plan, "holiday_posting");
        Assert.False(posting.IsApplicable);
        Assert.Equal(HolidayCalendarMissing, posting.NaReason);

        var approval = Verdict(plan, "holiday_approval");
        Assert.False(approval.IsApplicable);
        Assert.Equal(HolidayCalendarMissing + ApprovalDateMissing, approval.NaReason);
    }

    [Fact]
    public void Plan_WithoutCreatedByMapping_MarksPreparerSummaryNotApplicable()
    {
        // 只改確認配對用語，以下 N/A 事實與狀態仍逐項核對。
        // 第一次失敗：20261004-092023464-13b0a6928d5e400492fbe8a6a24eb69d。
        var verdict = Verdict(Plan(Snapshot(hasCreatedBy: false)), "preparer_summary");

        Assert.False(verdict.IsApplicable);
        Assert.Equal("「傳票建立人員」尚未選擇來源欄，請回第三步欄位配對補上。", verdict.NaReason);
    }

    [Fact]
    public void Plan_WithoutAuthorizedPreparers_MarksNonAuthorizedPreparerNotApplicable()
    {
        var verdict = Verdict(Plan(Snapshot(hasAuthorizedPreparers: false)), "non_authorized_preparer");

        Assert.False(verdict.IsApplicable);
        Assert.Equal("尚未匯入授權編製人員清單，請先在第二步匯入。", verdict.NaReason);
    }

    [Fact]
    public void Plan_WithoutApprovalDate_PreservesBothExistingReasons()
    {
        // 兩個不同用途的原因都保留，只同步確認配對說法。
        // 第一次失敗：20261004-092023464-13b0a6928d5e400492fbe8a6a24eb69d。
        // 原因改由規則需求表產生後，核准側規則本身只寫缺什麼；「只檢查了總帳日期」改由週末與假日卡合併原因時附上，
        // 所以第二個用途改查卡片的核准側說明。
        var snapshot = Snapshot(hasApprovalDate: false, prepStartDate: null);
        var plan = Plan(snapshot);

        var prepPeriod = Verdict(plan, "prep_period_approval");
        Assert.False(prepPeriod.IsApplicable);
        Assert.Equal(ApprovalDateMissing + PrepStartDateMissing, prepPeriod.NaReason);

        Assert.All(
            new[] { "weekend_approval", "holiday_approval" },
            slug =>
            {
                var approval = Verdict(plan, slug);
                Assert.False(approval.IsApplicable);
                Assert.Equal(ApprovalDateMissing, approval.NaReason);
            });

        Assert.All(
            new[] { ("weekend_posting", "weekend_approval"), ("holiday_posting", "holiday_approval") },
            pair =>
            {
                var card = PrescreenProcedures.ActivityReasons(snapshot, pair.Item1, pair.Item2);
                Assert.Null(card.NaReason);
                Assert.Equal("只檢查了總帳日期。" + ApprovalDateMissing, card.ApprovalNaReason);
            });
    }

    [Fact]
    public void Plan_WithoutPrepStartDate_MarksPrepPeriodApprovalNotApplicable()
    {
        var verdict = Verdict(Plan(Snapshot(prepStartDate: null)), "prep_period_approval");

        Assert.False(verdict.IsApplicable);
        // 新增修改入口後，仍以完整固定文字核對補救指示。
        Assert.Equal(PrepStartDateMissing, verdict.NaReason);
    }

    [Fact]
    public void Plan_WithAllSoftDependencies_MarksEveryPrescreenProcedureApplicable()
    {
        var plan = Plan(Snapshot());

        // 裁定 2026-10-08-03 移除傳票日期與回溯過帳，預篩選程序由 15 個減為 14 個。
        // 第四步照舊版拿掉編製分錄較少的人員與使用較少的科目（裁定 2026-10-29-27），再減為 12 個；第一次失敗：Focused 收據 20261030-163342206-c1b391e5be2e46bfa36b85fdbe6b59ad。
        Assert.Equal(12, plan.Procedures.Count);
        Assert.All(plan.Procedures, verdict =>
        {
            Assert.True(verdict.IsApplicable);
            Assert.Null(verdict.NaReason);
        });
    }

    [Fact]
    public void Finalize_AllApplicableWithZeroHits_ReturnsNaZeroAndNoReason()
    {
        var plan = Plan(Snapshot());
        var result = new PrescreenRunResult(
            PrepPeriodApprovalCount: 0,
            SuspiciousKeywordsCount: 0,
            UnexpectedAccountPairCount: 0,
            TrailingZerosCount: 0,
            ZerosThreshold: TrailingZeroThreshold.DefaultZerosThreshold,
            Preparers: Array.Empty<PreparerSummaryRow>(),
            DistinctAccountCount: 0,
            Accounts: Array.Empty<AccountUsageRow>(),
            WeekendPostingCount: 0,
            WeekendApprovalCount: 0,
            HolidayPostingCount: 0,
            HolidayApprovalCount: 0,
            BlankDescriptionCount: 0,
            NonAuthorizedPreparerCount: 0);

        var manifest = JetAuditProgram.Finalize(
            plan,
            new AuditOutcome(Prescreen: result));

        // 裁定 2026-10-08-03 移除傳票日期與回溯過帳，預篩選程序由 15 個減為 14 個。
        // 第四步照舊版拿掉編製分錄較少的人員與使用較少的科目（裁定 2026-10-29-27），再減為 12 個；第一次失敗：Focused 收據 20261030-163342206-c1b391e5be2e46bfa36b85fdbe6b59ad。
        Assert.Equal(12, manifest.Procedures.Count);
        Assert.All(manifest.Procedures, verdict =>
        {
            Assert.True(verdict.IsApplicable);
            Assert.Equal("na", verdict.Status);
            Assert.Equal(0L, verdict.Count);
            Assert.Null(verdict.NaReason);
        });
    }

    private static AuditExecutionPlan Plan(AuditProjectSnapshot snapshot) =>
        JetAuditProgram.Plan(
            snapshot,
            new AuditUserParameters(
                ResultId: string.Empty,
                GeneratedUtc: default,
                SampleSize: 0,
                ActionName: "prescreen.run"));

    private static ProcedureVerdict Verdict(AuditExecutionPlan plan, string slug) =>
        plan.Procedures.Single(candidate =>
            string.Equals(candidate.Definition.Slug, slug, StringComparison.Ordinal));

    private static AuditProjectSnapshot Snapshot(
        bool hasApprovalDate = true,
        bool hasCreatedBy = true,
        bool hasHolidays = true,
        bool hasAccountMapping = true,
        bool hasRevenue = true,
        bool hasCounterpart = true,
        bool hasAuthorizedPreparers = true,
        string? prepStartDate = "2025-12-31") =>
        new(
            ProjectId: "prescreen-core-test",
            HasGlMapping: true,
            HasTbMapping: false,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            MoneyScale: 10_000,
            SampleSeed: 48_271,
            PrepStartDate: prepStartDate,
            AvailableGlFields: JetFieldCatalog.GlFields
                .Select(static field => field.SemanticIdentity)
                .Where(field => (hasApprovalDate || field != JetFieldCatalog.GlApprovalDate)
                    && (hasCreatedBy || field != JetFieldCatalog.GlCreatedBy))
                .ToArray(),
            HasHolidays: hasHolidays,
            HasAccountMapping: hasAccountMapping,
            HasRevenue: hasRevenue,
            HasCounterpart: hasCounterpart,
            HasAuthorizedPreparers: hasAuthorizedPreparers,
            NonWorkingDays: [0, 6]);
}
