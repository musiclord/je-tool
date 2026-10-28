using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 預篩選報告的唯一寫出入口。Application 先完成跨資料庫的規劃與工作簿中繼資料，
/// Infrastructure 只依計畫填固定範本。
/// </summary>
internal interface IPrescreenReportWriter
{
    Task<ExportStats> WriteAsync(
        Stream output,
        PrescreenReportContext context,
        PrescreenReportProjection projection,
        PrescreenReportPlan plan,
        ReportWorkbookMetadata workbookMetadata,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null);
}
