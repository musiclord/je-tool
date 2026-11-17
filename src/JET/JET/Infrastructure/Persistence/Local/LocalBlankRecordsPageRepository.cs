using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 分頁讀出空值與期外日期的紀錄。要讀哪一類由 <see cref="BlankRecordCategory"/> 決定，每一類對應一段固定的 WHERE 條件；
/// 判斷空白時用方言的去空白寫法。
/// 排序與換頁條件由 <c>KeysetPaging.Plan</c> 依 <c>ResultPageSorting.BlankRecords</c> 組出：沒有指定排序時依 entry_id 升冪，
/// 指定排序時依排序欄再依 entry_id。LIMIT 的寫法取自 <see cref="ISqlDialect"/>。
/// 資料驗證報告讀完一類的全部明細時改用 <see cref="IBlankRecordEntryStreamRepository"/>，一個查詢讀完。
/// </summary>
public sealed class LocalBlankRecordsPageRepository(ILocalProjectDatabase database)
    : IBlankRecordsPageRepository,
      IBlankRecordEntryStreamRepository
{
    async IAsyncEnumerable<long> IBlankRecordEntryStreamRepository.StreamEntryIdsAsync(
        string projectId,
        BlankRecordCategory category,
        string periodStart,
        string periodEnd,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        BindPeriod(command, category, periodStart, periodEnd);
        // 條件和分頁查詢用同一段；排序用分頁查詢沒有指定排序時的穩定鍵，所以讀出的分錄與順序和逐頁讀完相同。
        command.CommandText =
            "SELECT entry_id FROM gl " +
            "WHERE " + BlankRecordsCategoryPredicate.Scoped(category, database.Dialect) + " " +
            $"ORDER BY {ResultPageSorting.BlankRecords.StableKey.Sql};";
        command.CommandTimeout = 0;

        await using var reader = await command.ExecuteReaderAsync(
            System.Data.CommandBehavior.SequentialAccess | System.Data.CommandBehavior.SingleResult,
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return reader.GetInt64(0);
        }
    }

    public async Task<PageResult<BlankRecordRow>> GetPageAsync(
        string projectId,
        BlankRecordCategory category,
        string periodStart,
        string periodEnd,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var predicate = BlankRecordsCategoryPredicate.Scoped(category, database.Dialect);
        BindPeriod(command, category, periodStart, periodEnd);

        var paging = KeysetPaging.Plan(database.Dialect, request, ResultPageSorting.BlankRecords);
        foreach (var parameter in paging.Parameters)
        {
            command.AddWithValue(parameter.Key, parameter.Value);
        }

        command.AddWithValue("@pageSize", request.ClampedPageSize + 1);

        command.CommandText =
            "SELECT voucher_number, account_number, gl_date, description, entry_id, approval_date" + paging.SelectSuffix + " " +
            "FROM gl " +
            "WHERE " + predicate + paging.Predicate + " " +
            paging.OrderBy + " " + database.Dialect.LimitClause("@pageSize") + ";";

        var totalCount = await PageTotalCount.ReadKeysetAsync(command, request, paging, database.Dialect, cancellationToken);
        var buffer = new KeysetPageBuffer<BlankRecordRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            buffer.Add(BlankRecordRowMapper.MapRow(reader, category), paging.HasSort ? reader.GetValue(6) : null);
        }

        return buffer.ToPage(request, paging, static row => row.EntryId, totalCount);
    }

    // 只有「核准日不在期間」的條件用到查核期間；分頁與整份讀完共用。
    private static void BindPeriod(
        System.Data.Common.DbCommand command,
        BlankRecordCategory category,
        string periodStart,
        string periodEnd)
    {
        if (category == BlankRecordCategory.ApprovalDateNotInPeriod)
        {
            command.AddWithValue("@periodStart", periodStart);
            command.AddWithValue("@periodEnd", periodEnd);
        }
    }
}
