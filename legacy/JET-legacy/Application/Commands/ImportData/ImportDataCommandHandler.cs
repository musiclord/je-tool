using JET.Domain.Abstractions;
using JET.Domain.Abstractions.Repositories;

namespace JET.Application.Commands.ImportData
{
    public sealed class ImportDataCommandHandler
    {
        private readonly IProjectSessionStore _session;
        private readonly IDateDimensionRepository _dateDimensionRepository;

        public ImportDataCommandHandler(IProjectSessionStore session, IDateDimensionRepository dateDimensionRepository)
        {
            _session = session;
            _dateDimensionRepository = dateDimensionRepository;
        }

        public async Task<object> HandleImportHolidayAsync(IReadOnlyList<string> dates, CancellationToken cancellationToken)
        {
            _session.SetHolidays(dates);
            // Persist to staging_calendar_raw_day when a project pointer is available.
            // The pointer is set by CreateProjectCommandHandler; legacy callers without
            // a project (e.g. exploratory UI before project.create) keep the session-only
            // behavior so this stays an additive change.
            var projectId = _session.CurrentProjectId;
            string? batchId = null;
            if (!string.IsNullOrWhiteSpace(projectId))
            {
                batchId = await _dateDimensionRepository
                    .ReplaceCalendarInputAsync(projectId, "holiday", dates, cancellationToken)
                    .ConfigureAwait(false);
            }
            return new { dates, batchId };
        }

        public async Task<object> HandleImportMakeupDayAsync(IReadOnlyList<string> dates, CancellationToken cancellationToken)
        {
            _session.SetMakeupDays(dates);
            var projectId = _session.CurrentProjectId;
            string? batchId = null;
            if (!string.IsNullOrWhiteSpace(projectId))
            {
                batchId = await _dateDimensionRepository
                    .ReplaceCalendarInputAsync(projectId, "makeupDay", dates, cancellationToken)
                    .ConfigureAwait(false);
            }
            return new { dates, batchId };
        }
    }
}
