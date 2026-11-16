using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 分頁讀出空值與期外日期的紀錄。要讀哪一類由 <see cref="BlankRecordCategory"/> 決定，每一類對應一段固定的 WHERE 條件；
/// 判斷空白時用方言的去空白寫法。
/// 排序與換頁條件由 <c>KeysetPaging.Plan</c> 依 <c>ResultPageSorting.BlankRecords</c> 組出：沒有指定排序時依 entry_id 升冪，
/// 指定排序時依排序欄再依 entry_id。LIMIT 的寫法取自 <see cref="ISqlDialect"/>。
/// </summary>
public sealed class LocalBlankRecordsPageRepository(ILocalProjectDatabase database)
    : IBlankRecordsPageRepository
{
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
        if (category == BlankRecordCategory.ApprovalDateNotInPeriod)
        {
            command.AddWithValue("@periodStart", periodStart);
            command.AddWithValue("@periodEnd", periodEnd);
        }

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
}
