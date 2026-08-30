using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// Production Pre-screening workbook 的 finalized internal seam。Application 先完成
/// provider-neutral planning，Infrastructure 只依 plan 填入固定範本。
/// </summary>
internal interface IPlannedPrescreenReportWriter
{
    Task<ExportStats> WritePlannedAsync(
        Stream output,
        PrescreenReportContext context,
        PrescreenReportProjection projection,
        PrescreenReportPlan plan,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null);
}
