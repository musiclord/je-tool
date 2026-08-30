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
    long OutOfRangeDateCount,
    long SourceQualityFindingCount,
    IReadOnlyList<SourceQualityFindingRow> SourceQualitySampleRows);

/// <summary>
/// Production validation export 的 internal typed seam。公開
/// <see cref="IValidationReportWriter"/> 與 <see cref="ValidationReportContext"/>
/// 保持原有 source、binary 與 record value semantics；Application 僅在 writer
/// 明確實作此縫時傳入已解析的有界 projection。
/// </summary>
internal interface ITypedValidationReportWriter
{
    Task<ExportStats> WriteTypedAsync(
        Stream output,
        ValidationReportContext context,
        ValidationReportProjection projection,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null);
}

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

/// <summary>
/// Production prescreen export 的 internal typed seam。公開
/// <see cref="IPrescreenReportWriter"/> 與 <see cref="PrescreenReportContext"/>
/// 保持既有 contract；Application 僅在 writer 明確實作此縫時傳入已解析的
/// 有界 projection。
/// </summary>
internal interface ITypedPrescreenReportWriter
{
    Task<ExportStats> WriteTypedAsync(
        Stream output,
        PrescreenReportContext context,
        PrescreenReportProjection projection,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null);
}

public sealed record CriteriaSelectionReportContext(
    ReportDocumentContext Project,
    string Revision,
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<SavedFilterScenario> Scenarios,
    IReadOnlyDictionary<int, string>? ScenarioConditionLogic = null,
    GlPopulationScope PopulationScope = GlPopulationScope.AuditPeriod,
    IReadOnlySet<int>? WholeVoucherSummaryPositions = null);

/// <summary>
/// Production Criteria Selection export 的 internal seam。公開 writer contract
/// 維持不變；Application 只透過此縫補入案件保存的 operator id。
/// </summary>
internal interface ITypedCriteriaSelectionReportWriter
{
    Task<ExportStats> WriteTypedAsync(
        Stream output,
        CriteriaSelectionReportContext context,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null);
}

public interface IValidationReportWriter
{
    Task<ExportStats> WriteAsync(
        Stream output,
        ValidationReportContext context,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null);
}

public interface IInfReportWriter
{
    Task<ExportStats> WriteAsync(
        Stream output,
        InfReportContext context,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null);
}

public interface IPrescreenReportWriter
{
    Task<ExportStats> WriteAsync(
        Stream output,
        PrescreenReportContext context,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null);
}

public interface ICriteriaSelectionReportWriter
{
    Task<ExportStats> WriteAsync(
        Stream output,
        CriteriaSelectionReportContext context,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null);
}
