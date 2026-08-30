using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// prescreen.run：預篩選規則以 set-based SQL 執行（manifest Prescreen 章節；
/// wire key 依 guide §4 命名登錄表）。前置條件→na 由 AuditCore Plan 裁定（guide §5）：
/// 0 命中也標 na（count 仍回 0）；naReason 只在前置不足時提供。
/// 完整 response 存 result_rule_run 供 resume。
/// </summary>
public sealed class PrescreenRunHandler : IApplicationActionHandler
{
    private readonly IPrescreenFactsPort prescreenFactsPort;
    private readonly IMappingStateStore mappingStore;
    private readonly ICalendarStore calendarStore;
    private readonly IAccountMappingStore accountMappingStore;
    private readonly IAuthorizedPreparerStore authorizedPreparerStore;
    private readonly IRuleRunStore runStore;
    private readonly IProjectStore projectStore;
    private readonly ProjectSession session;

    internal PrescreenRunHandler(
        IPrescreenFactsPort prescreenFactsPort,
        IMappingStateStore mappingStore,
        ICalendarStore calendarStore,
        IAccountMappingStore accountMappingStore,
        IAuthorizedPreparerStore authorizedPreparerStore,
        IRuleRunStore runStore,
        IProjectStore projectStore,
        ProjectSession session)
    {
        this.prescreenFactsPort = prescreenFactsPort;
        this.mappingStore = mappingStore;
        this.calendarStore = calendarStore;
        this.accountMappingStore = accountMappingStore;
        this.authorizedPreparerStore = authorizedPreparerStore;
        this.runStore = runStore;
        this.projectStore = projectStore;
        this.session = session;
    }

    public string Action => "prescreen.run";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();
        await CompletenessEligibilitySupport.RequireCurrentAsync(
            runStore,
            projectId,
            cancellationToken);

        var glMapping = await mappingStore.FindAsync(projectId, DatasetKind.Gl, cancellationToken);
        JetAuditProgram.RequireGlMapping(glMapping is not null);

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        var approvalMode = glMapping!.GlOptions?.ApprovalDateMode
                           ?? GlMappingOptions.NormalizeLegacy(glMapping.Mapping).ApprovalDateMode;
        var hasApprovalDate = approvalMode != ApprovalDateModeNames.Unmapped;
        var hasCreatedBy = glMapping.Mapping.ContainsKey(GlMappingKeys.CreateBy);
        var hasHolidays = await calendarStore.CountAsync(projectId, CalendarDayType.Holiday, cancellationToken) > 0;
        var lastPeriodStart = document.LastAccountingPeriodDate;

        var accountMappingState = await accountMappingStore.FindStateAsync(projectId, cancellationToken);
        var hasAuthorizedPreparers = await authorizedPreparerStore.CountAsync(projectId, cancellationToken) > 0;

        var runId = Guid.NewGuid().ToString("N");
        var generatedUtc = DateTimeOffset.UtcNow;
        var plan = JetAuditProgram.Plan(
            new PrescreenRequest(
                projectId,
                HasGlMapping: glMapping is not null,
                document.PeriodStart,
                document.PeriodEnd,
                document.MoneyScale,
                document.SampleSeed.GetValueOrDefault(),
                runId,
                generatedUtc,
                LastPeriodStart: lastPeriodStart,
                HasApprovalDate: hasApprovalDate,
                HasCreatedBy: hasCreatedBy,
                HasHolidays: hasHolidays,
                HasAccountMapping: accountMappingState is not null,
                HasRevenue: accountMappingState?.HasRevenue ?? false,
                HasCounterpart: accountMappingState?.HasCounterpart ?? false,
                HasAuthorizedPreparers: hasAuthorizedPreparers,
                NonWorkingDays: document.NonWorkingDays));
        var facts = await JetAuditProgram.ExecuteAsync(plan, prescreenFactsPort, cancellationToken);
        var prescreen = JetAuditProgram.Finalize(plan, facts);
        var positioning = JetAuditProgram.RenderPrescreenPositioning();
        var runManifest = prescreen.Manifest;
        var result = prescreen.Data;
        var scale = document.MoneyScale;

