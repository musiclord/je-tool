using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 正式報告匯出要引用哪些來源。Application 先確認這些驗證結果、預篩選結果或情境版本是目前有效的，
/// 才組出這份輸入。它不帶路徑、活頁簿的資料列，也不帶 action 的 JSON。
/// </summary>
internal sealed record ReportExportRequest(
    string ActionName,
    string ProjectId,
    string? ValidationResultId = null,
    string? PrescreenResultId = null,
    string? ScenarioRevision = null,
    IReadOnlyList<int>? ScenarioPositions = null,
    ReportWorkbookMetadata? WorkbookMetadata = null,
    IReadOnlyList<GlRdeFieldMetadata>? CustomFields = null,
    string? FilterDataRevision = null);

/// <summary>
/// 一個匯出動作的計畫，由 AuditCore 決定三件事：要出哪幾份報告、引用哪些來源、是否整批一起發布。
/// 報告內容的寫出與存檔不在 AuditCore。
/// </summary>
internal sealed record ReportExportPlan(
    ReportExportRequest Request,
    IReadOnlyList<ReportKind> ReportKinds,
    ReportSourceRefs SourceRef,
    ReportWorkbookMetadata? WorkbookMetadata,
    IReadOnlyList<GlRdeFieldMetadata> CustomFields,
    bool UseAtomicBatch);

/// <summary>
/// 發布的報告。OperationLogWarning 是報告已發布、卻寫不進操作紀錄時給審計員的提醒，
/// 寫進去時是 null。
/// </summary>
internal sealed record ReportExportFacts(IReadOnlyList<Report> Reports, string? OperationLogWarning = null);

internal sealed record ReportExportResult(
    ReportExportPlan Plan,
    IReadOnlyList<Report> Reports,
    string? OperationLogWarning = null);

/// <summary>
/// 發布報告的介面，只照計畫執行，不做判斷。Application 提供已經備好的內容寫出程式；
/// 實作依 <see cref="ReportExportPlan"/> 的 <c>UseAtomicBatch</c>，用報告儲存區的單份發布或整批發布。
/// </summary>
internal interface IReportExportFactsPort
{
    Task<ReportExportFacts> ExecuteAsync(
        ReportExportPlan plan,
        CancellationToken cancellationToken);
}

public static partial class JetAuditProgram
{
    private const string ValidationReportsAction = "export.validationReports";
    private const string PrescreenReportAction = "export.prescreenReport";
    private const string CriteriaSelectionReportAction = "export.criteriaSelectionReport";

    internal static ReportExportPlan Plan(ReportExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ActionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectId);
        var workbookMetadata = request.WorkbookMetadata;
        IReadOnlyList<GlRdeFieldMetadata> customFields;
        if (workbookMetadata is null)
        {
            if (request.CustomFields is { Count: > 0 })
            {
                throw new InvalidOperationException(
                    "Report export custom fields require canonical workbook metadata.");
            }
            customFields = Array.AsReadOnly(Array.Empty<GlRdeFieldMetadata>());
        }
        else
        {
            ReportWorkbookMetadataInvariant.Validate(workbookMetadata);
            customFields = ReportWorkbookMetadataInvariant.ValidateCustomFields(
                workbookMetadata,
                request.CustomFields);
        }

        var (kinds, sourceRef, useAtomicBatch) = request.ActionName switch
        {
            // 科目配對範本是工作檔，不在報告匯出計畫裡；驗證批次只有兩份報告。
            ValidationReportsAction => (
                Kinds(
                    ReportKind.ValidationReport,
                    ReportKind.InfReport),
                new ReportSourceRefs(
                    ValidationResultId: Required(
                        request.ValidationResultId,
                        nameof(request.ValidationResultId))),
                true),
            PrescreenReportAction => (
                Kinds(ReportKind.PrescreenReport),
                new ReportSourceRefs(
                    PrescreenResultId: Required(
                        request.PrescreenResultId,
                        nameof(request.PrescreenResultId))),
                false),
            CriteriaSelectionReportAction => (
                Kinds(ReportKind.CriteriaSelectionReport),
                new ReportSourceRefs(
                    ValidationResultId: Required(
                        request.ValidationResultId,
                        nameof(request.ValidationResultId)),
                    ScenarioRevision: Required(
                        request.ScenarioRevision,
                        nameof(request.ScenarioRevision)),
                    ScenarioPositions: Array.AsReadOnly(
                        (request.ScenarioPositions
                            ?? throw new InvalidOperationException(
                                "Criteria export 缺少 ScenarioPositions。"))
                        .ToArray()),
                    FilterDataRevision: Required(request.FilterDataRevision, nameof(request.FilterDataRevision))),
                false),
            _ => throw new InvalidOperationException(
                $"Report export lifecycle 未登錄 action '{request.ActionName}'。")
        };

        return new ReportExportPlan(
            request,
            kinds,
            sourceRef,
            workbookMetadata,
            customFields,
            useAtomicBatch);
    }

    internal static ReportExportResult Finalize(
        ReportExportPlan plan,
        ReportExportFacts facts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(facts.Reports);

        // 報告內容的要求在呼叫報告儲存區之前已經檢查過。這裡不再檢查儲存區的回傳，
        // 以免報告發布之後又多出一個會失敗的地方。
        return new ReportExportResult(plan, facts.Reports, facts.OperationLogWarning);
    }

    private static IReadOnlyList<ReportKind> Kinds(
        params ReportKind[] kinds) =>
        Array.AsReadOnly(kinds);

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Report export 缺少 {name}。")
            : value;

}
