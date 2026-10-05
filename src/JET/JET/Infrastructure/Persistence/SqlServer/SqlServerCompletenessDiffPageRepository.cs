using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 完整性差異 keyset 分頁(SQL Server,鏡像 <see cref="LocalCompletenessDiffPageRepository"/>)。
/// 共用 <see cref="ValidationProcedures.CompletenessDiffCte"/>,只列 <c>tb_s &lt;&gt; gl_s</c> 的科目;
/// 排序、搜尋與換頁見 <see cref="CompletenessAccountPageQuery"/>,limit 由 <see cref="SqlServerDialect"/> 出
/// <c>OFFSET 0 ROWS FETCH NEXT @pageSize ROWS ONLY</c>。
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

        var paging = CompletenessAccountPageQuery.Plan(Dialect, request);

        // ValidationProcedures.CompletenessDiffCteFor 把共用 CTE 內的 target_gl_entry/target_tb_balance 前綴專案 schema。
        await using var command = database.CreateCommand(connection, projectId,
            CompletenessAccountPageQuery.Sql(
                ValidationProcedures.CompletenessDiffCteFor(SqlServerProjectSchema.QualifierFor(projectId)),
                differencesOnly: true, paging, Dialect));
        CompletenessAccountPageQuery.Bind(command, paging, request);
        return await CompletenessAccountPageQuery.ReadAsync(command, paging, request, cancellationToken);
    }
}
