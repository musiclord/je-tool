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

        var hasCursor = PageCursor.TryDecode(request.Cursor, out var cursorKey);
        var keyset = hasCursor ? " AND document_number > @cursor" : string.Empty;

        // 母體核心走 ValidationProcedures.UnbalancedCore(有效分錄 + 游標 keyset),與計數端同口徑。
        await using var command = database.CreateCommand(connection, projectId,
            "SELECT document_number, " +
            "COALESCE(SUM(debit_amount_scaled), 0), " +
            "COALESCE(SUM(credit_amount_scaled), 0), " +
            "COALESCE(SUM(amount_scaled), 0) " +
            ValidationProcedures.UnbalancedCore("{s}.", keyset) +
            " ORDER BY document_number " + Dialect.LimitClause("@pageSize") + ";");
        if (hasCursor)
        {
            command.Parameters.AddWithValue("@cursor", cursorKey);
        }

        command.Parameters.AddWithValue("@pageSize", request.ClampedPageSize + 1);

        var rows = new List<UnbalancedDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new UnbalancedDocument(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
        }

        var hasMore = rows.Count > request.ClampedPageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var next = hasMore
            ? PageCursor.Encode(rows[^1].DocumentNumber ?? string.Empty)
            : null;
        return new PageResult<UnbalancedDocument>(rows, next);
    }
}
