using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// Pre-screening workbook planning 的 bounded input。N/A 只由已保存 summary 的
/// typed reason 決定；provider count、worksheet 名稱與 OpenXML layout 不進入 request。
/// </summary>
internal sealed record PrescreenReportRequest(
    string ProjectId,
    FilterRuleContext RuleContext,
    string? PostPeriodApprovalNaReason,
    string? SuspiciousKeywordsNaReason,
    string? UnexpectedAccountPairNaReason,
    string? TrailingZerosNaReason,
    string? BlankDescriptionNaReason);

/// <summary>五個具有 raw GL 明細的 legacy Pre-screening semantic family。</summary>
internal enum PrescreenReportDetailKind
{
    PostPeriodApproval,
    SuspiciousKeywords,
    UnexpectedAccountPair,
    TrailingZeros,
    BlankDescription
}

/// <summary>
/// Pending 只存在於 Plan 與 Execute 間；NotApplicable 不讀 count／detail；
/// Omit 是已執行零筆；Emit 是 1–9,999 筆；SummaryOnly 是 10,000 筆起。
/// </summary>
internal enum PrescreenReportDetailDisposition
{
    Pending,
    NotApplicable,
    Omit,
    Emit,
    SummaryOnly
}

/// <summary>單一 Pre-screening detail family 的 finalized workbook decision。</summary>
internal sealed record PrescreenReportDetailPlan(
    PrescreenReportDetailKind Kind,
    bool IsApplicable,
    string? NaReason,
    PrescreenHitCounts? Counts,
    PrescreenReportDetailDisposition Disposition)
{
    internal bool Emit => Disposition == PrescreenReportDetailDisposition.Emit;

    internal bool IsSummaryOnly =>
        Disposition == PrescreenReportDetailDisposition.SummaryOnly;
}

/// <summary>
/// Typed planning port 回傳的 set-based counts。Key 只使用 AuditCore semantic kind，
/// 不讓 wire rule key 成為 lifecycle 契約。
/// </summary>
internal sealed record PrescreenReportPlanningFacts(
    IReadOnlyDictionary<PrescreenReportDetailKind, PrescreenHitCounts> Counts);

/// <summary>
/// Pre-screening workbook plan。Plan 綁定 applicability；Finalize 才依 row count
/// 產生 ordered detail decision。
/// </summary>
internal sealed record PrescreenReportPlan(
    PrescreenReportRequest Request,
    IReadOnlyList<PrescreenReportDetailPlan> Details,
    bool IsFinalized)
{
    internal PrescreenReportDetailPlan RequireDetail(PrescreenReportDetailKind kind)
    {
        if (!IsFinalized)
        {
            throw new InvalidOperationException("PrescreenReportPlan 尚未 Finalize。");
        }

        return Details.Single(detail => detail.Kind == kind);
    }
}

/// <summary>
/// Pre-screening workbook typed planning facts port。實作只取得 distinct voucher
/// count 與 GL row count；明細 page 與 raw-row 回取不屬於此 port。
/// </summary>
internal interface IPrescreenReportPlanningFactsPort
{
    Task<PrescreenReportPlanningFacts> ExecuteAsync(
        PrescreenReportPlan plan,
        CancellationToken cancellationToken);
}

public static partial class JetAuditProgram
{
    private const long PrescreenDetailExclusiveMaximumRows = 10_000;

    /// <summary>綁定保存 summary 的 N/A 狀態，不在 Plan 階段查 provider。</summary>
    internal static PrescreenReportPlan Plan(PrescreenReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectId);
        ArgumentNullException.ThrowIfNull(request.RuleContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RuleContext.PeriodStart);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RuleContext.PeriodEnd);

        var details = new[]
        {
            PendingPrescreenDetail(
                PrescreenReportDetailKind.PostPeriodApproval,
                request.PostPeriodApprovalNaReason),
            PendingPrescreenDetail(
                PrescreenReportDetailKind.SuspiciousKeywords,
                request.SuspiciousKeywordsNaReason),
            PendingPrescreenDetail(
                PrescreenReportDetailKind.UnexpectedAccountPair,
                request.UnexpectedAccountPairNaReason),
            PendingPrescreenDetail(
                PrescreenReportDetailKind.TrailingZeros,
                request.TrailingZerosNaReason),
            PendingPrescreenDetail(
                PrescreenReportDetailKind.BlankDescription,
                request.BlankDescriptionNaReason)
        };

        return new PrescreenReportPlan(
            request,
            Array.AsReadOnly(details),
            IsFinalized: false);
    }

    /// <summary>
    /// 依 row count finalize workbook policy：零筆 omit、1–9,999 emit、
    /// 10,000 起 summary-only；N/A family 不要求也不接受 provider count。
    /// </summary>
    internal static PrescreenReportPlan Finalize(
        PrescreenReportPlan plan,
        PrescreenReportPlanningFacts facts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(facts.Counts);
        if (plan.IsFinalized)
        {
            throw new InvalidOperationException("PrescreenReportPlan 已 Finalize。");
        }

        var applicable = plan.Details
            .Where(detail => detail.IsApplicable)
            .Select(detail => detail.Kind)
            .ToHashSet();
        var unexpected = facts.Counts.Keys.Where(kind => !applicable.Contains(kind)).ToArray();
        if (unexpected.Length > 0)
        {
            throw new InvalidOperationException(
                $"Pre-screening planning facts 含 N/A family：{string.Join(", ", unexpected)}。");
        }

        var details = plan.Details.Select(detail =>
        {
            if (!detail.IsApplicable)
            {
                return detail with
                {
                    Counts = null,
                    Disposition = PrescreenReportDetailDisposition.NotApplicable
                };
            }

            if (!facts.Counts.TryGetValue(detail.Kind, out var counts))
            {
                throw new InvalidOperationException(
                    $"Pre-screening planning facts 缺少 '{detail.Kind}'。");
            }
            EnsureNonNegativePrescreenCount(
                counts.VoucherHitCount,
                $"{detail.Kind}.VoucherHitCount");
            EnsureNonNegativePrescreenCount(
                counts.RowHitCount,
                $"{detail.Kind}.RowHitCount");

            return detail with
            {
                Counts = counts,
                Disposition = counts.RowHitCount switch
                {
                    0 => PrescreenReportDetailDisposition.Omit,
                    < PrescreenDetailExclusiveMaximumRows =>
                        PrescreenReportDetailDisposition.Emit,
                    _ => PrescreenReportDetailDisposition.SummaryOnly
                }
            };
        }).ToArray();

        return plan with
        {
            Details = Array.AsReadOnly(details),
            IsFinalized = true
        };
    }

    private static PrescreenReportDetailPlan PendingPrescreenDetail(
        PrescreenReportDetailKind kind,
        string? naReason)
    {
        var applicable = string.IsNullOrWhiteSpace(naReason);
        return new PrescreenReportDetailPlan(
            kind,
            applicable,
            applicable ? null : naReason,
            Counts: null,
            applicable
                ? PrescreenReportDetailDisposition.Pending
                : PrescreenReportDetailDisposition.NotApplicable);
    }

    private static void EnsureNonNegativePrescreenCount(long count, string name)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(
                name,
                count,
                "Pre-screening report count 不得為負數。");
        }
    }
}
