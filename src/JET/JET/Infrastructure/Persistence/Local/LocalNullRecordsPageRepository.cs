using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 空值/期外日期紀錄 keyset 分頁(本地引擎)。category 由白名單列舉決定 WHERE 述詞(空白判定 TRIM(x)='');
/// 排序鍵 entry_id ASC、游標展開布林式 <c>AND entry_id &gt; @cursor</c>(@cursor 綁 long,首頁省略)、
/// limit 由注入的 <see cref="ISqlDialect"/> 出。SELECT 末欄取 entry_id 供編游標,不放進 wire row。
/// </summary>
public sealed class LocalNullRecordsPageRepository(ILocalProjectDatabase database)
    : INullRecordsPageRepository
{
    public async Task<PageResult<NullRecordRow>> GetPageAsync(
        string projectId,
        NullRecordCategory category,
        string periodStart,
        string periodEnd,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var predicate = NullRecordsCategoryPredicate.Scoped(category, database.Dialect);
        if (category == NullRecordCategory.OutOfRangeDate)
        {
            command.AddWithValue("@periodStart", periodStart);
            command.AddWithValue("@periodEnd", periodEnd);
        }

        var paging = KeysetPaging.Plan(database.Dialect, request, ResultPageSorting.NullRecords);
        foreach (var parameter in paging.Parameters)
        {
            command.AddWithValue(parameter.Key, parameter.Value);
        }

        command.AddWithValue("@pageSize", request.ClampedPageSize + 1);

        command.CommandText =
            "SELECT document_number, account_code, post_date, document_description, entry_id" + paging.SelectSuffix + " " +
            "FROM target_gl_entry " +
            "WHERE " + predicate + paging.Predicate + " " +
            paging.OrderBy + " " + database.Dialect.LimitClause("@pageSize") + ";";

        var buffer = new KeysetPageBuffer<NullRecordRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            buffer.Add(NullRecordRowMapper.MapRow(reader, category), paging.HasSort ? reader.GetValue(5) : null);
        }

        return buffer.ToPage(request, paging, static row => row.EntryId);
    }
}
