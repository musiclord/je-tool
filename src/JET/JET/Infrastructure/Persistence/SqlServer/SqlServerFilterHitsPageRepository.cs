using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 已存篩選情境命中行層明細 keyset 分頁(SQL Server,鏡像 <see cref="LocalFilterHitsPageRepository"/>)。
/// 排序鍵 entry_id ASC、游標展開布林式(@cursor 綁 long)、limit 由 <see cref="SqlServerDialect"/>
/// 出 OFFSET/FETCH(ORDER BY g.entry_id 已具備)。
/// </summary>
public sealed class SqlServerFilterHitsPageRepository(SqlServerProjectDatabase database)
    : IFilterHitsPageRepository
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    public async Task<PageResult<FilterHitRow>> GetPageAsync(
        string projectId, int scenarioPosition, int moneyScale, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        var hasCursor = PageCursor.TryDecode(request.Cursor, out var cursorKey);
        var keyset = hasCursor ? "AND g.entry_id > @cursor" : string.Empty;

        await using var command = database.CreateCommand(connection, projectId,
            "SELECT g.document_number, g.line_item, g.post_date, g.account_code, g.account_name, " +
            "       g.amount_scaled, g.dr_cr, g.document_description, g.entry_id " +
            "FROM {s}.result_filter_run r " +
            "JOIN {s}.target_gl_entry g ON g.entry_id = r.entry_id " +
            $"WHERE r.scenario_position = @pos AND {GlEffectivePopulation.SqlPredicate("g")} " + keyset + " " +
            "ORDER BY g.entry_id " + Dialect.LimitClause("@pageSize") + ";");
        command.Parameters.AddWithValue("@pos", scenarioPosition);
        if (hasCursor)
        {
            command.Parameters.AddWithValue("@cursor", long.Parse(cursorKey));
        }

        command.Parameters.AddWithValue("@pageSize", request.ClampedPageSize + 1);

        var rows = new List<FilterHitRow>();
        long lastEntryId = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lastEntryId = reader.GetInt64(8);
            rows.Add(new FilterHitRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                lastEntryId));
        }

        var hasMore = rows.Count > request.ClampedPageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var next = hasMore
            ? PageCursor.Encode(rows[^1].EntryId.ToString())
            : null;
        return new PageResult<FilterHitRow>(rows, next);
    }
}
