using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 完整性全科目 keyset 分頁(SQL Server,鏡像 <see cref="LocalCompletenessAccountPageRepository"/>)。
/// 共用 <see cref="ValidationProcedures.CompletenessDiffCte"/>;**不加 <c>WHERE tb_s &lt;&gt; gl_s</c>**(step1 全科目)。
/// 排序鍵 account_code ASC、游標展開布林式、limit 由 <see cref="SqlServerDialect"/> 出
/// <c>OFFSET 0 ROWS FETCH NEXT @pageSize ROWS ONLY</c>。not_in_tb 旗標 SQL Server 用 GetInt32。
/// </summary>
public sealed class SqlServerCompletenessAccountPageRepository(SqlServerProjectDatabase database)
    : ICompletenessAccountPageRepository
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    public async Task<PageResult<CompletenessDiffAccount>> GetPageAsync(
        string projectId, int moneyScale, string periodStart, string periodEnd, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        var hasCursor = PageCursor.TryDecode(request.Cursor, out var cursorKey);
        var keyset = hasCursor ? "WHERE account_code > @cursor" : string.Empty;

        // ValidationProcedures.CompletenessDiffCteFor 把共用 CTE 內的 target_gl_entry/target_tb_balance 前綴專案 schema。
        await using var command = database.CreateCommand(connection, projectId,
            ValidationProcedures.CompletenessDiffCteFor(SqlServerProjectSchema.QualifierFor(projectId)) +
            "\nSELECT account_code, account_name, tb_s, gl_s, tb_s - gl_s, not_in_tb " +
            "FROM diff " + keyset +
            " ORDER BY account_code " + Dialect.LimitClause("@pageSize") + ";");
        if (hasCursor)
        {
            command.Parameters.AddWithValue("@cursor", cursorKey);
        }

        command.Parameters.AddWithValue("@pageSize", request.ClampedPageSize + 1);

        var rows = new List<CompletenessDiffAccount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CompletenessDiffAccount(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
                reader.GetInt32(5) != 0));
        }

        var hasMore = rows.Count > request.ClampedPageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var next = hasMore
            ? PageCursor.Encode(rows[^1].AccountCode)
            : null;
        return new PageResult<CompletenessDiffAccount>(rows, next);
    }
}
