namespace JET.Application.Commands.ExportReport
{
    public sealed class ExportReportCommandHandler
    {
        public Task<object> HandleExportValidationAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<object>(new { ok = true, message = "Validation report export delegated to frontend." });
        }

        public Task<object> HandleExportPrescreenAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<object>(new { ok = true, message = "Pre-screening report export delegated to frontend." });
        }

        public Task<object> HandleExportCriteriaAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<object>(new { ok = true, message = "Criteria report export delegated to frontend." });
        }

        public Task<object> HandleExportWorkpaperAsync(object selected, CancellationToken cancellationToken)
        {
            return Task.FromResult<object>(new { ok = true, message = "Workpaper export delegated to frontend." });
        }
    }
}
