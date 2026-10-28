namespace JET.Domain;

/// <summary>六份正式報告共用、且不含完整資料列的案件 metadata。</summary>
public sealed record ReportDocumentContext(
    string ProjectId,
    string CompanyName,
    string PeriodStart,
    string PeriodEnd,
    string? LastPeriodStart,
    int MoneyScale);

public sealed record ValidationReportContext(
    ReportDocumentContext Project,
    string RunId,
    DateTimeOffset GeneratedUtc,
    string SummaryJson);

/// <summary>
/// Validation report 實際需要的有界摘要值。此型別不攜帶明細列、wire JSON
/// 或 provider 資訊，且只在 Application 與 Infrastructure 的內部縫流動。
/// <see cref="OutOfRangeDateCount"/> 為 null 表示驗證時傳票核准日不可用，報告照這個結果寫 N/A。
/// </summary>
internal sealed record ValidationReportProjection(
    decimal Net,
    decimal TotalDebit,
    decimal TotalCredit,
    long GlRowCount,
    long CompletenessDiffAccountCount,
    long UnbalancedDocumentCount,
    long NullAccountCount,
    long NullDocumentCount,
    long NullDescriptionCount,
    long? OutOfRangeDateCount,
    long SourceQualityFindingCount);

public sealed record InfReportContext(
    ReportDocumentContext Project,
    string RunId,
    DateTimeOffset GeneratedUtc);

public sealed record PrescreenReportContext(
    ReportDocumentContext Project,
    string RunId,
    DateTimeOffset GeneratedUtc,
    string SummaryJson);

/// <summary>
/// Prescreen report 內單一規則摘要的 provider-neutral、bounded 投影。
/// <paramref name="ItemCount"/> 只供 R5／R6 彙總列顯示；命中規則的
/// voucher／row counts 仍由既有 set-based report repository 查詢。
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
    PrescreenReportRuleProjection PostPeriodApproval,
    PrescreenReportRuleProjection SuspiciousKeywords,
    PrescreenReportRuleProjection UnexpectedAccountPair,
    PrescreenReportRuleProjection TrailingZeros,
    PrescreenReportRuleProjection CreatorSummary,
    PrescreenReportRuleProjection RareAccounts,
    PrescreenReportRuleProjection BlankDescription);

public sealed record CriteriaSelectionReportContext(
    ReportDocumentContext Project,
    string Revision,
    DateTimeOffset GeneratedUtc,
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
        Action<WorkpaperProgress>? progress = null);
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
        Action<WorkpaperProgress>? progress = null);
}
