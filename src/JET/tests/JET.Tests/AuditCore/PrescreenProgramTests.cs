using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class PrescreenProgramTests
{
    [Fact]
    public void Plan_BindsTypedRequestToExistingReviewPlanWithoutLoss()
    {
        var request = Request();

        var plan = JetAuditProgram.Plan(request);

        Assert.Same(request, plan.Request);
        Assert.Equal(request.ProjectId, plan.ReviewPlan.CaseSnapshot.ProjectId);
        Assert.Equal(request.PeriodStart, plan.ReviewPlan.CaseSnapshot.PeriodStart);
        Assert.Equal(request.PeriodEnd, plan.ReviewPlan.CaseSnapshot.PeriodEnd);
        Assert.Equal(request.MoneyScale, plan.ReviewPlan.CaseSnapshot.MoneyScale);
        Assert.Equal(request.SampleSeed, plan.ReviewPlan.CaseSnapshot.SampleSeed);
        Assert.Equal(request.RunId, plan.ReviewPlan.UserParameters.RunId);
        Assert.Equal(request.GeneratedUtc, plan.ReviewPlan.UserParameters.GeneratedUtc);
        Assert.Equal("prescreen.run", plan.ReviewPlan.UserParameters.ActionName);
        Assert.Equal(TrailingZeroThreshold.DefaultZerosThreshold, plan.ZerosThreshold);
        Assert.Equal(15, plan.ReviewPlan.Procedures.Count);
        Assert.All(plan.ReviewPlan.Procedures, verdict => Assert.True(verdict.IsApplicable));
    }

    [Fact]
    public async Task ExecuteAsync_PassesTypedPlanToFactsPortWithoutLoss()
    {
        var plan = JetAuditProgram.Plan(Request());
        var expectedFacts = Facts();
        var port = new RecordingFactsPort(expectedFacts);

        var actual = await JetAuditProgram.ExecuteAsync(plan, port, CancellationToken.None);

        Assert.Same(plan, port.Plan);
        Assert.Same(expectedFacts, actual);
    }

    [Fact]
    public void Finalize_MissingDependencies_DiscardsSkippedRawFactsAndPreservesPrecedence()
    {
        var plan = JetAuditProgram.Plan(Request(
            hasApprovalDate: false,
            hasCreatedBy: false,
            hasHolidays: false,
            hasAccountMapping: false,
            hasRevenue: false,
            hasCounterpart: false,
            hasAuthorizedPreparers: false,
            lastPeriodStart: null));
        var facts = Facts(nonZero: true);

        var result = JetAuditProgram.Finalize(plan, facts);

        Assert.Equal(0, result.Data.PostPeriodApprovalCount);
        Assert.Equal(facts.SuspiciousKeywordsCount, result.Data.SuspiciousKeywordsCount);
        Assert.Equal(0, result.Data.UnexpectedAccountPairCount);
        Assert.Equal(facts.TrailingZerosCount, result.Data.TrailingZerosCount);
        Assert.Empty(result.Data.Creators);
        Assert.Equal(facts.DistinctAccountCount, result.Data.DistinctAccountCount);
        Assert.Same(facts.Accounts, result.Data.Accounts);
        Assert.Equal(facts.WeekendPostingCount, result.Data.WeekendPostingCount);
        Assert.Null(result.Data.WeekendApprovalCount);
        Assert.Equal(0, result.Data.HolidayPostingCount);
        Assert.Null(result.Data.HolidayApprovalCount);
        Assert.Equal(0, result.Data.NonAuthorizedPreparerCount);
        Assert.Equal(
            TrailingZeroThreshold.DefaultZerosThreshold,
            result.Data.ZerosThreshold);

        AssertSkipped(
            result,
            "post_period_approval",
            PrescreenProcedures.MissingApprovalDateMappingReason);
        AssertSkipped(
            result,
            "unexpected_account_pair",
            PrescreenProcedures.MissingAccountMappingReason);
        AssertSkipped(
            result,
            "creator_summary",
            PrescreenProcedures.MissingCreatedByMappingReason);
        AssertSkipped(
            result,
            "weekend_approval",
            PrescreenProcedures.MissingApprovalDateForActivityReason);
        AssertSkipped(
            result,
            "holiday_posting",
            PrescreenProcedures.MissingHolidayCalendarReason);
        AssertSkipped(
            result,
            "holiday_approval",
            PrescreenProcedures.MissingHolidayCalendarReason);
        AssertSkipped(
            result,
            "non_authorized_preparer",
            PrescreenProcedures.MissingAuthorizedPreparersReason);
    }

    [Fact]
    public void Finalize_AllApplicableWithZeroFacts_ReturnsNaZeroAndNoReason()
    {
        var result = JetAuditProgram.Finalize(
            JetAuditProgram.Plan(Request()),
            Facts());

        Assert.Equal(15, result.Manifest.Procedures.Count);
        Assert.All(result.Manifest.Procedures, verdict =>
        {
            Assert.True(verdict.IsApplicable);
            Assert.Equal("na", verdict.Status);
            Assert.Equal(0L, verdict.Count);
            Assert.Null(verdict.NaReason);
        });
        Assert.Equal(0, result.Data.WeekendApprovalCount);
        Assert.Equal(0, result.Data.HolidayApprovalCount);
    }

    [Fact]
    public void Finalize_UsesThresholdOwnedByPlan()
    {
        var plan = JetAuditProgram.Plan(Request()) with
        {
            ZerosThreshold = 3
        };

        var result = JetAuditProgram.Finalize(plan, Facts());

        Assert.Equal(3, result.Data.ZerosThreshold);
    }

    [Fact]
    public void Finalize_AllApplicable_PreservesRawCountsAndBoundedSummaries()
    {
        var facts = Facts(nonZero: true);

        var result = JetAuditProgram.Finalize(
            JetAuditProgram.Plan(Request()),
            facts);

        Assert.Equal(facts.PostPeriodApprovalCount, result.Data.PostPeriodApprovalCount);
        Assert.Equal(facts.UnexpectedAccountPairCount, result.Data.UnexpectedAccountPairCount);
        Assert.Same(facts.Creators, result.Data.Creators);
        Assert.Equal(facts.WeekendApprovalCount, result.Data.WeekendApprovalCount);
        Assert.Equal(facts.HolidayPostingCount, result.Data.HolidayPostingCount);
        Assert.Equal(facts.HolidayApprovalCount, result.Data.HolidayApprovalCount);
        Assert.Equal(facts.NonAuthorizedPreparerCount, result.Data.NonAuthorizedPreparerCount);
        Assert.All(result.Manifest.Procedures, verdict => Assert.Equal("V", verdict.Status));
    }

    [Fact]
    public void Finalize_Concentration_DerivesSharesFromWholePopulationNotListedRows()
    {
        // 手算母體：彙總列出 12 位（40、30、20、15、10、8、7、6、5、4、3、2＝150 列），
        // 但期間全母體是 200 列、30 位——占比一律以全母體為分母，不得改用列出者合計。
        var facts = ConcentrationFacts(
            [40, 30, 20, 15, 10, 8, 7, 6, 5, 4, 3, 2],
            totalPreparerCount: 30,
            totalEntryCount: 200);

        var result = JetAuditProgram.Finalize(JetAuditProgram.Plan(Request()), facts);
        var concentration = result.Concentration;

        Assert.Equal("V", concentration.Status);
        Assert.Null(concentration.NaReason);

        var preparers = Assert.IsType<PreparerConcentration>(concentration.Preparers);
        Assert.Equal(30, preparers.TotalPreparerCount);
        Assert.Equal(200, preparers.TotalEntryCount);
        Assert.Equal(10, preparers.Top.Count);

        // 累積：40、70、90、105、115、123、130、136、141、145（÷200）。
        Assert.Equal(20.0m, preparers.Top[0].CumulativePct);
        Assert.Equal(57.5m, preparers.Top[4].CumulativePct);
        Assert.Equal(72.5m, preparers.Top[9].CumulativePct);

        // 其他＝全母體 200 − 前 10 名 145；前五佔比＝115／200。
        Assert.Equal(55, preparers.OthersEntryCount);
        Assert.Equal(57.5m, preparers.Top5SharePct);
        Assert.Equal(3, preparers.Top[2].ManualCount);
    }

    [Fact]
    public void Finalize_Concentration_RoundsHalfAwayFromZeroToOneDecimal()
    {
        // 1／16 ＝ 6.25%：一位小數的中點一律遠離零（6.3），不採銀行家捨入（6.2）。
        var facts = ConcentrationFacts([1], totalPreparerCount: 1, totalEntryCount: 16);

        var result = JetAuditProgram.Finalize(JetAuditProgram.Plan(Request()), facts);

        var preparers = Assert.IsType<PreparerConcentration>(result.Concentration.Preparers);
        Assert.Equal(6.3m, preparers.Top[0].CumulativePct);
        Assert.Equal(6.3m, preparers.Top5SharePct);
    }

    [Fact]
    public void Finalize_Concentration_ZeroPopulation_ReturnsNullSharesInsteadOfZeroPercent()
    {
        // 分母為 0 → 占比 null（前端顯示「—」）；不得以 0% 冒充已計算。
        var facts = ConcentrationFacts([3], totalPreparerCount: 1, totalEntryCount: 0);

        var result = JetAuditProgram.Finalize(JetAuditProgram.Plan(Request()), facts);

        var preparers = Assert.IsType<PreparerConcentration>(result.Concentration.Preparers);
        Assert.Null(preparers.Top[0].CumulativePct);
        Assert.Null(preparers.Top5SharePct);
        Assert.Equal(0, preparers.OthersEntryCount);
    }

    [Fact]
    public void Finalize_Concentration_CapsRareAccountsAtTwentyAscendingRows()
    {
        var facts = ConcentrationFacts(
            [5],
            totalPreparerCount: 1,
            totalEntryCount: 5,
            accounts: [.. Enumerable.Range(1, 25).Select(i =>
                new AccountUsageRow($"A{i:00}", $"科目 {i:00}", i, 0, 0))]);

        var result = JetAuditProgram.Finalize(JetAuditProgram.Plan(Request()), facts);

        var rare = Assert.IsAssignableFrom<IReadOnlyList<AccountUsageRow>>(result.Concentration.RareAccounts);
        Assert.Equal(20, rare.Count);
        Assert.Equal("A01", rare[0].AccountCode);
        Assert.Equal("A20", rare[19].AccountCode);
        Assert.Equal(facts.DistinctAccountCount, result.Concentration.DistinctAccountCount);
    }

    [Fact]
    public void Finalize_Concentration_WithoutCreatedByMapping_IsNotApplicableWithoutNumbers()
    {
        // 整塊沿用 creator_summary 裁定：前置不足時不帶任何數值格（與「結果為 0」語意不同）。
        var plan = JetAuditProgram.Plan(Request(hasCreatedBy: false));
        var facts = ConcentrationFacts([9, 8], totalPreparerCount: 2, totalEntryCount: 17);

        var result = JetAuditProgram.Finalize(plan, facts);

        Assert.Equal("na", result.Concentration.Status);
        Assert.Equal(PrescreenProcedures.MissingCreatedByMappingReason, result.Concentration.NaReason);
        Assert.Null(result.Concentration.Preparers);
        Assert.Null(result.Concentration.RareAccounts);
        Assert.Null(result.Concentration.DistinctAccountCount);
    }

    [Fact]
    public void Finalize_Concentration_EmptyCreatorSummary_IsNotApplicableWithoutReason()
    {
        // 前置齊備但期間無分錄：creator_summary 依既有規則為 na 且無 N/A 原因，集中度同步。
        var result = JetAuditProgram.Finalize(JetAuditProgram.Plan(Request()), Facts());

        Assert.Equal("na", result.Concentration.Status);
        Assert.Null(result.Concentration.NaReason);
        Assert.Null(result.Concentration.Preparers);
    }

    [Fact]
    public void Explain_TypedResult_ReusesExistingReviewManifestText()
    {
        var result = JetAuditProgram.Finalize(
            JetAuditProgram.Plan(Request()),
            Facts());

        Assert.Equal(JetAuditProgram.Explain(result.Manifest), JetAuditProgram.Explain(result));
    }

    private static PrescreenRequest Request(
        bool hasApprovalDate = true,
        bool hasCreatedBy = true,
        bool hasHolidays = true,
        bool hasAccountMapping = true,
        bool hasRevenue = true,
        bool hasCounterpart = true,
        bool hasAuthorizedPreparers = true,
        string? lastPeriodStart = "2025-12-31") =>
        new(
            ProjectId: "project",
            HasGlMapping: true,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            MoneyScale: 10_000,
            SampleSeed: 48_271,
            RunId: "run",
            GeneratedUtc: new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero),
            LastPeriodStart: lastPeriodStart,
            HasApprovalDate: hasApprovalDate,
            HasCreatedBy: hasCreatedBy,
            HasHolidays: hasHolidays,
            HasAccountMapping: hasAccountMapping,
            HasRevenue: hasRevenue,
            HasCounterpart: hasCounterpart,
            HasAuthorizedPreparers: hasAuthorizedPreparers,
            NonWorkingDays: [0, 6]);

    private static PrescreenFacts Facts(bool nonZero = false)
    {
        var count = nonZero ? 1 : 0;
        return new PrescreenFacts(
            PostPeriodApprovalCount: count,
            SuspiciousKeywordsCount: count,
            UnexpectedAccountPairCount: count,
            TrailingZerosCount: count,
            Creators: nonZero
                ? [new CreatorSummaryRow("creator", 1, 2, 3, 1)]
                : [],
            DistinctAccountCount: count,
            Accounts: nonZero
                ? [new AccountUsageRow("1000", "Cash", 1, 2, 3)]
                : [],
            WeekendPostingCount: count,
            WeekendApprovalCount: count,
            HolidayPostingCount: count,
            HolidayApprovalCount: count,
            BlankDescriptionCount: count,
            BackdatedPostingCount: count,
            NonAuthorizedPreparerCount: count,
            LowFrequencyPreparerCount: count,
            LowFrequencyAccountCount: count,
            RuleVoucherCounts: PrescreenRuleKeys.FilterableKeys.ToDictionary(
                key => key,
                _ => (long)count,
                StringComparer.Ordinal),
            TotalPreparerCount: count,
            TotalEntryCount: count);
    }

    /// <summary>
    /// 集中度用 facts：<paramref name="creatorEntryCounts"/> 依遞減順序給定各編製者列數
    /// （模擬 provider 已排序的 50 列上限彙總），人工筆數固定取序號以便對位斷言。
    /// </summary>
    private static PrescreenFacts ConcentrationFacts(
        IReadOnlyList<long> creatorEntryCounts,
        long totalPreparerCount,
        long totalEntryCount,
        IReadOnlyList<AccountUsageRow>? accounts = null)
    {
        var creators = creatorEntryCounts
            .Select((count, index) => new CreatorSummaryRow($"C{index + 1:00}", count, 0, 0, index + 1))
            .ToArray();

        return Facts() with
        {
            Creators = creators,
            Accounts = accounts ?? [],
            DistinctAccountCount = accounts?.Count ?? 0,
            TotalPreparerCount = totalPreparerCount,
            TotalEntryCount = totalEntryCount
        };
    }

    private static void AssertSkipped(
        PrescreenResult result,
        string slug,
        string expectedReason)
    {
        var verdict = result.Manifest.Procedures.Single(item =>
            string.Equals(item.Definition.Slug, slug, StringComparison.Ordinal));
        Assert.False(verdict.IsApplicable);
        Assert.Equal("na", verdict.Status);
        Assert.Equal(0L, verdict.Count);
        Assert.Equal(expectedReason, verdict.NaReason);
    }

    private sealed class RecordingFactsPort(PrescreenFacts result) : IPrescreenFactsPort
    {
        public PrescreenPlan? Plan { get; private set; }

        public Task<PrescreenFacts> ExecuteAsync(
            PrescreenPlan plan,
            CancellationToken cancellationToken)
        {
            Plan = plan;
            return Task.FromResult(result);
        }
    }
}
