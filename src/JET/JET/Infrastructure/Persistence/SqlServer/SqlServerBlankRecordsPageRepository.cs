using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 空值/期外日期紀錄 keyset 分頁(SQL Server,鏡像 <see cref="LocalBlankRecordsPageRepository"/>)。
/// 空白判定由方言的去空白方法產生;排序鍵 entry_id ASC、游標展開布林式(@cursor 綁 long)、
/// limit 由 <see cref="SqlServerDialect"/> 出 OFFSET/FETCH(ORDER BY entry_id 已具備)。
/// </summary>
public sealed class SqlServerBlankRecordsPageRepository(SqlServerProjectDatabase database)
    : IBlankRecordsPageRepository
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    public async Task<PageResult<BlankRecordRow>> GetPageAsync(
        string projectId,
        BlankRecordCategory category,
        string periodStart,
        string periodEnd,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        var predicate = BlankRecordsCategoryPredicate.Scoped(category, SqlServerDialect.Instance);
        var paging = KeysetPaging.Plan(Dialect, request, ResultPageSorting.BlankRecords);

        await using var command = database.CreateCommand(connection, projectId,
            "SELECT voucher_number, account_number, gl_date, description, entry_id" + paging.SelectSuffix + " " +
            "FROM {s}.gl " +
            "WHERE " + predicate + paging.Predicate + " " +
            paging.OrderBy + " " + Dialect.LimitClause("@pageSize") + ";");
        if (category == BlankRecordCategory.ApprovalDateNotInPeriod)
        {
            command.Parameters.AddWithValue("@periodStart", periodStart);
            command.Parameters.AddWithValue("@periodEnd", periodEnd);
        }

        foreach (var parameter in paging.Parameters)
        {
            command.Parameters.AddWithValue(parameter.Key, parameter.Value);
        }

        command.Parameters.AddWithValue("@pageSize", request.ClampedPageSize + 1);

        var totalCount = await PageTotalCount.ReadKeysetAsync(command, request, paging, Dialect, cancellationToken);
        var buffer = new KeysetPageBuffer<BlankRecordRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            buffer.Add(BlankRecordRowMapper.MapRow(reader, category), paging.HasSort ? reader.GetValue(5) : null);
        }

        return buffer.ToPage(request, paging, static row => row.EntryId, totalCount);
    }
}
