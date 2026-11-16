using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// Application 交給預篩選的輸入。只帶案件現況，以及這次執行的結果代號與時間，
/// 不帶 GL 的資料列。可用欄位與分類名稱的意義同 <see cref="AuditProjectSnapshot"/>。
/// </summary>
internal sealed record PrescreenRequest(
    string ProjectId,
    bool HasGlMapping,
    string PeriodStart,
    string PeriodEnd,
    int MoneyScale,
    long SampleSeed,
    string ResultId,
    DateTimeOffset GeneratedUtc,
    string? PrepStartDate,
    IReadOnlyList<string>? AvailableGlFields,
    bool HasHolidays,
    bool HasAccountMapping,
    bool HasRevenue,
    bool HasCounterpart,
    bool HasAuthorizedPreparers,
    IReadOnlyList<int>? NonWorkingDays,
    AccountCategoryLabels? CategoryLabels = null)
{
    /// <summary>
    /// 「分錄金額中有連續 0 的尾數」的 0 的個數，由已確認 GL 配對的控制總數算出（<see cref="TrailingZeroThreshold.FromCommittedGl"/>）；
    /// 計數與回應的 zerosThreshold 都用這個數字。
    /// </summary>
    public required int ZerosThreshold { get; init; }
}

/// <summary>
/// 預篩選的計畫。<see cref="ReviewPlan"/> 是公開的 <c>JetAuditProgram.Plan</c> 產生的程序判定，
/// 各規則要不要執行都從這一份判定讀出。尾零的 0 的個數跟著請求（<see cref="PrescreenRequest.ZerosThreshold"/>）。
/// </summary>
internal sealed record PrescreenPlan(
    PrescreenRequest Request,
    AuditExecutionPlan ReviewPlan)
{
    internal bool RunPrepPeriodApproval => IsApplicable("prep_period_approval");

    internal bool RunUnexpectedAccountPair => IsApplicable("unexpected_account_pair");

    internal bool RunPreparerSummary => IsApplicable("preparer_summary");

    internal bool RunWeekendApproval => IsApplicable("weekend_approval");

    internal bool RunHolidayPosting => IsApplicable("holiday_posting");

    internal bool RunHolidayApproval => IsApplicable("holiday_approval");

    internal bool RunNonAuthorizedPreparer => IsApplicable("non_authorized_preparer");

    internal bool IsApplicable(string slug) =>
        ReviewPlan.Procedures.Single(verdict =>
            string.Equals(verdict.Definition.Slug, slug, StringComparison.Ordinal))
        .IsApplicable;
}

/// <summary>
/// 資料庫執行預篩選後交回 AuditCore 的原始數字。所有筆數都不是 nullable。
/// 沒有執行的規則在回應裡要寫 0 還是 null、尾零的 0 的個數，以及 status，都不由 Infrastructure 決定。
/// <c>Preparers</c> 與 <c>Accounts</c> 沿用最多 50 列的摘要型別。
/// <c>TotalPreparerCount</c> 與 <c>TotalEntryCount</c> 是整個查核期間的總數，不是這 50 列以內的合計。
/// <c>RuleVoucherCounts</c> 與十條逐筆規則的分錄筆數來自同一批規則查詢，不另寫第二套條件。
/// </summary>
internal sealed record PrescreenFacts(
    long PrepPeriodApprovalCount,
    long SuspiciousKeywordsCount,
    long UnexpectedAccountPairCount,
    long TrailingZerosCount,
    IReadOnlyList<PreparerSummaryRow> Preparers,
    long DistinctAccountCount,
    IReadOnlyList<AccountUsageRow> Accounts,
    long WeekendPostingCount,
    long WeekendApprovalCount,
    long HolidayPostingCount,
    long HolidayApprovalCount,
    long BlankDescriptionCount,
    long NonAuthorizedPreparerCount,
    IReadOnlyDictionary<string, long> RuleVoucherCounts,
    long TotalPreparerCount = 0,
    long TotalEntryCount = 0);