        var postPeriodApproval = Verdict(runManifest, "post_period_approval");
        var unexpectedAccountPair = Verdict(runManifest, "unexpected_account_pair");
        var creatorSummary = Verdict(runManifest, "creator_summary");
        var weekendPosting = Verdict(runManifest, "weekend_posting");
        var weekendApproval = Verdict(runManifest, "weekend_approval");
        var holidayPosting = Verdict(runManifest, "holiday_posting");
        var holidayApproval = Verdict(runManifest, "holiday_approval");
        var nonAuthorizedPreparer = Verdict(runManifest, "non_authorized_preparer");

        var dto = new
        {
            postPeriodApproval = RuleStatus(result.PostPeriodApprovalCount, postPeriodApproval),
            suspiciousKeywords = new
            {
                status = Verdict(runManifest, "suspicious_keywords").Status!,
                count = result.SuspiciousKeywordsCount
            },
            unexpectedAccountPair = RuleStatus(result.UnexpectedAccountPairCount, unexpectedAccountPair),
            trailingZeros = new
            {
                status = Verdict(runManifest, "trailing_zeros").Status!,
                count = result.TrailingZerosCount,
                zerosThreshold = result.ZerosThreshold
            },
            creatorSummary = new
            {
                status = creatorSummary.Status!,
                naReason = creatorSummary.NaReason,
                creators = result.Creators.Select(c => new
                {
                    createdBy = c.CreatedBy,
                    entryCount = c.EntryCount,
                    debitTotal = ToDisplay(c.DebitTotalScaled, scale),
                    creditTotal = ToDisplay(c.CreditTotalScaled, scale),
                    manualCount = c.ManualCount
                }).ToArray()
            },
            rareAccounts = new
            {
                status = Verdict(runManifest, "rare_accounts").Status!,
                distinctAccountCount = result.DistinctAccountCount,
                accounts = result.Accounts.Select(a => new
                {
                    accountCode = a.AccountCode,
                    accountName = a.AccountName,
                    entryCount = a.EntryCount,
                    debitTotal = ToDisplay(a.DebitTotalScaled, scale),
                    creditTotal = ToDisplay(a.CreditTotalScaled, scale)
                }).ToArray()
            },
            weekendActivity = new
            {
                status = CombinedStatus(weekendPosting, weekendApproval),
                naReason = weekendApproval.NaReason,
                postingCount = result.WeekendPostingCount,
                approvalCount = result.WeekendApprovalCount
            },
            holidayActivity = new
            {
                status = CombinedStatus(holidayPosting, holidayApproval),
                naReason = holidayPosting.NaReason,
                postingCount = result.HolidayPostingCount,
                approvalCount = result.HolidayApprovalCount
            },
            blankDescription = new
            {
                status = Verdict(runManifest, "blank_description").Status!,
                count = result.BlankDescriptionCount
            },
            backdatedPosting = new
            {
                status = Verdict(runManifest, "backdated_posting").Status!,
                count = result.BackdatedPostingCount
            },
            nonAuthorizedPreparer = RuleStatus(result.NonAuthorizedPreparerCount, nonAuthorizedPreparer),
            lowFrequencyPreparer = new
            {
                status = Verdict(runManifest, "low_frequency_preparer").Status!,
                count = result.LowFrequencyPreparerCount
            },
            lowFrequencyAccount = new
            {
                status = Verdict(runManifest, "low_frequency_account").Status!,
                count = result.LowFrequencyAccountCount
            },
            concentration = ConcentrationWire(prescreen.Concentration),
            rulePeriod = RulePeriodWire(prescreen.RulePeriod),
            positioning = PositioningWire(positioning),
            resultRef = new { runId, generatedUtc, logicVersion = RuleLogicVersions.Prescreen }
        };

