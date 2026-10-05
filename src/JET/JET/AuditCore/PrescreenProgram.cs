using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// Prescreen typed lifecycle 的 Application 輸入。只攜帶有界案件事實與 run identity，
/// 不攜帶 GL 完整列集。
/// </summary>
internal sealed record PrescreenRequest(
    string ProjectId,
    bool HasGlMapping,
    string PeriodStart,
    string PeriodEnd,
    int MoneyScale,
    long SampleSeed,
    string RunId,
    DateTimeOffset GeneratedUtc,
    string? LastPeriodStart,
    bool HasApprovalDate,
    bool HasCreatedBy,
    bool HasHolidays,
    bool HasAccountMapping,
    bool HasRevenue,
    bool HasCounterpart,
    bool HasAuthorizedPreparers,
    IReadOnlyList<int>? NonWorkingDays,
    bool HasVoucherDate);

/// <summary>
/// Prescreen typed plan。<see cref="ReviewPlan"/> 沿用既有 public review contract，
/// 各規則要不要執行，都從同一份程序適用性判定衍生。
/// </summary>
internal sealed record PrescreenPlan(
    PrescreenRequest Request,
    AuditExecutionPlan ReviewPlan,
    int ZerosThreshold)
{
    internal bool RunPostPeriodApproval => IsApplicable("post_period_approval");

    internal bool RunUnexpectedAccountPair => IsApplicable("unexpected_account_pair");

    internal bool RunCreatorSummary => IsApplicable("creator_summary");

    internal bool RunWeekendApproval => IsApplicable("weekend_approval");

    internal bool RunHolidayPosting => IsApplicable("holiday_posting");

    internal bool RunHolidayApproval => IsApplicable("holiday_approval");

    internal bool RunNonAuthorizedPreparer => IsApplicable("non_authorized_preparer");
    internal bool RunBackdatedPosting => IsApplicable("backdated_posting");
    internal bool RunLowFrequencyPreparer => IsApplicable("low_frequency_preparer");

    internal bool IsApplicable(string slug) =>
        ReviewPlan.Procedures.Single(verdict =>
            string.Equals(verdict.Definition.Slug, slug, StringComparison.Ordinal))
        .IsApplicable;
}

/// <summary>
/// Provider 執行後回到 AuditCore 的 raw facts。所有計數皆為非 nullable；
/// 跳過規則的 outward 0／null、固定尾零門檻與 status 不由 Infrastructure 決定。
/// Creators／Accounts 沿用既有 50 列上限的有界摘要型別；
/// TotalPreparerCount／TotalEntryCount 是查核期間的全母體純量（非上限內合計）；
/// RuleVoucherCounts 與既有 13 條 row-tag 行數來自同一批規則查詢，不另跑第二套述詞。
/// </summary>
internal sealed record PrescreenFacts(
    long PostPeriodApprovalCount,
    long SuspiciousKeywordsCount,
    long UnexpectedAccountPairCount,
    long TrailingZerosCount,
    IReadOnlyList<CreatorSummaryRow> Creators,
    long DistinctAccountCount,
    IReadOnlyList<AccountUsageRow> Accounts,
    long WeekendPostingCount,
    long WeekendApprovalCount,
    long HolidayPostingCount,
    long HolidayApprovalCount,
    long BlankDescriptionCount,
    long BackdatedPostingCount,
    long NonAuthorizedPreparerCount,
    long LowFrequencyPreparerCount,
    long LowFrequencyAccountCount,
    IReadOnlyDictionary<string, long> RuleVoucherCounts,
    long TotalPreparerCount = 0,
    long TotalEntryCount = 0)
{
    public long? LowFrequencyDistinctAccountCount { get; init; }
}

/// <summary>
/// 集中度分析（流程總覽區塊⑤）的有界呈現事實。累積占比、「其他」彙總與前五佔比一律
/// 在此算好，前端只鏡射與格式化。整塊沿用 <c>creator_summary</c> 的程序裁定：該程序為
/// N/A 時本區塊亦為 N/A，且不攜帶任何數值格（與「結果為 0」語意不同，不併入同一統計）。
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
/// 預篩選 row-tag 的全期命中分布。Population 是查核期間 GL 行母體；規則依
/// <see cref="RuleCatalog"/> 的 RowTag 登錄順序輸出。真正 N/A 的規則不攜帶數值，
/// 適用但零命中則保留 0；母體為 0 時只有 RatePct 為 null。
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
/// AuditCore Finalize 的 typed prescreen 產物。Data 保留既有 wire／report compatibility
/// shape；Manifest 保留每項程序的適用性判定、狀態與計數；
/// Concentration 與 RulePeriod 是流程總覽的有界呈現事實。
/// </summary>
internal sealed record PrescreenResult(
    PrescreenRunResult Data,
    AuditRunManifest Manifest,
    PrescreenConcentration Concentration,
    PrescreenRulePeriod RulePeriod);

