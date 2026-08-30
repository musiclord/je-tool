using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// Production Validation workbook 的 finalized internal seam。Application 先完成
/// provider-neutral planning 與 Field Info projection，Infrastructure 只依 plan
/// 寫入固定範本，不重新判斷門檻或解讀保存的 JSON。
/// </summary>
internal interface IPlannedValidationReportWriter
{
    Task<ExportStats> WritePlannedAsync(
        Stream output,
        ValidationReportContext context,
        ValidationReportProjection projection,
        ValidationReportPlan plan,
        FieldInfoProjection fieldInfo,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null);
}
