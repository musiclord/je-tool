using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;

namespace JET.Tests.Application;

/// <summary>
/// 直接呼叫報告寫出器的測試共用的材料。
/// 測試和正式 handler 一樣，先把保存的摘要算成計畫、備好工作簿中繼資料，再呼叫唯一的正式入口。
/// </summary>
internal static class ReportWriterTestSupport
{
    /// <summary>和正式 handler 相同的驗證報告計畫：規劃後用摘要裡的借貸不平傳票數當作明細列數 Finalize。</summary>
    internal static ValidationReportPlan ValidationPlan(
        ValidationReportContext context,
        ValidationReportProjection projection) =>
        JetAuditProgram.Finalize(
            JetAuditProgram.Plan(new ValidationReportRequest(
                context.Project.ProjectId,
                context.Project.PeriodStart,
                context.Project.PeriodEnd,
                projection.CompletenessDiffAccountCount,
                projection.BlankAccountNumberCount,
                projection.BlankVoucherNumberCount,
                projection.BlankDescriptionCount,
                projection.ApprovalDateNotInPeriodCount)),
            new ValidationReportPlanningFacts(projection.UnbalancedVoucherCount));

    /// <summary>和正式 handler 相同的預篩選報告計畫：每個適用的明細都用摘要裡的項目數當作傳票數與列數。</summary>
    internal static PrescreenReportPlan PrescreenPlan(
        PrescreenReportContext context,
        PrescreenReportProjection projection)
    {
        var unfinalized = JetAuditProgram.Plan(new PrescreenReportRequest(
            context.Project.ProjectId,
            new FilterConditionContext(
                context.Project.MoneyScale,
                context.Project.PrepStartDate,
                context.Project.PeriodStart,
                context.Project.PeriodEnd),
            projection.PrepPeriodApproval.NaReason,
            projection.SuspiciousKeywords.NaReason,
            projection.UnexpectedAccountPair.NaReason,
            projection.TrailingZeros.NaReason,
            projection.BlankDescription.NaReason));
        var facts = unfinalized.Details
            .Where(detail => detail.IsApplicable)
            .ToDictionary(
                detail => detail.Kind,
                detail =>
                {
                    var count = ProjectionFor(projection, detail.Kind).ItemCount;
                    return new PrescreenHitCounts(count, count);
                });
        return JetAuditProgram.Finalize(unfinalized, new PrescreenReportPlanningFacts(facts));
    }

    /// <summary>通過中繼資料檢查的最小工作簿中繼資料；寫出器讀欄位配對時仍看資料庫，這份只決定隱藏的中繼資料工作表。</summary>
    internal static ReportWorkbookMetadata SyntheticMetadata(
        string periodStart = "2025-01-01",
        string periodEnd = "2025-12-31") =>
        new(
            periodStart,
            periodEnd,
            CategoryRevision: 1,
            new CommittedMapping(
                DatasetKind.Gl,
                new Dictionary<string, string>(StringComparer.Ordinal),
                GlAmountModeNames.Signed,
                "synthetic-gl",
                DateTimeOffset.UnixEpoch),
            TbMapping: null);