/// <summary>
/// Prescreen 的 typed Infrastructure port。實作只執行 parameterized set-based SQL
/// 與 raw reader mapping，不裁定 prerequisites、status、N/A 或固定政策值。
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
    /// Prescreen production path 的 typed Plan。既有 public review plan 是 prerequisites
    /// 與 N/A precedence 的唯一來源。
    /// </summary>
    internal static PrescreenPlan Plan(PrescreenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reviewPlan = Plan(
            new AuditCaseSnapshot(
                ProjectId: request.ProjectId,
                HasGlMapping: request.HasGlMapping,
                HasTbMapping: false,
                PeriodStart: request.PeriodStart,
                PeriodEnd: request.PeriodEnd,
                MoneyScale: request.MoneyScale,
                SampleSeed: request.SampleSeed,
                LastPeriodStart: request.LastPeriodStart,
                HasApprovalDate: request.HasApprovalDate,
                HasCreatedBy: request.HasCreatedBy,
                HasHolidays: request.HasHolidays,
                HasAccountMapping: request.HasAccountMapping,
                HasRevenue: request.HasRevenue,
                HasCounterpart: request.HasCounterpart,
                HasAuthorizedPreparers: request.HasAuthorizedPreparers,
                NonWorkingDays: request.NonWorkingDays,
                HasVoucherDate: request.HasVoucherDate),
            new AuditUserParameters(
                RunId: request.RunId,
                GeneratedUtc: request.GeneratedUtc,
                SampleSize: 0,
                ActionName: PrescreenAction));

        return new PrescreenPlan(
            request,
            reviewPlan,
            TrailingZeroThreshold.DefaultZerosThreshold);
    }

    /// <summary>
    /// Prescreen typed Finalize：AuditCore 依 plan 丟棄不適用規則的 raw facts、套用固定
    /// 尾零門檻，再沿用既有 public manifest finalizer 產生 status 與 N/A 證據。
    /// </summary>
    internal static PrescreenResult Finalize(
        PrescreenPlan plan,
        PrescreenFacts facts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts);

        var data = new PrescreenRunResult(
            PostPeriodApprovalCount: plan.RunPostPeriodApproval
                ? facts.PostPeriodApprovalCount
                : 0,
            SuspiciousKeywordsCount: facts.SuspiciousKeywordsCount,
            UnexpectedAccountPairCount: plan.RunUnexpectedAccountPair
                ? facts.UnexpectedAccountPairCount
                : 0,
            TrailingZerosCount: facts.TrailingZerosCount,
            ZerosThreshold: plan.ZerosThreshold,
            Creators: plan.RunCreatorSummary
                ? facts.Creators
                : Array.Empty<CreatorSummaryRow>(),
            DistinctAccountCount: facts.DistinctAccountCount,
            Accounts: facts.Accounts,
            WeekendPostingCount: facts.WeekendPostingCount,
            WeekendApprovalCount: plan.RunWeekendApproval
                ? facts.WeekendApprovalCount
                : null,
            HolidayPostingCount: plan.RunHolidayPosting
                ? facts.HolidayPostingCount
                : 0,
            HolidayApprovalCount: plan.RunHolidayApproval
                ? facts.HolidayApprovalCount
                : null,
            BlankDescriptionCount: facts.BlankDescriptionCount,
            BackdatedPostingCount: plan.RunBackdatedPosting ? facts.BackdatedPostingCount : 0,
            NonAuthorizedPreparerCount: plan.RunNonAuthorizedPreparer
                ? facts.NonAuthorizedPreparerCount
                : 0,
            LowFrequencyPreparerCount: plan.RunLowFrequencyPreparer ? facts.LowFrequencyPreparerCount : 0,
            LowFrequencyAccountCount: facts.LowFrequencyAccountCount)
        {
            LowFrequencyDistinctAccountCount = facts.LowFrequencyDistinctAccountCount
        };
        var manifest = Finalize(plan.ReviewPlan, new AuditOutcome(Prescreen: data));

        return new PrescreenResult(
            data,
            manifest,
            Concentrate(manifest, data, facts),
            BuildRulePeriod(manifest, data, facts));
    }

    /// <summary>
    /// 以既有 finalized 行數、同 query 取得的去重傳票數與全期母體建立 rulePeriod。
    /// 適用性只讀同一份 manifest，不以 outward status 推測，因 status="na" 亦可能只是零命中。
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
    /// 集中度分析的呈現事實。適用性完全沿用 <c>creator_summary</c> 已完成的適用性判定，
    /// 不另立第二套裁定；不適用時整塊不帶數值。
    /// </summary>
    private static PrescreenConcentration Concentrate(
        AuditRunManifest manifest,
        PrescreenRunResult data,
        PrescreenFacts facts)
    {
        var verdict = manifest.Procedures.Single(candidate =>
            string.Equals(candidate.Definition.Slug, "creator_summary", StringComparison.Ordinal));
        var status = verdict.Status
            ?? throw new InvalidOperationException("程序 'creator_summary' 尚未 Finalize。");

        if (!string.Equals(status, "V", StringComparison.Ordinal))
        {
            return new PrescreenConcentration(status, verdict.NaReason, null, null, null);
        }

        var totalEntryCount = facts.TotalEntryCount;
        var top = new List<PreparerConcentrationRow>(ConcentrationLimits.TopPreparerRows);
        var running = 0L;
        var leaderEntryCount = 0L;
        for (var i = 0; i < data.Creators.Count && i < ConcentrationLimits.TopPreparerRows; i++)
        {
            var creator = data.Creators[i];
            running += creator.EntryCount;
            if (i < ConcentrationLimits.ShareLeaderRows)
            {
                leaderEntryCount = running;
            }

            top.Add(new PreparerConcentrationRow(
                creator.CreatedBy,
                creator.EntryCount,
                creator.ManualCount,
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

    /// <summary>占比取一位小數；分母為 0 → null（不畫 0%）。</summary>
    private static decimal? SharePct(long part, long total) =>
        total <= 0
            ? null
            : Math.Round((decimal)part * 100m / total, 1, MidpointRounding.AwayFromZero);
}
