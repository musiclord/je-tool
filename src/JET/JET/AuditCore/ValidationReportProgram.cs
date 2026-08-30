namespace JET.AuditCore;

/// <summary>
/// Validation workbook planning 的 bounded input。各 count 來自已驗證為 current 的
/// validation run；不攜帶 wire JSON、GL／TB rows 或 OpenXML layout。
/// </summary>
internal sealed record ValidationReportRequest(
    string ProjectId,
    string PeriodStart,
    string PeriodEnd,
    long CompletenessDiffAccountCount,
    long NullAccountCount,
    long NullDocumentCount,
    long NullDescriptionCount,
    long OutOfRangeDateCount);

/// <summary>
/// Workbook detail 的 provider-neutral semantic identity。Legacy 工作表名稱、欄位與
/// 落點仍由 Infrastructure writer 持有，AuditCore 只裁定是否讀取／輸出明細。
/// </summary>
internal enum ValidationReportDetailKind
{
    NullAccount,
    NullDocument,
    NullDescription,
    OutOfRangeApprovalDate,
    CompletenessAccount,
    UnbalancedGlEntry
}

/// <summary>
/// <see cref="Omit"/> 表示零筆而不建表；<see cref="Emit"/> 表示可讀取並輸出；
/// <see cref="SummaryOnly"/> 表示保留摘要 count、Excel 路徑不得讀取明細 repository。
/// </summary>
internal enum ValidationReportDetailDisposition
{
    Omit,
    Emit,
    SummaryOnly
}

/// <summary>一個 validation detail family 的 finalized workbook decision。</summary>
internal sealed record ValidationReportDetailPlan(
    ValidationReportDetailKind Kind,
    long Count,
    ValidationReportDetailDisposition Disposition)
{
    internal bool Emit => Disposition == ValidationReportDetailDisposition.Emit;

    internal bool IsSummaryOnly =>
        Disposition == ValidationReportDetailDisposition.SummaryOnly;
}

/// <summary>
/// Infrastructure planning port 回傳的 raw fact。它是借貸不平傳票回接本期 GL 後的
/// 原始明細列數，不是 distinct voucher count，也不進入 public validation schema。
/// </summary>
internal sealed record ValidationReportPlanningFacts(long UnbalancedDetailRowCount);

/// <summary>
/// Validation workbook 的 in-memory plan。Plan 先綁定 request 與 program node；
/// Finalize 才依 provider raw detail count 產生每一明細 family 的 ordered decision。
/// </summary>
internal sealed record ValidationReportPlan(
    ValidationReportRequest Request,
    ProgramNode Node,
    IReadOnlyList<ValidationReportDetailPlan> Details,
    bool EmitCompletenessExplanation,
    bool IsFinalized)
{
    internal ValidationReportDetailPlan RequireDetail(ValidationReportDetailKind kind)
    {
        if (!IsFinalized)
        {
            throw new InvalidOperationException("ValidationReportPlan 尚未 Finalize。");
        }

        return Details.Single(detail => detail.Kind == kind);
    }
}

/// <summary>
/// Validation workbook 的 typed planning facts port。實作只執行 parameterized、
/// set-based raw detail count；明細分頁、raw-row 回取與 OpenXML 不屬於此 port。
/// </summary>
internal interface IValidationReportPlanningFactsPort
{
    Task<ValidationReportPlanningFacts> ExecuteAsync(
        ValidationReportPlan plan,
        CancellationToken cancellationToken);
}

public static partial class JetAuditProgram
{
    private const long ValidationNullDetailMaximumRows = 10_000;
    private const long ValidationUnbalancedDetailExclusiveMaximumRows = 10_000;

    private static readonly IReadOnlyList<ValidationReportDetailPlan> NoValidationReportDetails =
        Array.AsReadOnly(Array.Empty<ValidationReportDetailPlan>());

