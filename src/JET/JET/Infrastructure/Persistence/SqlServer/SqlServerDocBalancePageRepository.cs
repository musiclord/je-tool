using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 借貸不平傳票 keyset 分頁(SQL Server,鏡像 <see cref="LocalDocBalancePageRepository"/>)。
/// 排序鍵 document_number ASC、游標展開布林式置於 WHERE(GROUP BY 前)、
/// limit 由 <see cref="SqlServerDialect"/> 出 OFFSET/FETCH(ORDER BY document_number 已具備)。
/// </summary>
public sealed class SqlServerDocBalancePageRepository(SqlServerProjectDatabase database)
    : IDocBalancePageRepository
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    public async Task<PageResult<UnbalancedDocument>> GetPageAsync(
        string projectId, int moneyScale, string periodStart, string periodEnd, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        var paging = KeysetPaging.Plan(Dialect, request, ResultPageSorting.DocBalance);

        // 母體核心走 ValidationProcedures.UnbalancedCore(有效分錄),與計數端同口徑；彙總後包成子查詢，
        // 排序、搜尋與游標述詞才能用彙總欄（debit_s、credit_s、diff_s）。
        await using var command = database.CreateCommand(connection, projectId,
            "SELECT document_number, debit_s, credit_s, diff_s" + paging.SelectSuffix + " FROM (" +
            "SELECT document_number, " +
            "COALESCE(SUM(debit_amount_scaled), 0) AS debit_s, " +
            "COALESCE(SUM(credit_amount_scaled), 0) AS credit_s, " +
            "COALESCE(SUM(amount_scaled), 0) AS diff_s " +
            ValidationProcedures.UnbalancedCore("{s}.") +
            ") u WHERE 1 = 1" + paging.Predicate + " " +
            paging.OrderBy + " " + Dialect.LimitClause("@pageSize") + ";");
        foreach (var parameter in paging.Parameters)
        {
            command.Parameters.AddWithValue(parameter.Key, parameter.Value);
        }

        command.Parameters.AddWithValue("@pageSize", request.ClampedPageSize + 1);

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

    public async IAsyncEnumerable<UnbalancedVoucherDateRow> StreamVoucherDateRowsAsync(
        string projectId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = database.CreateCommand(connection, projectId,
            ValidationProcedures.UnbalancedVoucherDateSummary("{s}.") + ";");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return new UnbalancedVoucherDateRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3));
        }
    }
}
