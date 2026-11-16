using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 分頁讀出 INF 抽樣的樣本分錄明細，分頁用 keyset 方式：用上一頁最後一筆的排序鍵接下一頁。
/// 從 inf_sample JOIN gl 取要顯示的欄位，借方與貸方金額各一欄；只讀呼叫端指定的 resultId 那一次抽出的樣本。
/// 排序、搜尋與換頁條件由 <c>KeysetPaging.Plan</c> 依 <c>ResultPageSorting.InfSample</c> 組出，沒有指定排序時依 entry_id 升冪；
/// LIMIT 的寫法取自 <see cref="ISqlDialect"/>。SELECT 多取 g.entry_id，用來編下一頁的 cursor；它不是回給前端的欄位。
/// </summary>
public sealed class LocalInfSamplePageRepository(ILocalProjectDatabase database)
    : IInfSamplePageRepository
{
    public async Task<PageResult<InfSampleRow>> GetPageAsync(
        string projectId, string resultId, int moneyScale, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var paging = KeysetPaging.Plan(database.Dialect, request, ResultPageSorting.InfSample);
        foreach (var parameter in paging.Parameters)
        {
            command.AddWithValue(parameter.Key, parameter.Value);
        }

        command.AddWithValue("@pageSize", request.ClampedPageSize + 1);
        command.AddWithValue("@resultId", resultId);

        command.CommandText =
            "SELECT g.voucher_number, g.account_number, g.account_name, " +
            "       g.debit_amount_scaled, g.credit_amount_scaled, " +
            "       g.gl_date, g.approval_date, g.created_by, g.approved_by, g.description, g.entry_id, " +
            "       g.source_module, g.dr_cr" + paging.SelectSuffix + " " +
            "FROM inf_sample s " +
            "JOIN gl g ON g.entry_id = s.entry_id " +
            "WHERE " + InfSamplePageSql.ResultFilter +
            $" AND {GlExcludedPopulation.SqlPredicate("g")}" + paging.Predicate + " " +
            paging.OrderBy + " " + database.Dialect.LimitClause("@pageSize") + ";";

        var totalCount = await PageTotalCount.ReadKeysetAsync(command, request, paging, database.Dialect, cancellationToken);
        var buffer = new KeysetPageBuffer<InfSampleRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            buffer.Add(
                new InfSampleRow(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.GetInt64(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.GetString(12)),
                paging.HasSort ? reader.GetValue(13) : null);
        }

        return buffer.ToPage(request, paging, static row => row.EntryId, totalCount);
    }
}
