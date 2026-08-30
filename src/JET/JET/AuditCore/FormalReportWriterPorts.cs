using JET.Domain;

namespace JET.AuditCore;

internal interface IFormalPlannedValidationReportWriter : IPlannedValidationReportWriter
{
    Task<ExportStats> WriteFormalPlannedAsync(
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

internal interface IFormalPlannedPrescreenReportWriter : IPlannedPrescreenReportWriter
{
    Task<ExportStats> WriteFormalPlannedAsync(
        Stream output,
        PrescreenReportContext context,
        PrescreenReportProjection projection,
        PrescreenReportPlan plan,
        ReportWorkbookMetadata workbookMetadata,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null);
}
