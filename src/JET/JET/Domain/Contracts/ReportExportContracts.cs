namespace JET.Domain;

/// <summary>六份正式報告共用、且不含完整資料列的案件 metadata。</summary>
public sealed record ReportDocumentContext(
    string ProjectId,
    string CompanyName,
    string PeriodStart,
    string PeriodEnd,
    string? PrepStartDate,
    int MoneyScale);

/// <param name="PreparedDate">報告的編製日期，寫按下匯出那一天的本機日期，不寫執行資料驗證的時間。</param>
public sealed record ValidationReportContext(
    ReportDocumentContext Project,
    string ResultId,
    DateOnly PreparedDate,
    string SummaryJson);

/// <summary>
/// Validation report 實際需要的有界摘要值。此型別不攜帶明細列、wire JSON
/// 或 provider 資訊，且只在 Application 與 Infrastructure 的內部縫流動。
/// <see cref="ApprovalDateNotInPeriodCount"/> 為 null 表示驗證時傳票核准日不可用，報告照這個結果寫 N/A。
/// <see cref="CompletenessNaNote"/> 有值表示驗證時無法執行 GL 與 TB 逐科目比對，報告寫 N/A 並附這段註記。
/// </summary>
internal sealed record ValidationReportProjection(
    decimal Net,
    decimal TotalDebit,
    decimal TotalCredit,
    long GlRowCount,
    long CompletenessDiffAccountCount,
    long UnbalancedVoucherCount,
    long BlankAccountNumberCount,
    long BlankVoucherNumberCount,
    long BlankDescriptionCount,
    long? ApprovalDateNotInPeriodCount,
    long SourceQualityFindingCount,
    string? CompletenessNaNote = null);

public sealed record InfReportContext(
    ReportDocumentContext Project,
    string ResultId,
    DateTimeOffset GeneratedUtc);

/// <param name="PreparedDate">報告的編製日期，寫按下匯出那一天的本機日期，不寫執行預篩選的時間。</param>
public sealed record PrescreenReportContext(
    ReportDocumentContext Project,
    string ResultId,
    DateOnly PreparedDate,
    string SummaryJson);

/// <summary>
/// Prescreen report 內單一規則摘要的 provider-neutral、bounded 投影。
/// <paramref name="ItemCount"/> 是預篩選結果記下的筆數或項目數。摘要頁照舊版原句不寫這個數字，
/// 報告寫出程式不讀它，報告測試用它組出計畫的筆數；命中規則的 voucher／row counts 由既有 set-based report repository 查詢。
/// </summary>
internal sealed record PrescreenReportRuleProjection(
    string Status,
    string? NaReason,
    long ItemCount = 0);

/// <summary>
/// Prescreen report 實際需要的七個具名摘要區段。此型別不攜帶 wire JSON、
/// 完整 GL 列集或 provider 資訊。
/// </summary>
internal sealed record PrescreenReportProjection(
    PrescreenReportRuleProjection PrepPeriodApproval,
    PrescreenReportRuleProjection SuspiciousKeywords,
    PrescreenReportRuleProjection UnexpectedAccountPair,
    PrescreenReportRuleProjection TrailingZeros,
    PrescreenReportRuleProjection PreparerSummary,
    PrescreenReportRuleProjection RareAccounts,
    PrescreenReportRuleProjection BlankDescription)
{
    /// <summary>
    /// 那次預篩選「分錄金額中有連續 0 的尾數」用的 0 的個數（回應的 zerosThreshold）。
    /// 報告的「設定連續0的位數為：N」與 [R4] 工作表的明細都用這個數字，和那次預篩選的筆數一致。
    /// </summary>
    public required int ZerosThreshold { get; init; }
}

/// <param name="PreparedDate">報告的編製日期，寫按下匯出那一天的本機日期。</param>
public sealed record CriteriaSelectionReportContext(
    ReportDocumentContext Project,
    string Revision,
    DateOnly PreparedDate,
    IReadOnlyList<SavedFilterScenario> Scenarios,
    IReadOnlyDictionary<int, string>? ScenarioConditionLogic = null,
    GlPopulationScope PopulationScope = GlPopulationScope.AuditPeriod,
    IReadOnlySet<int>? WholeVoucherSummaryPositions = null);

/// <summary>
/// INF 報告的唯一寫出入口。工作簿中繼資料與要一起輸出的攸關資料元素欄位由 Application 決定，
/// 寫出器只填固定範本。
/// </summary>
internal interface IInfReportWriter
{
    Task<ExportStats> WriteAsync(
        Stream output,
        InfReportContext context,
        ReportWorkbookMetadata workbookMetadata,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress = null);
}

/// <summary>
/// 條件篩選報告的唯一寫出入口。條件文字、工作簿中繼資料、攸關資料元素欄位與操作者都由 Application 準備好再傳入。
/// </summary>
internal interface ICriteriaSelectionReportWriter
{
    Task<ExportStats> WriteAsync(
        Stream output,
        CriteriaSelectionReportContext context,
        ReportWorkbookMetadata workbookMetadata,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress = null);
}
