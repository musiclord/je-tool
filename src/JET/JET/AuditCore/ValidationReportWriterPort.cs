using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 驗證報告的唯一寫出入口。Application 先完成跨資料庫的規劃、欄位資訊投影與工作簿中繼資料，
/// Infrastructure 只依計畫填固定範本，不重新判斷門檻或解讀保存的 JSON。
/// </summary>
internal interface IValidationReportWriter
{
    Task<ExportStats> WriteAsync(
        Stream output,
        ValidationReportContext context,
        ValidationReportProjection projection,
        ValidationReportPlan plan,
        FieldInfoProjection fieldInfo,
        ReportWorkbookMetadata workbookMetadata,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null);
}
