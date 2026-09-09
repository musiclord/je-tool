using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 借貸不平傳票 keyset 分頁(本地引擎)。GROUP BY document_number HAVING SUM(amount_scaled)≠0;
/// 排序鍵 document_number ASC、游標展開布林式 <c>AND document_number &gt; @cursor</c> 置於 WHERE
/// (GROUP BY 前,先過濾游標再彙總;首頁省略)、limit 由注入的 <see cref="ISqlDialect"/> 出。
/// </summary>
public sealed class LocalDocBalancePageRepository(ILocalProjectDatabase database)
    : IDocBalancePageRepository
{
    public async Task<PageResult<UnbalancedDocument>> GetPageAsync(
        string projectId, int moneyScale, string periodStart, string periodEnd, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var paging = KeysetPaging.Plan(database.Dialect, request, ResultPageSorting.DocBalance);
        foreach (var parameter in paging.Parameters)
        {
            command.AddWithValue(parameter.Key, parameter.Value);
        }

        command.AddWithValue("@pageSize", request.ClampedPageSize + 1);

        // 母體核心走 ValidationProcedures.UnbalancedCore(有效分錄),與計數端同口徑；彙總後包成子查詢，
        // 排序、搜尋與游標述詞才能用彙總欄（debit_s、credit_s、diff_s）。
        command.CommandText =
            "SELECT document_number, debit_s, credit_s, diff_s" + paging.SelectSuffix + " FROM (" +
            "SELECT document_number, " +
            "COALESCE(SUM(debit_amount_scaled), 0) AS debit_s, " +
            "COALESCE(SUM(credit_amount_scaled), 0) AS credit_s, " +
            "COALESCE(SUM(amount_scaled), 0) AS diff_s " +
            ValidationProcedures.UnbalancedCore(string.Empty) +
            ") u WHERE 1 = 1" + paging.Predicate + " " +
            paging.OrderBy + " " + database.Dialect.LimitClause("@pageSize") + ";";

        var buffer = new KeysetPageBuffer<UnbalancedDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            buffer.Add(
                new UnbalancedDocument(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)),
                paging.HasSort ? reader.GetValue(4) : null);
        }

        return buffer.ToPage(request, paging, static row => row.DocumentNumber ?? string.Empty);
    }
}
