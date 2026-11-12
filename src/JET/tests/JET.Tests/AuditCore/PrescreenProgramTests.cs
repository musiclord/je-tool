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
        Assert.Equal(request.ProjectId, plan.ReviewPlan.ProjectSnapshot.ProjectId);
        Assert.Equal(request.PeriodStart, plan.ReviewPlan.ProjectSnapshot.PeriodStart);
        Assert.Equal(request.PeriodEnd, plan.ReviewPlan.ProjectSnapshot.PeriodEnd);
        Assert.Equal(request.MoneyScale, plan.ReviewPlan.ProjectSnapshot.MoneyScale);
        Assert.Equal(request.SampleSeed, plan.ReviewPlan.ProjectSnapshot.SampleSeed);
        Assert.Equal(request.ResultId, plan.ReviewPlan.UserParameters.ResultId);
        Assert.Equal(request.GeneratedUtc, plan.ReviewPlan.UserParameters.GeneratedUtc);
        Assert.Equal("prescreen.run", plan.ReviewPlan.UserParameters.ActionName);
        // 預篩選程序共 12 個：沒有傳票日期與回溯過帳，也照舊版 JET 不含編製分錄較少的人員與使用較少的科目。
        Assert.Equal(12, plan.ReviewPlan.Procedures.Count);
        Assert.All(plan.ReviewPlan.Procedures, verdict => Assert.True(verdict.IsApplicable));
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
            prepStartDate: null));
        var facts = Facts(nonZero: true);

        var result = JetAuditProgram.Finalize(plan, facts);

        Assert.Equal(0, result.Data.PrepPeriodApprovalCount);
        Assert.Equal(facts.SuspiciousKeywordsCount, result.Data.SuspiciousKeywordsCount);
        Assert.Equal(0, result.Data.UnexpectedAccountPairCount);
        Assert.Equal(facts.TrailingZerosCount, result.Data.TrailingZerosCount);
        Assert.Empty(result.Data.Preparers);
        Assert.Equal(facts.DistinctAccountCount, result.Data.DistinctAccountCount);
        Assert.Same(facts.Accounts, result.Data.Accounts);
        Assert.Equal(facts.WeekendPostingCount, result.Data.WeekendPostingCount);
        Assert.Null(result.Data.WeekendApprovalCount);
        Assert.Equal(0, result.Data.HolidayPostingCount);
        Assert.Null(result.Data.HolidayApprovalCount);
        Assert.Equal(0, result.Data.NonAuthorizedPreparerCount);
        // 0 的個數照 IDEA 版由 GL 算出；Finalize 照請求帶出個數，預期值是 Request() 帶入的 5。
        Assert.Equal(5, result.Data.ZerosThreshold);

        // 第四步的原因由規則需求表產生。
        // 第四步與第五步對同一條規則給同一個原因，畫面只顯示後端一句寫出缺什麼、到哪裡補的原因，
        // 預期值是規則需求表的句子；一條規則缺幾項就依表列順序各寫一句，科目配對沒有匯入時只寫這一項。
        const string approvalDateMissing = "「傳票核准日」尚未選擇來源欄，請回第三步欄位配對補上。";
        const string createdByMissing = "「傳票建立人員」尚未選擇來源欄，請回第三步欄位配對補上。";
        const string holidayCalendarMissing = "尚未上傳國定假日清單，請先在第二步「國定假日與補班日」上傳。";
        AssertSkipped(
            result,
            "prep_period_approval",
            approvalDateMissing + "尚未填寫期末財報準備日，請先在第一步按「修改案件資料」填寫。");
        AssertSkipped(
            result,
            "unexpected_account_pair",
            "科目配對中尚無任何科目設定分類，請先在第四步設定。");
        AssertSkipped(
            result,
            "preparer_summary",
            createdByMissing);
        AssertSkipped(
            result,
            "weekend_approval",
            approvalDateMissing);
        AssertSkipped(
            result,
            "holiday_posting",
            holidayCalendarMissing);
        AssertSkipped(
            result,
            "holiday_approval",
            holidayCalendarMissing + approvalDateMissing);
        AssertSkipped(
            result,
            "non_authorized_preparer",
            "尚未匯入授權編製人員清單，請先在第二步匯入。" + createdByMissing);
    }

    [Fact]
    public void Finalize_AllApplicableWithZeroFacts_ReturnsNaZeroAndNoReason()
    {
        var result = JetAuditProgram.Finalize(
            JetAuditProgram.Plan(Request()),
            Facts());

        // 預篩選程序共 12 個：沒有傳票日期與回溯過帳，也照舊版 JET 不含編製分錄較少的人員與使用較少的科目。
        Assert.Equal(12, result.Manifest.Procedures.Count);
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

    // 0 的個數照 IDEA 版由 GL 算出，請求帶個數；上面那則核對 Finalize 照請求帶出的個數。

    [Fact]
    public void Finalize_AllApplicable_PreservesRawCountsAndBoundedSummaries()
    {
        var facts = Facts(nonZero: true);

        var result = JetAuditProgram.Finalize(
            JetAuditProgram.Plan(Request()),
            facts);

        Assert.Equal(facts.PrepPeriodApprovalCount, result.Data.PrepPeriodApprovalCount);
        Assert.Equal(facts.UnexpectedAccountPairCount, result.Data.UnexpectedAccountPairCount);
        Assert.Same(facts.Preparers, result.Data.Preparers);
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
        Assert.Equal("A01", rare[0].AccountNumber);
        Assert.Equal("A20", rare[19].AccountNumber);
        Assert.Equal(facts.DistinctAccountCount, result.Concentration.DistinctAccountCount);
    }

    [Fact]
    public void Finalize_Concentration_WithoutCreatedByMapping_IsNotApplicableWithoutNumbers()
    {
        // 整塊沿用 preparer_summary 的作法：前置不足時不帶任何數值格（與「結果為 0」語意不同）。
        var plan = JetAuditProgram.Plan(Request(hasCreatedBy: false));
        var facts = ConcentrationFacts([9, 8], totalPreparerCount: 2, totalEntryCount: 17);

        var result = JetAuditProgram.Finalize(plan, facts);

        Assert.Equal("na", result.Concentration.Status);
        // 第四步與第五步給同一個原因，預期值是規則需求表的句子。
        Assert.Equal("「傳票建立人員」尚未選擇來源欄，請回第三步欄位配對補上。", result.Concentration.NaReason);
        Assert.Null(result.Concentration.Preparers);
        Assert.Null(result.Concentration.RareAccounts);
        Assert.Null(result.Concentration.DistinctAccountCount);
    }

    [Fact]
    public void Finalize_Concentration_EmptyPreparerSummary_IsNotApplicableWithoutReason()
    {
        // 前置齊備但期間無分錄：preparer_summary 依既有規則為 na 且無 N/A 原因，集中度同步。
        var result = JetAuditProgram.Finalize(JetAuditProgram.Plan(Request()), Facts());

        Assert.Equal("na", result.Concentration.Status);
        Assert.Null(result.Concentration.NaReason);
        Assert.Null(result.Concentration.Preparers);
    }

    private static PrescreenRequest Request(
        bool hasApprovalDate = true,
        bool hasCreatedBy = true,
        bool hasHolidays = true,
        bool hasAccountMapping = true,
        bool hasRevenue = true,
        bool hasCounterpart = true,
        bool hasAuthorizedPreparers = true,
        string? prepStartDate = "2025-12-31") =>
        new(
            ProjectId: "project",
            HasGlMapping: true,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            MoneyScale: 10_000,
            SampleSeed: 48_271,
            ResultId: "run",
            GeneratedUtc: new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero),
            PrepStartDate: prepStartDate,
            // 核准日與建立人員兩個旗標換成可用欄位清單。
            // （編譯失敗）。
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
            NonWorkingDays: [0, 6])
        {
            // 0 的個數照 IDEA 版由 GL 算出，請求要帶 0 的個數；這裡帶 5，Finalize 要照請求帶出。
            ZerosThreshold = 5
        };

    private static PrescreenFacts Facts(bool nonZero = false)
    {
        var count = nonZero ? 1 : 0;
        return new PrescreenFacts(
            PrepPeriodApprovalCount: count,
            SuspiciousKeywordsCount: count,
            UnexpectedAccountPairCount: count,
            TrailingZerosCount: count,
            Preparers: nonZero
                ? [new PreparerSummaryRow("creator", 1, 2, 3, 1)]
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
            NonAuthorizedPreparerCount: count,
            RuleVoucherCounts: PrescreenRuleKeys.FilterableKeys.ToDictionary(
                key => key,
                _ => (long)count,
                StringComparer.Ordinal),
            TotalPreparerCount: count,
            TotalEntryCount: count);
    }

    /// <summary>
    /// 集中度用 facts：<paramref name="preparerEntryCounts"/> 依遞減順序給定各編製者列數
    /// （模擬 provider 已排序的 50 列上限彙總），人工筆數固定取序號以便對位斷言。
    /// </summary>
    private static PrescreenFacts ConcentrationFacts(
        IReadOnlyList<long> preparerEntryCounts,
        long totalPreparerCount,
        long totalEntryCount,
        IReadOnlyList<AccountUsageRow>? accounts = null)
    {
        var preparers = preparerEntryCounts
            .Select((count, index) => new PreparerSummaryRow($"C{index + 1:00}", count, 0, 0, index + 1))
            .ToArray();

        return Facts() with
        {
            Preparers = preparers,
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
}
