using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// Formal report export source identity after Application has validated the current
/// run or scenario revision. It does not carry paths, workbook rows, or wire JSON.
/// </summary>
internal sealed record ReportExportRequest(
    string ActionName,
    string ProjectId,
    string? ValidationRunId = null,
    string? PrescreenRunId = null,
    string? ScenarioRevision = null,
    IReadOnlyList<int>? ScenarioPositions = null,
    ReportWorkbookMetadata? WorkbookMetadata = null,
    IReadOnlyList<GlRdeFieldMetadata>? CustomFields = null);

/// <summary>
/// AuditCore-owned artifact set, source references, and atomicity for one included
/// export action. Content writers and artifact persistence remain outside AuditCore.
/// </summary>
internal sealed record ReportExportPlan(
    ReportExportRequest Request,
    ProgramNode Node,
    IReadOnlyList<ReportArtifactKind> ArtifactKinds,
    ReportArtifactSourceRefs SourceRef,
    ReportWorkbookMetadata? WorkbookMetadata,
    IReadOnlyList<GlRdeFieldMetadata> CustomFields,
    bool UseAtomicBatch);

internal sealed record ReportExportFacts(IReadOnlyList<ReportArtifact> Artifacts);

internal sealed record ReportExportResult(
    ReportExportPlan Plan,
    IReadOnlyList<ReportArtifact> Artifacts);

/// <summary>
/// Mechanical artifact publication port. Application supplies the already-shaped
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
    private const string ValidationArtifactsAction = "export.validationArtifacts";
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
            ValidationArtifactsAction => (
                Kinds(
                    ReportArtifactKind.ValidationReport,
                    ReportArtifactKind.InfReport),
                new ReportArtifactSourceRefs(
                    ValidationRunId: Required(
                        request.ValidationRunId,
                        nameof(request.ValidationRunId))),
                true),
            PrescreenReportAction => (
                Kinds(ReportArtifactKind.PrescreenReport),
                new ReportArtifactSourceRefs(
                    PrescreenRunId: Required(
                        request.PrescreenRunId,
                        nameof(request.PrescreenRunId))),
                false),
            CriteriaSelectionReportAction => (
                Kinds(ReportArtifactKind.CriteriaSelectionReport),
                new ReportArtifactSourceRefs(
                    ValidationRunId: Required(
                        request.ValidationRunId,
                        nameof(request.ValidationRunId)),
                    ScenarioRevision: Required(
                        request.ScenarioRevision,
                        nameof(request.ScenarioRevision)),
                    ScenarioPositions: Array.AsReadOnly(
                        (request.ScenarioPositions
                            ?? throw new InvalidOperationException(
                                "Criteria export 缺少 ScenarioPositions。"))
                        .ToArray())),
                false),
            _ => throw new InvalidOperationException(
                $"Report export lifecycle 未登錄 action '{request.ActionName}'。")
        };

        return new ReportExportPlan(
            request,
            ProgramGraph.Current.RequireNode(request.ActionName),
            kinds,
            sourceRef,
            workbookMetadata,
            customFields,
            useAtomicBatch);
    }

    internal static Task<ReportExportFacts> ExecuteAsync(
        ReportExportPlan plan,
        IReportExportFactsPort factsPort,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(factsPort);
        return factsPort.ExecuteAsync(plan, cancellationToken);
    }

    internal static ReportExportResult Finalize(
        ReportExportPlan plan,
        ReportExportFacts facts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(facts.Artifacts);

        // Content requests are checked before the store call. Do not add a new
        // post-publication failure boundary by revalidating store output here.
        return new ReportExportResult(plan, facts.Artifacts);
    }

    internal static string Explain(ReportExportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"{result.Plan.Request.ActionName}：產出 "
            + string.Join(
                "、",
                result.Artifacts.Select(artifact =>
                    ReportArtifactKindValues.ToValue(artifact.Kind)))
            + "。";
    }

    private static IReadOnlyList<ReportArtifactKind> Kinds(
        params ReportArtifactKind[] kinds) =>
        Array.AsReadOnly(kinds);

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Report export 缺少 {name}。")
            : value;

}