        var summaryJson = JsonSerializer.Serialize(dto, JetJsonStorage.Options);
        await runStore.SaveAsync(
            projectId,
            new RuleRunRecord(runId, RuleRunKinds.Prescreen, generatedUtc, summaryJson),
            CancellationToken.None);

        await MappingCommitShared.AdvanceStepAsync(
            projectStore,
            document,
            ProgramGraph.Current.RequireNode(Action),
            CancellationToken.None);

        using var parsed = JsonDocument.Parse(summaryJson);
        return parsed.RootElement.Clone();
    }

    /// <summary>
    /// 集中度分析（總覽區塊⑤）。所有顯示用彙總已由 AuditCore 算好，這裡只做 wire 命名；
    /// 不適用時 preparers／rareAccounts／distinctAccountCount 一律為 null，不以 0 冒充。
    /// </summary>
    private static object ConcentrationWire(PrescreenConcentration concentration) => new
    {
        status = concentration.Status,
        naReason = concentration.NaReason,
        preparers = concentration.Preparers is not { } preparers ? null : (object)new
        {
            top = preparers.Top.Select(row => new
            {
                createdBy = row.CreatedBy,
                entryCount = row.EntryCount,
                manualCount = row.ManualCount,
                cumulativePct = row.CumulativePct
            }).ToArray(),
            othersEntryCount = preparers.OthersEntryCount,
            totalPreparerCount = preparers.TotalPreparerCount,
            totalEntryCount = preparers.TotalEntryCount,
            top5SharePct = preparers.Top5SharePct
        },
        rareAccounts = concentration.RareAccounts?.Select(account => new
        {
            accountCode = account.AccountCode,
            accountName = account.AccountName,
            entryCount = account.EntryCount
        }).ToArray(),
        distinctAccountCount = concentration.DistinctAccountCount
    };

    /// <summary>
    /// 預篩選 row-tag 的全期命中分布。AuditCore 已完成適用性、分母與占比裁定；
    /// handler 僅轉成 camelCase wire。N/A 規則的三個數值欄皆為 null。
    /// </summary>
    private static object RulePeriodWire(PrescreenRulePeriod rulePeriod) => new
    {
        population = rulePeriod.Population,
        rules = rulePeriod.Rules.Select(rule => new
        {
            key = rule.Key,
            naReason = rule.NaReason,
            hitLines = rule.HitLines,
            hitVouchers = rule.HitVouchers,
            ratePct = rule.RatePct
        }).ToArray()
    };

    /// <summary>AuditCore 的定位文案只在此轉成 camelCase wire，不在 Application 改寫語意。</summary>
    private static object PositioningWire(PrescreenPositioning positioning) => new
    {
        aggregateGuidance = positioning.AggregateGuidance,
        signalGuidance = positioning.SignalGuidance,
        reportGuidance = positioning.ReportGuidance,
        overviewGuidance = positioning.OverviewGuidance,
        exportDefaultGuidance = positioning.ExportDefaultGuidance,
        exportPendingRunGuidance = positioning.ExportPendingRunGuidance
    };

    private static object RuleStatus(long count, ProcedureVerdict verdict)
    {
        return new
        {
            status = verdict.Status!,
            naReason = verdict.NaReason,
            count
        };
    }

    private static string CombinedStatus(ProcedureVerdict first, ProcedureVerdict second) =>
        string.Equals(first.Status, "V", StringComparison.Ordinal)
        || string.Equals(second.Status, "V", StringComparison.Ordinal)
            ? "V"
            : "na";

    private static ProcedureVerdict Verdict(AuditRunManifest manifest, string slug) =>
        manifest.Procedures.Single(verdict =>
            string.Equals(verdict.Definition.Slug, slug, StringComparison.Ordinal));

    private static decimal ToDisplay(long scaled, int moneyScale) => (decimal)scaled / moneyScale;
}
