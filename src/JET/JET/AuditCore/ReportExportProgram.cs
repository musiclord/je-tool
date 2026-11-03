using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// Formal report export source identity after Application has validated the current
/// run or scenario revision. It does not carry paths, workbook rows, or wire JSON.
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
/// AuditCore-owned report set, source references, and atomicity for one included
/// export action. Content writers and report persistence remain outside AuditCore.
/// </summary>
internal sealed record ReportExportPlan(
    ReportExportRequest Request,
    IReadOnlyList<ReportKind> ReportKinds,
    ReportSourceRefs SourceRef,
    ReportWorkbookMetadata? WorkbookMetadata,
    IReadOnlyList<GlRdeFieldMetadata> CustomFields,
    bool UseAtomicBatch);

/// <summary>
/// 發布的報告。OperationLogWarning 是報告已發布、卻寫不進操作紀錄時給審計員的提醒
/// （裁定 2026-10-19-20），寫進去時是 null。
/// </summary>
internal sealed record ReportExportFacts(IReadOnlyList<Report> Reports, string? OperationLogWarning = null);

internal sealed record ReportExportResult(
    ReportExportPlan Plan,
    IReadOnlyList<Report> Reports,
    string? OperationLogWarning = null);

/// <summary>
/// Mechanical report publication port. Application supplies the already-shaped
/// content writers; the implementation preserves the existing single/batch store
/// boundary selected by <see cref="ReportExportPlan"/>.
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
            // 帳戶對應範本自 2026-09-02 起是工作檔，不在報告匯出計畫裡；驗證批次只剩兩份報告。
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

        // Content requests are checked before the store call. Do not add a new
        // post-publication failure boundary by revalidating store output here.
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
