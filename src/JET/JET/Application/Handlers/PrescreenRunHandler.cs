using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// prescreen.run：預篩選規則以 set-based SQL 執行，wire key 見 <see cref="PrescreenRuleKeys"/>。
/// 前置條件不足時標 na，由 AuditCore 的 Plan 階段決定：
/// 0 命中也標 na（count 仍回 0）；naReason 只在前置不足時提供。
/// 完整 response 存 prescreen_result 供 resume。
/// </summary>
public sealed class PrescreenRunHandler : IApplicationActionHandler
{
    private readonly IProjectStore projectStore;
    private readonly ProjectSession session;
    private readonly ILogger? logger;

    internal PrescreenRunHandler(
        IProjectStore projectStore,
        ProjectSession session, ILogger? logger = null)
    {
        this.projectStore = projectStore;
        this.session = session;
        this.logger = logger;
    }

    public string Action => "prescreen.run";

    /// <summary>預篩選跑完後，案件至少推進到第四步。</summary>
    internal const int WorkflowStep = 4;

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        SupportOperationStage.Enter("state_read");
        await CompletenessEligibilitySupport.RequireCurrentAsync(
            repositories.Results,
            projectId,
            cancellationToken);

        var glMapping = await repositories.MappingStates.FindAsync(projectId, DatasetKind.Gl, cancellationToken);
        JetAuditProgram.RequireGlMapping(glMapping is not null);

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);

        // 能使用的欄位和第五步、前端停用說明讀同一份欄位目錄的答案。
        var availableGlFields = JetFieldCatalog.AvailableGlFields(glMapping.Mapping, glMapping.GlOptions);
        var hasHolidays = await repositories.Calendar.CountAsync(projectId, CalendarDayType.Holiday, cancellationToken) > 0;
        var prepStartDate = document.LastAccountingPeriodDate;

        var accountMappingState = await repositories.AccountMappings.FindStateAsync(projectId, cancellationToken);
        var hasAuthorizedPreparers = await repositories.AuthorizedPreparers.CountAsync(projectId, cancellationToken) > 0;
        var categorySnapshot = await repositories.AccountCategories.ReadAsync(projectId, cancellationToken);

        var resultId = Guid.NewGuid().ToString("N");
        var generatedUtc = DateTimeOffset.UtcNow;
        var plan = JetAuditProgram.Plan(
            new PrescreenRequest(
                projectId,
                HasGlMapping: true,
                document.PeriodStart,
                document.PeriodEnd,
                document.MoneyScale,
                document.SampleSeed.GetValueOrDefault(),
                resultId,
                generatedUtc,
                PrepStartDate: prepStartDate,
                AvailableGlFields: availableGlFields,
                HasHolidays: hasHolidays,
                HasAccountMapping: accountMappingState is not null,
                HasRevenue: accountMappingState?.HasRevenue ?? false,
                HasCounterpart: accountMappingState?.HasCounterpart ?? false,
                HasAuthorizedPreparers: hasAuthorizedPreparers,
                NonWorkingDays: document.NonWorkingDays,
                CategoryLabels: AccountCategoryLabels.From(categorySnapshot))
            {
                // 照 IDEA 版 R4 由 GL 的借方平均決定 0 的個數。
                // 確認 GL 配對時控制總數和配對同一個交易寫下，讀不到就是資料庫不完整，明確丟出。
                ZerosThreshold = TrailingZeroThreshold.FromCommittedGl(glMapping, document.MoneyScale)
                    ?? throw new InvalidOperationException("已確認的 GL 配對讀不到控制總數，無法決定尾數 0 的個數。")
            });
        SupportOperationStage.Enter("prescreen_facts");
        var facts = await repositories.PrescreenFacts.ExecuteAsync(plan, cancellationToken);
        SupportOperationStage.Enter("summary");
        var prescreen = JetAuditProgram.Finalize(plan, facts);
        var positioning = JetAuditProgram.RenderPrescreenPositioning();
        var runManifest = prescreen.Manifest;
        var result = prescreen.Data;
        var scale = document.MoneyScale;

        var prepPeriodApproval = Verdict(runManifest, "prep_period_approval");
        var unexpectedAccountPair = Verdict(runManifest, "unexpected_account_pair");
        var preparerSummary = Verdict(runManifest, "preparer_summary");
        var weekendPosting = Verdict(runManifest, "weekend_posting");
        var weekendApproval = Verdict(runManifest, "weekend_approval");
        var holidayPosting = Verdict(runManifest, "holiday_posting");
        var holidayApproval = Verdict(runManifest, "holiday_approval");
        var nonAuthorizedPreparer = Verdict(runManifest, "non_authorized_preparer");

        var dto = new
        {
            prepPeriodApproval = RuleStatus(result.PrepPeriodApprovalCount, prepPeriodApproval),
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
            preparerSummary = new
            {
                status = preparerSummary.Status!,
                naReason = preparerSummary.NaReason,
                // preparers 最多 50 列；人數用查核期間的完整人數（空白人員算一組），不適用時為 null，不以 0 冒充。
                totalPreparerCount = preparerSummary.NaReason is null ? facts.TotalPreparerCount : (long?)null,
                preparers = result.Preparers.Select(c => new
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
                    accountNumber = a.AccountNumber,
                    accountName = a.AccountName,
                    entryCount = a.EntryCount,
                    debitTotal = ToDisplay(a.DebitTotalScaled, scale),
                    creditTotal = ToDisplay(a.CreditTotalScaled, scale)
                }).ToArray()
            },
            weekendActivity = new
            {
                status = CombinedStatus(weekendPosting, weekendApproval),
                naReason = prescreen.WeekendActivity.NaReason,
                approvalNaReason = prescreen.WeekendActivity.ApprovalNaReason,
                postingCount = result.WeekendPostingCount,
                approvalCount = result.WeekendApprovalCount
            },
            holidayActivity = new
            {
                status = CombinedStatus(holidayPosting, holidayApproval),
                naReason = prescreen.HolidayActivity.NaReason,
                approvalNaReason = prescreen.HolidayActivity.ApprovalNaReason,
                postingCount = result.HolidayPostingCount,
                approvalCount = result.HolidayApprovalCount
            },
            blankDescription = new
            {
                status = Verdict(runManifest, "blank_description").Status!,
                count = result.BlankDescriptionCount
            },
            nonAuthorizedPreparer = RuleStatus(result.NonAuthorizedPreparerCount, nonAuthorizedPreparer),
            concentration = ConcentrationWire(prescreen.Concentration),
            rulePeriod = RulePeriodWire(prescreen.RulePeriod),
            positioning = PositioningWire(positioning),
            resultRef = new { resultId, generatedUtc, logicVersion = RuleLogicVersions.Prescreen }
        };

        var summaryJson = JsonSerializer.Serialize(dto, JetJsonStorage.Options);
        SupportOperationStage.Enter("run_save");
        await repositories.Results.SaveAsync(
            projectId,
            new ResultRecord(resultId, ResultKind.Prescreen, generatedUtc, summaryJson),
            CancellationToken.None);

        SupportOperationStage.Enter("after_commit");
        await MappingCommitShared.AdvanceStepAsync(
            projectStore,
            document,
            WorkflowStep,
            CancellationToken.None, logger);

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
            accountNumber = account.AccountNumber,
            accountName = account.AccountName,
            entryCount = account.EntryCount
        }).ToArray(),
        distinctAccountCount = concentration.DistinctAccountCount
    };

    /// <summary>
    /// 預篩選 row-tag 的全期命中分布。AuditCore 已完成適用性、分母與占比的判定；
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
