using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 完整性差異 keyset 分頁(本地引擎)。共用 <see cref="ValidationProcedures.CompletenessDiffCte"/>,
/// 只列 <c>tb_s &lt;&gt; gl_s</c> 的科目;排序、搜尋與換頁見 <see cref="CompletenessAccountPageQuery"/>。
/// </summary>
public sealed class LocalCompletenessDiffPageRepository(ILocalProjectDatabase database)
    : ICompletenessDiffPageRepository
{
    public async Task<PageResult<CompletenessDiffAccount>> GetPageAsync(
        string projectId, int moneyScale, string periodStart, string periodEnd, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var paging = CompletenessAccountPageQuery.Plan(database.Dialect, request);
        CompletenessAccountPageQuery.Bind(command, paging, request);
        command.CommandText = CompletenessAccountPageQuery.Sql(
            ValidationProcedures.CompletenessDiffCte, differencesOnly: true, paging, database.Dialect);
        return await CompletenessAccountPageQuery.ReadAsync(command, paging, request, cancellationToken);
    }
}
