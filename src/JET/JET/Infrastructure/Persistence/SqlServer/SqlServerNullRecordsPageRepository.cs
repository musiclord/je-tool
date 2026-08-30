using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 空值/期外日期紀錄 keyset 分頁(SQL Server,鏡像 <see cref="LocalNullRecordsPageRepository"/>)。
/// 空白判定用 LTRIM(RTRIM(x))='';排序鍵 entry_id ASC、游標展開布林式(@cursor 綁 long)、
/// limit 由 <see cref="SqlServerDialect"/> 出 OFFSET/FETCH(ORDER BY entry_id 已具備)。
/// </summary>
public sealed class SqlServerNullRecordsPageRepository(SqlServerProjectDatabase database)
    : INullRecordsPageRepository
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    public async Task<PageResult<NullRecordRow>> GetPageAsync(
        string projectId,
        NullRecordCategory category,
        string periodStart,
        string periodEnd,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        var predicate = NullRecordsCategoryPredicate.ScopedSqlServer(category);
        var hasCursor = PageCursor.TryDecode(request.Cursor, out var cursorKey);
        var keyset = hasCursor ? "AND entry_id > @cursor" : string.Empty;

        await using var command = database.CreateCommand(connection, projectId,
            "SELECT document_number, account_code, post_date, document_description, entry_id " +
            "FROM {s}.target_gl_entry " +
            "WHERE " + predicate + " " + keyset + " " +
            "ORDER BY entry_id " + Dialect.LimitClause("@pageSize") + ";");
        if (category == NullRecordCategory.OutOfRangeDate)
        {
            command.Parameters.AddWithValue("@periodStart", periodStart);
            command.Parameters.AddWithValue("@periodEnd", periodEnd);
        }

        if (hasCursor)
        {
            command.Parameters.AddWithValue("@cursor", long.Parse(cursorKey));
        }

        command.Parameters.AddWithValue("@pageSize", request.ClampedPageSize + 1);

        var rows = new List<NullRecordRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(NullRecordRowMapper.MapRow(reader, category));
        }

        var hasMore = rows.Count > request.ClampedPageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var next = hasMore
            ? PageCursor.Encode(rows[^1].EntryId.ToString())
            : null;
        return new PageResult<NullRecordRow>(rows, next);
    }
}
