using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 完整性全科目 keyset 分頁(本地引擎)。鏡射 <see cref="LocalCompletenessDiffPageRepository"/>,
/// 共用 <see cref="ValidationProcedures.CompletenessDiffCte"/>;**差別只在不加 <c>WHERE tb_s &lt;&gt; gl_s</c>**
/// (step1 全科目,含差異為 0)。排序與換頁見 <see cref="CompletenessAccountPageQuery"/>:
/// 空白科目在最前,換頁比較固定順序的序號,兩個本地引擎得到同一個順序。
/// </summary>
public sealed class LocalCompletenessAccountPageRepository(ILocalProjectDatabase database)
    : ICompletenessAccountPageRepository
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
            ValidationProcedures.CompletenessDiffCte, differencesOnly: false, paging, database.Dialect);
        return await CompletenessAccountPageQuery.ReadAsync(command, paging, request, cancellationToken);
    }
}
