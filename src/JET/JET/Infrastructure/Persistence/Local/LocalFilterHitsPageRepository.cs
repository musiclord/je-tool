using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 已存篩選情境命中行層明細 keyset 分頁(本地引擎)。JOIN result_filter_run 取該 position 的命中 entry_id,
/// 再 JOIN target_gl_entry 取顯示欄;預設排序鍵 entry_id ASC，排序、搜尋與游標述詞由 <see cref="KeysetPaging"/>
/// 依 <see cref="ResultPageSorting.GlEntryRows"/> 組出、limit 由注入的 <see cref="ISqlDialect"/> 出。
/// SELECT 末欄取 g.entry_id 供編游標,不放進 wire row。scenario_position 與游標皆參數綁定。
/// </summary>
public sealed class LocalFilterHitsPageRepository(ILocalProjectDatabase database)
    : IFilterHitsPageRepository
{
    public async Task<PageResult<FilterHitRow>> GetPageAsync(
        string projectId, int scenarioPosition, int moneyScale, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.AddWithValue("@pos", scenarioPosition);

        var paging = KeysetPaging.Plan(database.Dialect, request, ResultPageSorting.GlEntryRows);
        foreach (var parameter in paging.Parameters)
        {
            command.AddWithValue(parameter.Key, parameter.Value);
        }

        command.AddWithValue("@pageSize", request.ClampedPageSize + 1);

        command.CommandText =
            "SELECT g.document_number, g.line_item, g.post_date, g.account_code, g.account_name, " +
            "       g.amount_scaled, g.dr_cr, g.document_description, g.entry_id" + paging.SelectSuffix + " " +
            "FROM result_filter_run r " +
            "JOIN target_gl_entry g ON g.entry_id = r.entry_id " +
            $"WHERE r.scenario_position = @pos AND {GlEffectivePopulation.SqlPredicate("g")}" + paging.Predicate + " " +
            paging.OrderBy + " " + database.Dialect.LimitClause("@pageSize") + ";";

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
