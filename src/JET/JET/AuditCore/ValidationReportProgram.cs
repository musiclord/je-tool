namespace JET.AuditCore;

/// <summary>
/// 規劃驗證報告時的輸入。各項筆數取自已確認仍然有效的那一次資料驗證結果；
/// 不帶 action 的 JSON、GL 與 TB 的資料列，也不帶 OpenXML 版面。
/// <see cref="ApprovalDateNotInPeriodCount"/> 為 null 表示驗證時傳票核准日不可用，沒有明細可讀。
/// </summary>
internal sealed record ValidationReportRequest(
    string ProjectId,
    string PeriodStart,
    string PeriodEnd,
    long CompletenessDiffAccountCount,
    long BlankAccountNumberCount,
    long BlankVoucherNumberCount,
    long BlankDescriptionCount,
    long? ApprovalDateNotInPeriodCount);

/// <summary>
/// 驗證報告裡有哪幾類明細，不綁特定資料庫。舊版格式的工作表名稱、欄位與寫入位置仍由 Infrastructure 的寫出程式決定；
/// AuditCore 只判定每類明細要不要讀、要不要寫。
/// </summary>
internal enum ValidationReportDetailKind
{
    BlankAccountNumber,
    BlankVoucherNumber,
    BlankDescription,
    ApprovalDateNotInPeriod,
    CompletenessAccount,
    UnbalancedVoucherEntry
}

/// <summary>
/// <see cref="Omit"/> 表示零筆而不建表；<see cref="Emit"/> 表示可讀取並輸出；
/// <see cref="SummaryOnly"/> 表示只保留摘要上的筆數，寫 Excel 時不得讀明細。
/// </summary>
internal enum ValidationReportDetailDisposition
{
    Omit,
    Emit,
    SummaryOnly
}

/// <summary>驗證報告裡一類明細的決定：筆數，以及明細要怎麼寫。</summary>
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
/// 規劃驗證報告時 Infrastructure 查回的數字：借貸不平的傳票接回本期 GL 之後的明細列數。
/// 它不是不平傳票的張數，也不屬於對外的資料驗證結果。
/// </summary>
internal sealed record ValidationReportPlanningFacts(long UnbalancedDetailRowCount);

/// <summary>
/// 驗證報告的計畫，只留在記憶體裡。Plan 先記下輸入；
/// Finalize 才依資料庫查回的明細列數，照固定順序定出每類明細要怎麼寫。
/// </summary>
internal sealed record ValidationReportPlan(
    ValidationReportRequest Request,
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
/// 規劃驗證報告時查列數的介面，由 Infrastructure 實作。實作只用參數化的集合式 SQL 算明細列數；
/// 分頁讀明細、取回來源檔原始列與 OpenXML 都不在這個介面。
/// </summary>
internal interface IValidationReportPlanningFactsPort
{
    Task<ValidationReportPlanningFacts> ExecuteAsync(
        ValidationReportPlan plan,
        CancellationToken cancellationToken);
}

public static partial class JetAuditProgram
{
    private const long ValidationBlankDetailMaximumRows = 10_000;
    private const long ValidationUnbalancedDetailExclusiveMaximumRows = 10_000;

    private static readonly IReadOnlyList<ValidationReportDetailPlan> NoValidationReportDetails =
        Array.AsReadOnly(Array.Empty<ValidationReportDetailPlan>());

    /// <summary>
    /// 記下已保存的資料驗證筆數；這一步不查資料庫，也不重算資料驗證結果。
    /// </summary>
    internal static ValidationReportPlan Plan(ValidationReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PeriodStart);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PeriodEnd);

        return new ValidationReportPlan(
            request,
            NoValidationReportDetails,
            EmitCompletenessExplanation: false,
            IsFinalized: false);
    }

    /// <summary>
    /// 依已保存的筆數與查回的明細列數，決定每類明細要怎麼寫。
    /// 資料缺漏輔助檢視的四個類別是 1 到 10,000 筆寫明細，超過只寫摘要；
    /// 借貸不平的 GL 明細是 1 到 9,999 列寫明細，10,000 列以上只寫摘要。
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
        EnsureNonNegative(request.BlankAccountNumberCount, nameof(request.BlankAccountNumberCount));
        EnsureNonNegative(request.BlankVoucherNumberCount, nameof(request.BlankVoucherNumberCount));
        EnsureNonNegative(request.BlankDescriptionCount, nameof(request.BlankDescriptionCount));
        EnsureNonNegative(request.ApprovalDateNotInPeriodCount ?? 0L, nameof(request.ApprovalDateNotInPeriodCount));
        EnsureNonNegative(facts.UnbalancedDetailRowCount, nameof(facts.UnbalancedDetailRowCount));

        var details = new[]
        {
            InclusiveDetail(ValidationReportDetailKind.BlankAccountNumber, request.BlankAccountNumberCount),
            InclusiveDetail(ValidationReportDetailKind.BlankVoucherNumber, request.BlankVoucherNumberCount),
            InclusiveDetail(ValidationReportDetailKind.BlankDescription, request.BlankDescriptionCount),
            // 傳票核准日不可用時沒有檢查，不讀明細；摘要的 N/A 由寫出器依同一個結果寫。
            InclusiveDetail(
                ValidationReportDetailKind.ApprovalDateNotInPeriod,
                request.ApprovalDateNotInPeriodCount ?? 0L),
            new ValidationReportDetailPlan(
                ValidationReportDetailKind.CompletenessAccount,
                request.CompletenessDiffAccountCount,
                ValidationReportDetailDisposition.Emit),
            ExclusiveDetail(
                ValidationReportDetailKind.UnbalancedVoucherEntry,
                facts.UnbalancedDetailRowCount)
        };

        return plan with
        {
            Details = Array.AsReadOnly(details),
            EmitCompletenessExplanation = request.CompletenessDiffAccountCount > 0,
            IsFinalized = true
        };
    }

    private static ValidationReportDetailPlan InclusiveDetail(
        ValidationReportDetailKind kind,
        long count) =>
        new(kind, count, count switch
        {
            0 => ValidationReportDetailDisposition.Omit,
            <= ValidationBlankDetailMaximumRows => ValidationReportDetailDisposition.Emit,
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
