using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class PrescreenProceduresTests
{
    [Fact]
    public void Plan_WithoutAccountMapping_MarksUnexpectedPairNotApplicable()
    {
        Assert.Equal("需先匯入科目配對。", PrescreenProcedures.MissingAccountMappingReason);

        var verdict = Verdict(Plan(Snapshot(hasAccountMapping: false)), "unexpected_account_pair");

        Assert.False(verdict.IsApplicable);
        Assert.Equal(PrescreenProcedures.MissingAccountMappingReason, verdict.NaReason);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Plan_WithoutRequiredAccountCategories_MarksUnexpectedPairNotApplicable(
        bool hasRevenue,
        bool hasCounterpart)
    {
        Assert.Equal(
            "科目配對需包含收入，以及至少一項應收款項、現金或預收款項分類。",
            PrescreenProcedures.IncompleteAccountMappingReason);

        var verdict = Verdict(
            Plan(Snapshot(hasRevenue: hasRevenue, hasCounterpart: hasCounterpart)),
            "unexpected_account_pair");

        Assert.False(verdict.IsApplicable);
        Assert.Equal(PrescreenProcedures.IncompleteAccountMappingReason, verdict.NaReason);
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
        Assert.Equal("請先上傳事務所假日檔。", PrescreenProcedures.MissingHolidayCalendarReason);

        var plan = Plan(Snapshot(hasApprovalDate: false, hasHolidays: false));

        Assert.All(
            new[] { "holiday_posting", "holiday_approval" },
            slug =>
            {
                var verdict = Verdict(plan, slug);
                Assert.False(verdict.IsApplicable);
                Assert.Equal(PrescreenProcedures.MissingHolidayCalendarReason, verdict.NaReason);
            });
    }

    [Fact]
    public void Plan_WithoutCreatedByMapping_MarksCreatorSummaryNotApplicable()
    {
        Assert.Equal("請先完成 GL「傳票建立人員」欄位配對。", PrescreenProcedures.MissingCreatedByMappingReason);

        var verdict = Verdict(Plan(Snapshot(hasCreatedBy: false)), "creator_summary");

        Assert.False(verdict.IsApplicable);
        Assert.Equal(PrescreenProcedures.MissingCreatedByMappingReason, verdict.NaReason);
    }

    [Fact]
    public void Plan_WithoutAuthorizedPreparers_MarksNonAuthorizedPreparerNotApplicable()
    {
        Assert.Equal("需先匯入授權編製人員清單。", PrescreenProcedures.MissingAuthorizedPreparersReason);

        var verdict = Verdict(Plan(Snapshot(hasAuthorizedPreparers: false)), "non_authorized_preparer");

        Assert.False(verdict.IsApplicable);
        Assert.Equal(PrescreenProcedures.MissingAuthorizedPreparersReason, verdict.NaReason);
    }

    [Fact]
    public void Plan_WithoutApprovalDate_PreservesBothExistingReasons()
    {
        Assert.Equal("請先完成 GL「傳票核准日」欄位配對。", PrescreenProcedures.MissingApprovalDateMappingReason);
        Assert.Equal("尚未完成 GL「傳票核准日」欄位配對，因此僅檢查總帳日期。", PrescreenProcedures.MissingApprovalDateForActivityReason);

        var plan = Plan(Snapshot(hasApprovalDate: false, lastPeriodStart: null));

        var postPeriod = Verdict(plan, "post_period_approval");
        Assert.False(postPeriod.IsApplicable);
        Assert.Equal(PrescreenProcedures.MissingApprovalDateMappingReason, postPeriod.NaReason);

        Assert.All(
            new[] { "weekend_approval", "holiday_approval" },
            slug =>
            {
                var approval = Verdict(plan, slug);
                Assert.False(approval.IsApplicable);
                Assert.Equal(PrescreenProcedures.MissingApprovalDateForActivityReason, approval.NaReason);
            });
    }

    [Fact]
    public void Plan_WithoutLastPeriodStart_MarksPostPeriodApprovalNotApplicable()
    {
        Assert.Equal(
            "案件尚未設定期末財報準備日。",
            PrescreenProcedures.MissingLastPeriodStartReason);

        var verdict = Verdict(Plan(Snapshot(lastPeriodStart: null)), "post_period_approval");

        Assert.False(verdict.IsApplicable);
        Assert.Equal(PrescreenProcedures.MissingLastPeriodStartReason, verdict.NaReason);
    }

    [Fact]
    public void Plan_WithAllSoftDependencies_MarksEveryPrescreenProcedureApplicable()
    {
        var plan = Plan(Snapshot());

        Assert.Equal(15, plan.Procedures.Count);
        Assert.All(plan.Procedures, verdict =>
        {
            Assert.True(verdict.IsApplicable);
            Assert.Null(verdict.NaReason);
        });
    }

    [Fact]
    public void RenderPrescreenSummary_NormalizesLegacyNaReasonsWithoutChangingResults()
    {
        const string legacy =
            """
            {
              "postPeriodApproval": {
                "status": "na",
                "naReason": "GL 未配對核准日欄位（docDate）。",
                "count": 0
              },
              "nested": [{
                "naReason": "尚未匯入假日曆（import.holiday）。",
                "count": 7
              }],
              "auditNote": "GL 未配對核准日欄位（docDate）。"
            }
            """;

        var rendered = JetAuditProgram.RenderPrescreenSummary(legacy);

        var postPeriod = rendered.GetProperty("postPeriodApproval");
        Assert.Equal("na", postPeriod.GetProperty("status").GetString());
        Assert.Equal(0, postPeriod.GetProperty("count").GetInt32());
        Assert.Equal(
            PrescreenProcedures.MissingApprovalDateMappingReason,
            postPeriod.GetProperty("naReason").GetString());
        Assert.Equal(
            PrescreenProcedures.MissingHolidayCalendarReason,
            rendered.GetProperty("nested").EnumerateArray().Single()
                .GetProperty("naReason").GetString());
        Assert.Equal(
            "GL 未配對核准日欄位（docDate）。",
            rendered.GetProperty("auditNote").GetString());
    }

    [Fact]
    public void Finalize_AllApplicableWithZeroHits_ReturnsNaZeroAndNoReason()
    {
        var plan = Plan(Snapshot());
        var result = new PrescreenRunResult(
            PostPeriodApprovalCount: 0,
            SuspiciousKeywordsCount: 0,
            UnexpectedAccountPairCount: 0,
            TrailingZerosCount: 0,
            ZerosThreshold: TrailingZeroThreshold.DefaultZerosThreshold,
            Creators: Array.Empty<CreatorSummaryRow>(),
            DistinctAccountCount: 0,
            Accounts: Array.Empty<AccountUsageRow>(),
            WeekendPostingCount: 0,
            WeekendApprovalCount: 0,
            HolidayPostingCount: 0,
            HolidayApprovalCount: 0,
            BlankDescriptionCount: 0,
            BackdatedPostingCount: 0,
            NonAuthorizedPreparerCount: 0,
            LowFrequencyPreparerCount: 0,
            LowFrequencyAccountCount: 0);

        var manifest = JetAuditProgram.Finalize(
            plan,
            new AuditOutcome(Prescreen: result));

        Assert.Equal(15, manifest.Procedures.Count);
        Assert.All(manifest.Procedures, verdict =>
        {
            Assert.True(verdict.IsApplicable);
            Assert.Equal("na", verdict.Status);
            Assert.Equal(0L, verdict.Count);
            Assert.Null(verdict.NaReason);
        });
    }

    private static AuditExecutionPlan Plan(AuditCaseSnapshot snapshot) =>
        JetAuditProgram.Plan(
            snapshot,
            new AuditUserParameters(
                RunId: string.Empty,
                GeneratedUtc: default,
                SampleSize: 0,
                ActionName: "prescreen.run"));

    private static ProcedureVerdict Verdict(AuditExecutionPlan plan, string slug) =>
        plan.Procedures.Single(candidate =>
            string.Equals(candidate.Definition.Slug, slug, StringComparison.Ordinal));

    private static AuditCaseSnapshot Snapshot(
        bool hasApprovalDate = true,
        bool hasCreatedBy = true,
        bool hasHolidays = true,
        bool hasAccountMapping = true,
        bool hasRevenue = true,
        bool hasCounterpart = true,
        bool hasAuthorizedPreparers = true,
        string? lastPeriodStart = "2025-12-31") =>
        new(
            ProjectId: "prescreen-core-test",
            HasGlMapping: true,
            HasTbMapping: false,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            MoneyScale: 10_000,
            SampleSeed: 48_271,
            LastPeriodStart: lastPeriodStart,
            HasApprovalDate: hasApprovalDate,
            HasCreatedBy: hasCreatedBy,
            HasHolidays: hasHolidays,
            HasAccountMapping: hasAccountMapping,
            HasRevenue: hasRevenue,
            HasCounterpart: hasCounterpart,
            HasAuthorizedPreparers: hasAuthorizedPreparers,
            NonWorkingDays: [0, 6]);
}
