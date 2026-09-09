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

        var paging = KeysetPaging.Plan(Dialect, request, ResultPageSorting.GlEntryRows);

        await using var command = database.CreateCommand(connection, projectId,
            "SELECT g.document_number, g.line_item, g.post_date, g.account_code, g.account_name, " +
            "       g.amount_scaled, g.dr_cr, g.document_description, g.entry_id" + paging.SelectSuffix + " " +
            "FROM {s}.result_filter_run r " +
            "JOIN {s}.target_gl_entry g ON g.entry_id = r.entry_id " +
            $"WHERE r.scenario_position = @pos AND {GlEffectivePopulation.SqlPredicate("g")}" + paging.Predicate + " " +
            paging.OrderBy + " " + Dialect.LimitClause("@pageSize") + ";");
        command.Parameters.AddWithValue("@pos", scenarioPosition);
        foreach (var parameter in paging.Parameters)
        {
            command.Parameters.AddWithValue(parameter.Key, parameter.Value);
        }

        command.Parameters.AddWithValue("@pageSize", request.ClampedPageSize + 1);

        var buffer = new KeysetPageBuffer<FilterHitRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var entryId = reader.GetInt64(8);
            buffer.Add(
                new FilterHitRow(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt64(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    entryId),
                paging.HasSort ? reader.GetValue(9) : null);
        }

        return buffer.ToPage(request, paging, static row => row.EntryId);
    }
}
