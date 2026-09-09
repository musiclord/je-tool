using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 完整性差異 keyset 分頁(SQL Server,鏡像 <see cref="LocalCompletenessDiffPageRepository"/>)。
/// 共用 <see cref="ValidationProcedures.CompletenessDiffCte"/>;排序鍵 account_code ASC、游標展開布林式、
/// limit 由 <see cref="SqlServerDialect"/> 出 <c>OFFSET 0 ROWS FETCH NEXT @pageSize ROWS ONLY</c>
/// (ORDER BY account_code 已具備)。not_in_tb 旗標 SQL Server 用 GetInt32。
/// </summary>
public sealed class SqlServerCompletenessDiffPageRepository(SqlServerProjectDatabase database)
    : ICompletenessDiffPageRepository
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    public async Task<PageResult<CompletenessDiffAccount>> GetPageAsync(
        string projectId, int moneyScale, string periodStart, string periodEnd, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        var paging = KeysetPaging.Plan(Dialect, request, ResultPageSorting.CompletenessDiff);

        // ValidationProcedures.CompletenessDiffCteFor 把共用 CTE 內的 target_gl_entry/target_tb_balance 前綴專案 schema。
        await using var command = database.CreateCommand(connection, projectId,
            ValidationProcedures.CompletenessDiffCteFor(SqlServerProjectSchema.QualifierFor(projectId)) +
            "\nSELECT account_code, account_name, tb_s, gl_s, tb_s - gl_s, not_in_tb" + paging.SelectSuffix + " " +
            "FROM diff WHERE tb_s <> gl_s" + paging.Predicate + " " +
            paging.OrderBy + " " + Dialect.LimitClause("@pageSize") + ";");
        foreach (var parameter in paging.Parameters)
        {
            command.Parameters.AddWithValue(parameter.Key, parameter.Value);
        }

        command.Parameters.AddWithValue("@pageSize", request.ClampedPageSize + 1);

        var buffer = new KeysetPageBuffer<CompletenessDiffAccount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            buffer.Add(
                new CompletenessDiffAccount(
                    reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
                    reader.GetInt32(5) != 0),
                paging.HasSort ? reader.GetValue(6) : null);
        }

        return buffer.ToPage(request, paging, static row => row.AccountCode);
    }
}
