using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;

namespace JET.Tests.Application;

/// <summary>
/// 直接呼叫報告寫出器的測試共用的材料。2026-10-22 技術債一輪刪掉只給測試用的相容入口後，
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
                projection.NullAccountCount,
                projection.NullDocumentCount,
                projection.NullDescriptionCount,
                projection.OutOfRangeDateCount)),
            new ValidationReportPlanningFacts(projection.UnbalancedDocumentCount));

    /// <summary>和正式 handler 相同的預篩選報告計畫：每個適用的明細都用摘要裡的項目數當作傳票數與列數。</summary>
    internal static PrescreenReportPlan PrescreenPlan(
        PrescreenReportContext context,
        PrescreenReportProjection projection)
    {
        var unfinalized = JetAuditProgram.Plan(new PrescreenReportRequest(
            context.Project.ProjectId,
            new FilterRuleContext(
                context.Project.MoneyScale,
                context.Project.LastPeriodStart,
                context.Project.PeriodStart,
                context.Project.PeriodEnd),
            projection.PostPeriodApproval.NaReason,
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
            TaxonomyRevision: 1,
            new CommittedMapping(
                DatasetKind.Gl,
                new Dictionary<string, string>(StringComparer.Ordinal),
                GlAmountModeNames.Signed,
                "synthetic-gl",
                DateTimeOffset.UnixEpoch),
            TbMapping: null);

    /// <summary>可以被解析成報告投影的最小預篩選摘要：全部規則都有狀態，沒有命中。</summary>
    internal static string PrescreenSummaryJson() => JsonSerializer.Serialize(
        new
        {
            postPeriodApproval = new { status = "na", naReason = "尚未配對核准日", count = 0L },
            suspiciousKeywords = new { status = "V", count = 0L },
            unexpectedAccountPair = new { status = "na", naReason = "尚未完成科目配對", count = 0L },
            trailingZeros = new { status = "V", count = 0L },
            creatorSummary = new { status = "V", creators = Array.Empty<object>() },
            rareAccounts = new { status = "V", distinctAccountCount = 0L },
            blankDescription = new { status = "V", count = 0L },
            resultRef = new { logicVersion = RuleLogicVersions.Prescreen }
        },
        JetJsonStorage.Options);

    private static PrescreenReportRuleProjection ProjectionFor(
        PrescreenReportProjection projection,
        PrescreenReportDetailKind kind) => kind switch
    {
        PrescreenReportDetailKind.PostPeriodApproval => projection.PostPeriodApproval,
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
    internal sealed class BuiltInAccountTaxonomyStore : IAccountTaxonomyStore
    {
        public Task<AccountTaxonomySnapshot> ReadAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            Task.FromResult(AccountTaxonomyCatalog.BuiltInSnapshot);

        public Task<AccountTaxonomySnapshot> SaveAsync(
            string projectId,
            int expectedRevision,
            IReadOnlyList<AccountTaxonomyCategory> categories,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