/// <summary>
/// 流程總覽區塊⑤集中度分析要顯示的數字，列數有上限。累積占比、「其他」的合計與前五名的占比都在後端算好，
/// 前端只照著顯示與排版。整塊沿用 <c>preparer_summary</c> 的程序判定：那項程序是 N/A 時這一塊也是 N/A，
/// 而且不帶任何數字，因為 N/A 和「結果是 0」是兩回事，不能算在一起。
/// </summary>
internal sealed record PrescreenConcentration(
    string Status,
    string? NaReason,
    PreparerConcentration? Preparers,
    IReadOnlyList<AccountUsageRow>? RareAccounts,
    long? DistinctAccountCount);

/// <summary>
/// 編製人員集中度。<see cref="Top"/> 依分錄筆數遞減、最多 10 位；所有百分比的分母都是
/// 查核期間全母體（<see cref="TotalEntryCount"/>），不是前 N 名合計。分母為 0 時回 null，
/// 不以 0% 冒充。
/// </summary>
internal sealed record PreparerConcentration(
    IReadOnlyList<PreparerConcentrationRow> Top,
    long OthersEntryCount,
    long TotalPreparerCount,
    long TotalEntryCount,
    decimal? Top5SharePct);

internal sealed record PreparerConcentrationRow(
    string CreatedBy,
    long EntryCount,
    long ManualCount,
    decimal? CumulativePct);

/// <summary>
/// 逐筆標記的預篩選規則在整個查核期間各命中多少。<c>Population</c> 是查核期間 GL 的分錄筆數；
/// 規則依 <see cref="RuleCatalog"/> 裡 RowTag 規則的登錄順序輸出。N/A 的規則不帶數字；
/// 有執行但沒有命中的規則保留 0。分錄筆數是 0 時，只有 <c>RatePct</c> 是 null。
/// </summary>
internal sealed record PrescreenRulePeriod(
    long Population,
    IReadOnlyList<PrescreenRulePeriodRow> Rules);

internal sealed record PrescreenRulePeriodRow(
    string Key,
    string? NaReason,
    long? HitLines,
    long? HitVouchers,
    decimal? RatePct);

/// <summary>集中度分析的固定呈現上限（後端裁切，前端不得自行改變切點）。</summary>
internal static class ConcentrationLimits
{
    internal const int TopPreparerRows = 10;
    internal const int ShareLeaderRows = 5;
    internal const int RareAccountRows = 20;
}

/// <summary>
/// 預篩選 Finalize 的結果。<c>Data</c> 的形狀和 action 回應、報告使用的相同；
/// <c>Manifest</c> 保留每項程序的適用性判定、狀態與計數；
/// <c>Concentration</c> 與 <c>RulePeriod</c> 是流程總覽要顯示的數字，列數有上限。
/// 週末與假日兩張卡各有過帳與核准兩側，兩側的原因由同一個方法合併。
/// </summary>
internal sealed record PrescreenResult(
    PrescreenRunResult Data,
    AuditRunManifest Manifest,
    PrescreenConcentration Concentration,
    PrescreenRulePeriod RulePeriod,
    PrescreenActivityReasons WeekendActivity,
    PrescreenActivityReasons HolidayActivity);

/// <summary>
/// 預篩選的執行介面，由 Infrastructure 實作。實作只執行參數化的集合式 SQL，並把查詢結果原樣讀成數字；
/// 前置條件、status、N/A 與固定的規則參數都不由它判定。
/// </summary>
internal interface IPrescreenFactsPort
{
    Task<PrescreenFacts> ExecuteAsync(
        PrescreenPlan plan,
        CancellationToken cancellationToken);
}

public static partial class JetAuditProgram
{
    /// <summary>
    /// 預篩選正式流程用的 Plan。前置條件與 N/A 原因的先後順序，只由收 <see cref="AuditProjectSnapshot"/> 的公開 Plan 決定。
    /// </summary>
    internal static PrescreenPlan Plan(PrescreenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reviewPlan = Plan(
            new AuditProjectSnapshot(
                ProjectId: request.ProjectId,
                HasGlMapping: request.HasGlMapping,
                HasTbMapping: false,
                PeriodStart: request.PeriodStart,
                PeriodEnd: request.PeriodEnd,
                MoneyScale: request.MoneyScale,
                SampleSeed: request.SampleSeed,
                PrepStartDate: request.PrepStartDate,
                AvailableGlFields: request.AvailableGlFields,
                HasHolidays: request.HasHolidays,
                HasAccountMapping: request.HasAccountMapping,
                HasRevenue: request.HasRevenue,
                HasCounterpart: request.HasCounterpart,
                HasAuthorizedPreparers: request.HasAuthorizedPreparers,
                NonWorkingDays: request.NonWorkingDays,
                CategoryLabels: request.CategoryLabels),
            new AuditUserParameters(
                ResultId: request.ResultId,
                GeneratedUtc: request.GeneratedUtc,
                SampleSize: 0,
                ActionName: PrescreenAction));

        return new PrescreenPlan(request, reviewPlan);
    }

