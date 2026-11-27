using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 規劃預篩選報告時的輸入。規則是不是 N/A，只看已保存的預篩選摘要裡的原因；
/// 資料庫查回的筆數、工作表名稱與 OpenXML 版面都不放進這份輸入。
/// </summary>
internal sealed record PrescreenReportRequest(
    string ProjectId,
    FilterConditionContext RuleContext,
    string? PrepPeriodApprovalNaReason,
    string? SuspiciousKeywordsNaReason,
    string? UnexpectedAccountPairNaReason,
    string? TrailingZerosNaReason,
    string? BlankDescriptionNaReason);

/// <summary>預篩選報告裡會附 GL 明細的五類規則；報告沿用舊版 JET 的格式。</summary>
internal enum PrescreenReportDetailKind
{
    PrepPeriodApproval,
    SuspiciousKeywords,
    UnexpectedAccountPair,
    TrailingZeros,
    BlankDescription
}

/// <summary>
/// 一類明細要怎麼寫進報告。Pending 只出現在 Plan 之後、還沒查回筆數之前；NotApplicable 不讀筆數也不讀明細；
/// Omit 是有執行但 0 筆；Emit 是 1 到 9,999 筆；SummaryOnly 是 10,000 筆以上。
/// </summary>
internal enum PrescreenReportDetailDisposition
{
    Pending,
    NotApplicable,
    Omit,
    Emit,
    SummaryOnly
}

/// <summary>預篩選報告裡一類明細的決定：是否適用、筆數，以及明細要怎麼寫。</summary>
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
/// Infrastructure 用集合式 SQL 查回的各類明細筆數。字典的鍵用 AuditCore 的 <see cref="PrescreenReportDetailKind"/>，
/// 不用 action 回應裡的規則鍵，這段流程就不依賴回應的字串。
/// </summary>
internal sealed record PrescreenReportPlanningFacts(
    IReadOnlyDictionary<PrescreenReportDetailKind, PrescreenHitCounts> Counts);

/// <summary>
/// 預篩選報告的計畫。Plan 先定下每類明細是否適用；Finalize 才依分錄筆數，照固定順序定出每類明細要怎麼寫。
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
/// 規劃預篩選報告時查筆數的介面，由 Infrastructure 實作。實作只查不重複的傳票張數與 GL 分錄筆數；
/// 分頁讀明細與取回來源檔原始列不在這個介面。
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

    /// <summary>依已保存摘要的 N/A 原因定下每類明細是否適用；這一步不查資料庫。</summary>
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
                PrescreenReportDetailKind.PrepPeriodApproval,
                request.PrepPeriodApprovalNaReason),
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
    /// 依分錄筆數決定每類明細要怎麼寫：0 筆不寫明細，1 到 9,999 筆寫明細，10,000 筆以上只寫摘要。
    /// N/A 的類別不需要筆數；Infrastructure 如果回傳了 N/A 類別的筆數，會當成錯誤丟出。
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