    /// <summary>
    /// 綁定已保存的 validation counts；不在 Plan 階段查 provider 或重算 public result。
    /// </summary>
    internal static ValidationReportPlan Plan(ValidationReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PeriodStart);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PeriodEnd);

        return new ValidationReportPlan(
            request,
            ProgramGraph.Current.RequireNode(ValidationArtifactsAction),
            NoValidationReportDetails,
            EmitCompletenessExplanation: false,
            IsFinalized: false);
    }

    /// <summary>由 typed port 取得 V6 workbook gate 所需的單一 raw count。</summary>
    internal static Task<ValidationReportPlanningFacts> ExecuteAsync(
        ValidationReportPlan plan,
        IValidationReportPlanningFactsPort factsPort,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(factsPort);
        return factsPort.ExecuteAsync(plan, cancellationToken);
    }

    /// <summary>
    /// 依保存 count 與 provider-neutral raw detail count finalize workbook policy。
    /// 四個 null categories 為 1–10,000 emit、超量 summary-only；
    /// unbalanced GL raw rows 為 1–9,999 emit、10,000 起 summary-only。
    /// </summary>
    internal static ValidationReportPlan Finalize(
        ValidationReportPlan plan,
        ValidationReportPlanningFacts facts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts);

        if (plan.IsFinalized)
        {
            throw new InvalidOperationException("ValidationReportPlan 已 Finalize。");
        }

        var request = plan.Request;
        EnsureNonNegative(request.CompletenessDiffAccountCount, nameof(request.CompletenessDiffAccountCount));
        EnsureNonNegative(request.NullAccountCount, nameof(request.NullAccountCount));
        EnsureNonNegative(request.NullDocumentCount, nameof(request.NullDocumentCount));
        EnsureNonNegative(request.NullDescriptionCount, nameof(request.NullDescriptionCount));
        EnsureNonNegative(request.OutOfRangeDateCount, nameof(request.OutOfRangeDateCount));
        EnsureNonNegative(facts.UnbalancedDetailRowCount, nameof(facts.UnbalancedDetailRowCount));

        var details = new[]
        {
            InclusiveDetail(ValidationReportDetailKind.NullAccount, request.NullAccountCount),
            InclusiveDetail(ValidationReportDetailKind.NullDocument, request.NullDocumentCount),
            InclusiveDetail(ValidationReportDetailKind.NullDescription, request.NullDescriptionCount),
            InclusiveDetail(
                ValidationReportDetailKind.OutOfRangeApprovalDate,
                request.OutOfRangeDateCount),
            new ValidationReportDetailPlan(
                ValidationReportDetailKind.CompletenessAccount,
                request.CompletenessDiffAccountCount,
                ValidationReportDetailDisposition.Emit),
            ExclusiveDetail(
                ValidationReportDetailKind.UnbalancedGlEntry,
                facts.UnbalancedDetailRowCount)
        };

        return plan with
        {
            Details = Array.AsReadOnly(details),
            EmitCompletenessExplanation = request.CompletenessDiffAccountCount > 0,
            IsFinalized = true
        };
    }

    /// <summary>供 review 確認 emit／summary-only 決策，不參與 runtime dispatch。</summary>
    internal static string Explain(ValidationReportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsFinalized)
        {
            throw new InvalidOperationException("ValidationReportPlan 尚未 Finalize。");
        }

        var emitted = plan.Details.Count(detail => detail.Emit);
        var summaryOnly = plan.Details.Count(detail => detail.IsSummaryOnly);
        return $"{ValidationArtifactsAction}：明細輸出={emitted}；僅摘要={summaryOnly}。";
    }

    private static ValidationReportDetailPlan InclusiveDetail(
        ValidationReportDetailKind kind,
        long count) =>
        new(kind, count, count switch
        {
            0 => ValidationReportDetailDisposition.Omit,
            <= ValidationNullDetailMaximumRows => ValidationReportDetailDisposition.Emit,
            _ => ValidationReportDetailDisposition.SummaryOnly
        });

    private static ValidationReportDetailPlan ExclusiveDetail(
        ValidationReportDetailKind kind,
        long count) =>
        new(kind, count, count switch
        {
            0 => ValidationReportDetailDisposition.Omit,
            < ValidationUnbalancedDetailExclusiveMaximumRows => ValidationReportDetailDisposition.Emit,
            _ => ValidationReportDetailDisposition.SummaryOnly
        });

    private static void EnsureNonNegative(long count, string name)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(name, count, "Validation report count 不得為負數。");
        }
    }
}
