using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 照 AuditCore 排好的計畫，把科目配對、授權編製人員清單、國定假日與補班日的匯入交給各自的 store 執行；
/// 每週休息日改了以後，請 <see cref="ICalendarStore"/> 讓受影響的結果失效。
/// 交易、大量寫入、失敗時回復與結果失效都在 store 裡處理。
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
        // 科目配對與新增的分類已經寫進案件；之後讀匯入狀態不受取消影響，畫面才不會把寫好的匯入當成已取消。
        var state = await accountMappingPersistence.FindStateAsync(
                plan.Request.ProjectId,
                CancellationToken.None)
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
