using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由行事曆逐日讀回(比照 <see cref="ProviderRoutingCreatorSummaryExportRepository"/>)。</summary>
public sealed class ProviderRoutingCalendarExportRepository(
    ProjectProviderResolver resolver,
    ICalendarExportRepository sqlite,
    ICalendarExportRepository sqlServer,
    ICalendarExportRepository duckDb) : ICalendarExportRepository
{
    public async Task<IReadOnlyList<CalendarDayEntry>> FetchDaysAsync(
        string projectId, CalendarDayType type, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .FetchDaysAsync(projectId, type, cancellationToken);
    }
}
