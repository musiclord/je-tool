using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由行事曆存取(比照 <see cref="ProviderRoutingGlRepository"/>)。</summary>
public sealed class ProviderRoutingCalendarStore(
    ProjectProviderResolver resolver,
    ICalendarStore sqlite,
    ICalendarStore sqlServer,
    ICalendarStore duckDb) : ICalendarStore
{
    public async Task ReplaceDaysAsync(
        string projectId,
        CalendarDayType type,
        IReadOnlyList<CalendarDayEntry> days,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .ReplaceDaysAsync(projectId, type, days, cancellationToken);
    }

    public async Task<int> CountAsync(string projectId, CalendarDayType type, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb).CountAsync(projectId, type, cancellationToken);
    }

    public async Task InvalidateDependentResultsAsync(string projectId, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .InvalidateDependentResultsAsync(projectId, cancellationToken);
    }
}
