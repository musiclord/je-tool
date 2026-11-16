using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 分頁讀出一個已存篩選情境命中的分錄明細。分頁用 keyset 方式：用上一頁最後一筆的排序鍵接下一頁。
/// 先用情境位置從 filter_scenario 找到 scenario_id，JOIN filter_hit 取這個情境命中的 entry_id，再 JOIN gl 取要顯示的欄位。
/// 預設依 entry_id 升冪；排序、搜尋與換頁條件由 <see cref="KeysetPaging"/> 依 <see cref="ResultPageSorting.GlEntryRows"/> 組出，
/// LIMIT 的寫法取自 <see cref="ISqlDialect"/>。
/// SELECT 多取一欄 g.entry_id，用來編下一頁的 cursor；它不是回給前端的欄位。情境位置與 cursor 都用參數傳。
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
            "SELECT g.voucher_number, g.line_item, g.gl_date, g.account_number, g.account_name, " +
            "       g.amount_scaled, g.dr_cr, g.description, g.entry_id" + paging.SelectSuffix + " " +
            "FROM filter_hit r JOIN filter_scenario fs ON fs.scenario_id = r.scenario_id " +
            "JOIN gl g ON g.entry_id = r.entry_id " +
            $"WHERE fs.position = @pos AND {GlExcludedPopulation.SqlPredicate("g")}" + paging.Predicate + " " +
            paging.OrderBy + " " + database.Dialect.LimitClause("@pageSize") + ";";

        var totalCount = await PageTotalCount.ReadKeysetAsync(command, request, paging, database.Dialect, cancellationToken);
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

        return buffer.ToPage(request, paging, static row => row.EntryId, totalCount);
    }
}