    /// <summary>
    /// 預篩選的 Finalize。AuditCore 依計畫裡的適用性判定，丟掉不適用規則查回的數字；每條規則讀的都是規則需求表的同一個判定。
    /// 各項程序的 status 與 N/A 原因再交給公開的 Finalize 產生。
    /// </summary>
    internal static PrescreenResult Finalize(
        PrescreenPlan plan,
        PrescreenFacts facts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts);

        // 每條規則都讀同一份適用性判定（規則需求表）；不適用的規則不帶計數出去，核准側沒有判定時留 null，和回應的寫法一致。
        // 只需要必填欄的六條規則也一樣，十二條都照同一個判定。
        bool Runs(string key) => plan.IsApplicable(PrescreenRuleDefinitions.ByKey[key].Slug);
        var data = new PrescreenRunResult(
            PrepPeriodApprovalCount: Runs(PrescreenRuleKeys.PrepPeriodApproval) ? facts.PrepPeriodApprovalCount : 0,
            SuspiciousKeywordsCount: Runs(PrescreenRuleKeys.SuspiciousKeywords) ? facts.SuspiciousKeywordsCount : 0,
            UnexpectedAccountPairCount: Runs(PrescreenRuleKeys.UnexpectedAccountPair) ? facts.UnexpectedAccountPairCount : 0,
            TrailingZerosCount: Runs(PrescreenRuleKeys.TrailingZeros) ? facts.TrailingZerosCount : 0,
            ZerosThreshold: plan.Request.ZerosThreshold,
            Preparers: Runs(JetRuleRequirements.PreparerSummary) ? facts.Preparers : Array.Empty<PreparerSummaryRow>(),
            DistinctAccountCount: Runs(JetRuleRequirements.RareAccounts) ? facts.DistinctAccountCount : 0,
            Accounts: Runs(JetRuleRequirements.RareAccounts) ? facts.Accounts : Array.Empty<AccountUsageRow>(),
            WeekendPostingCount: Runs(PrescreenRuleKeys.WeekendPosting) ? facts.WeekendPostingCount : 0,
            WeekendApprovalCount: Runs(PrescreenRuleKeys.WeekendApproval) ? facts.WeekendApprovalCount : null,
            HolidayPostingCount: Runs(PrescreenRuleKeys.HolidayPosting) ? facts.HolidayPostingCount : 0,
            HolidayApprovalCount: Runs(PrescreenRuleKeys.HolidayApproval) ? facts.HolidayApprovalCount : null,
            BlankDescriptionCount: Runs(PrescreenRuleKeys.BlankDescription) ? facts.BlankDescriptionCount : 0,
            NonAuthorizedPreparerCount: Runs(PrescreenRuleKeys.NonAuthorizedPreparer) ? facts.NonAuthorizedPreparerCount : 0);
        var manifest = Finalize(plan.ReviewPlan, new AuditOutcome(Prescreen: data));

