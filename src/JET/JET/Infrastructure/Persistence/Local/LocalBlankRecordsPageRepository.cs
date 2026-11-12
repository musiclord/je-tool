using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 空值/期外日期紀錄 keyset 分頁。category 由白名單列舉決定 WHERE 述詞(空白判定由方言的去空白方法產生);
/// 排序與游標由 <c>KeysetPaging.Plan</c> 依 <c>ResultPageSorting.BlankRecords</c> 組出：沒有指定排序時依 entry_id 升冪，
/// 指定排序時依排序欄再依 entry_id。limit 由注入的 <see cref="ISqlDialect"/> 出。
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