    /// <summary>發布流程使用的完整合成預篩選摘要：全部規則都有狀態，沒有命中。</summary>
    // 摘要要通過形狀檢查，所以夾具是正式回應的形狀，不只列報告會讀的欄位。
    internal static string PrescreenSummaryJson() => JsonSerializer.Serialize(
        new
        {
            prepPeriodApproval = new { status = "na", naReason = "尚未配對核准日", count = 0L },
            suspiciousKeywords = new { status = "V", count = 0L },
            unexpectedAccountPair = new { status = "na", naReason = "尚未完成科目配對", count = 0L },
            // 保存的預篩選結果要記下那次用的 0 的個數，報告讀這裡；最小摘要帶 6。
            trailingZeros = new { status = "V", count = 0L, zerosThreshold = 6 },
            preparerSummary = new { status = "V", naReason = (string?)null, totalPreparerCount = 0L, preparers = Array.Empty<object>() },
            rareAccounts = new { status = "V", distinctAccountCount = 0L, accounts = Array.Empty<object>() },
            blankDescription = new { status = "V", count = 0L },
            weekendActivity = new { status = "V", naReason = (string?)null, approvalNaReason = (string?)null, postingCount = 0L, approvalCount = 0L },
            holidayActivity = new { status = "V", naReason = (string?)null, approvalNaReason = (string?)null, postingCount = 0L, approvalCount = 0L },
            nonAuthorizedPreparer = new { status = "na", naReason = "尚未提供授權編製人員清單", count = 0L },
            concentration = new
            {
                status = "V", naReason = (string?)null,
                preparers = new { top = Array.Empty<object>(), othersEntryCount = 0L, totalPreparerCount = 0L, totalEntryCount = 0L, top5SharePct = (decimal?)null },
                rareAccounts = Array.Empty<object>(), distinctAccountCount = 0L
            },
            rulePeriod = new { population = 0L, rules = Array.Empty<object>() },
            positioning = new
            {
                aggregateGuidance = "查看編製人員與較少使用科目的分錄筆數及金額。",
                signalGuidance = "符合條件的分錄供初步查核；請在「進階條件篩選」設定本案的測試範圍。",
                reportGuidance = "可在此產生預篩選報告，或在匯出底稿時一併產生。不產生也可繼續篩選與匯出底稿。",
                overviewGuidance = "預篩選呈現查核期間分錄的分布與符合條件的分錄，是否需進一步查核由審計員判斷。",
                exportDefaultGuidance = "預設一併產出預篩選報告；取消勾選不影響其他報告及底稿內容。",
                exportPendingRunGuidance = "尚無預篩選結果，匯出時會先執行預篩選；資料量大時需要數分鐘到十餘分鐘。"
            },
            resultRef = new { resultId = "prescreen-current", generatedUtc = "2026-09-23T00:00:00+00:00", logicVersion = RuleLogicVersions.Prescreen }
        },
        JetJsonStorage.Options);

    private static PrescreenReportRuleProjection ProjectionFor(
        PrescreenReportProjection projection,
        PrescreenReportDetailKind kind) => kind switch
    {
        PrescreenReportDetailKind.PrepPeriodApproval => projection.PrepPeriodApproval,
        PrescreenReportDetailKind.SuspiciousKeywords => projection.SuspiciousKeywords,
        PrescreenReportDetailKind.UnexpectedAccountPair => projection.UnexpectedAccountPair,
        PrescreenReportDetailKind.TrailingZeros => projection.TrailingZeros,
        PrescreenReportDetailKind.BlankDescription => projection.BlankDescription,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的 Pre-screening detail kind。")
    };

    /// <summary>每個適用的明細都回零命中，給只在意發布流程、不在意內容的 handler 測試用。</summary>
    internal sealed class ZeroPrescreenPlanningFactsPort : IPrescreenReportPlanningFactsPort
    {
        public Task<PrescreenReportPlanningFacts> ExecuteAsync(
            PrescreenReportPlan plan,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PrescreenReportPlanningFacts(
                plan.Details
                    .Where(detail => detail.IsApplicable)
                    .ToDictionary(detail => detail.Kind, _ => new PrescreenHitCounts(0, 0))));
    }

    /// <summary>正式組裝一定帶入欄位配對 store；這裡提供已確認、沒有欄位的 GL 配對，讓報表中繼資料可以建立。</summary>
    internal sealed class FixedMetadataMappingStore : IMappingStateStore
    {
        public Task SaveAsync(
            string projectId,
            CommittedMapping mapping,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CommittedMapping?> FindAsync(
            string projectId,
            DatasetKind kind,
            CancellationToken cancellationToken) =>
            Task.FromResult<CommittedMapping?>(kind == DatasetKind.Gl
                ? new CommittedMapping(
                    DatasetKind.Gl,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    GlAmountModeNames.Signed,
                    "synthetic-gl",
                    DateTimeOffset.UnixEpoch)
                : null);
    }

    /// <summary>只回內建科目分類的替身。</summary>
    internal sealed class BuiltInAccountCategoryStore : IAccountCategoryStore
    {
        public Task<AccountCategorySnapshot> ReadAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            Task.FromResult(AccountCategoryCatalog.BuiltInSnapshot);

        public Task<AccountCategorySnapshot> SaveAsync(
            string projectId,
            int expectedRevision,
            IReadOnlyList<AccountCategory> categories,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