        var snapshot = plan.ReviewPlan.ProjectSnapshot;
        return new PrescreenResult(
            data,
            manifest,
            Concentrate(manifest, data, facts),
            BuildRulePeriod(manifest, data, facts),
            PrescreenProcedures.ActivityReasons(snapshot, "weekend_posting", "weekend_approval"),
            PrescreenProcedures.ActivityReasons(snapshot, "holiday_posting", "holiday_approval"));
    }

    /// <summary>
    /// 組出 rulePeriod：分錄筆數取 Finalize 已定的數字，傳票張數取同一批查詢回傳的不重複傳票數，分母是整個查核期間的分錄筆數。
    /// 規則適不適用只讀 manifest，不從 status 反推，因為 status 是 "na" 也可能只是沒有命中。
    /// </summary>
    private static PrescreenRulePeriod BuildRulePeriod(
        AuditRunManifest manifest,
        PrescreenRunResult data,
        PrescreenFacts facts)
    {
        var descriptors = RuleCatalog.All
            .Where(descriptor => descriptor.Shape == RuleShape.RowTag)
            .ToArray();
        if (facts.RuleVoucherCounts.Count != descriptors.Length
            || descriptors.Any(descriptor =>
                !facts.RuleVoucherCounts.ContainsKey(descriptor.WireKey)))
        {
            throw new InvalidOperationException(
                "Prescreen rulePeriod 的去重傳票 facts 必須精確涵蓋全部 row-tag 規則。");
        }

        var population = facts.TotalEntryCount;
        var rules = new List<PrescreenRulePeriodRow>(descriptors.Length);
        foreach (var descriptor in descriptors)
        {
            var verdict = manifest.Procedures.Single(candidate =>
                string.Equals(
                    candidate.Definition.Slug,
                    descriptor.Slug,
                    StringComparison.Ordinal));
            if (!verdict.IsApplicable)
            {
                rules.Add(new PrescreenRulePeriodRow(
                    descriptor.WireKey,
                    verdict.NaReason,
                    null,
                    null,
                    null));
                continue;
            }

            var hitLines = CountForPrescreen(descriptor.Slug, data);
            var hitVouchers = facts.RuleVoucherCounts[descriptor.WireKey];
            rules.Add(new PrescreenRulePeriodRow(
                descriptor.WireKey,
                null,
                hitLines,
                hitVouchers,
                RatePct(hitLines, population)));
        }

        return new PrescreenRulePeriod(population, rules);
    }

    /// <summary>全期命中行數占 GL 行母體的百分比；分母為 0 時不冒充 0%。</summary>
    private static decimal? RatePct(long hitLines, long population) =>
        population <= 0
            ? null
            : (decimal)hitLines * 100m / population;

    /// <summary>
    /// 集中度分析的呈現事實。適用性完全沿用 <c>preparer_summary</c> 已完成的適用性判定，
    /// 不另立第二套判定；不適用時整塊不帶數值。
    /// </summary>
    private static PrescreenConcentration Concentrate(
        AuditRunManifest manifest,
        PrescreenRunResult data,
        PrescreenFacts facts)
    {
        var verdict = manifest.Procedures.Single(candidate =>
            string.Equals(candidate.Definition.Slug, "preparer_summary", StringComparison.Ordinal));
        var status = verdict.Status
            ?? throw new InvalidOperationException("程序 'preparer_summary' 尚未 Finalize。");

        if (!string.Equals(status, "V", StringComparison.Ordinal))
        {
            return new PrescreenConcentration(status, verdict.NaReason, null, null, null);
        }

        var totalEntryCount = facts.TotalEntryCount;
        var top = new List<PreparerConcentrationRow>(ConcentrationLimits.TopPreparerRows);
        var running = 0L;
        var leaderEntryCount = 0L;
        for (var i = 0; i < data.Preparers.Count && i < ConcentrationLimits.TopPreparerRows; i++)
        {
            var preparer = data.Preparers[i];
            running += preparer.EntryCount;
            if (i < ConcentrationLimits.ShareLeaderRows)
            {
                leaderEntryCount = running;
            }

            top.Add(new PreparerConcentrationRow(
                preparer.CreatedBy,
                preparer.EntryCount,
                preparer.ManualCount,
                SharePct(running, totalEntryCount)));
        }

        return new PrescreenConcentration(
            status,
            verdict.NaReason,
            new PreparerConcentration(
                top,
                Math.Max(0L, totalEntryCount - running),
                facts.TotalPreparerCount,
                totalEntryCount,
                SharePct(leaderEntryCount, totalEntryCount)),
            data.Accounts.Take(ConcentrationLimits.RareAccountRows).ToArray(),
            data.DistinctAccountCount);
    }

    /// <summary>占比取到小數一位；分母是 0 時回 null，畫面不畫成 0%。</summary>
    private static decimal? SharePct(long part, long total) =>
        total <= 0
            ? null
            : Math.Round((decimal)part * 100m / total, 1, MidpointRounding.AwayFromZero);
}
