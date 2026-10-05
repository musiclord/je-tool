using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// AuditCore reference-data plans 到同一個資料庫組裡各 store 的 typed facts adapter。
/// Store 仍擁有交易、bulk write、projection rollback 與結果失效的技術執行。
/// </summary>
internal sealed class ReferenceDataFactsPort(
    IAccountMappingImportPersistence accountMappingPersistence,
    IAuthorizedPreparerImportPersistence authorizedPreparerPersistence,
    ICalendarStore calendarStore) : IReferenceDataFactsPort
{
    public async Task<AccountMappingFacts> ExecuteAsync(
        AccountMappingPlan plan,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        AccountMappingProjection projection,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken)
    {
        var imported = await accountMappingPersistence.ImportAsync(
            plan.Request.ProjectId,
            source,
            columns,
            projection,
            rows,
            cancellationToken);
        var state = await accountMappingPersistence.FindStateAsync(
                plan.Request.ProjectId,
                cancellationToken)
            ?? throw new InvalidOperationException("科目配對匯入完成後找不到匯入狀態。");

        return new AccountMappingFacts(imported, state);
    }

    public async Task<AuthorizedPreparerFacts> ExecuteAsync(
        AuthorizedPreparerPlan plan,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        AuthorizedPreparerProjection projection,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken)
    {
        var imported = await authorizedPreparerPersistence.ImportAsync(
            plan.Request.ProjectId,
            source,
            columns,
            projection,
            rows,
            cancellationToken);
        return new AuthorizedPreparerFacts(imported);
    }

    public async Task<CalendarFacts> ExecuteAsync(
        CalendarPlan plan,
        IReadOnlyList<CalendarDayEntry> entries,
        CancellationToken cancellationToken)
    {
        await calendarStore.ReplaceDaysAsync(
            plan.ProjectId,
            plan.DayType,
            entries,
            cancellationToken);
        return new CalendarFacts(entries.Count);
    }

    public Task ExecuteAsync(
        NonWorkingDaysPlan plan,
        CancellationToken cancellationToken) =>
        calendarStore.InvalidateDependentResultsAsync(
            plan.ProjectId,
            cancellationToken);
}
