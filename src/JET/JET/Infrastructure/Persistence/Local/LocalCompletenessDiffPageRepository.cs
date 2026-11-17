using System.Runtime.CompilerServices;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 分頁讀出完整性測試有差異的科目。用的是 <see cref="ValidationProcedures.CompletenessDiffCte"/>，
/// 只列 <c>tb_s &lt;&gt; gl_s</c> 的科目；排序、搜尋與換頁見 <see cref="CompletenessAccountPageQuery"/>。
/// 正式輸出讀完整份清單時改用 <see cref="ICompletenessDiffStreamRepository"/>，一個查詢讀完，彙總只算一次。
/// </summary>
public sealed class LocalCompletenessDiffPageRepository(ILocalProjectDatabase database)
    : ICompletenessDiffPageRepository,
      ICompletenessDiffStreamRepository
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
        return await CompletenessAccountPageQuery.ReadAsync(command, paging, request, cancellationToken, database.Dialect, ValidationProcedures.CompletenessDiffCte);
    }

    async IAsyncEnumerable<CompletenessDiffAccount> ICompletenessDiffStreamRepository.StreamAsync(
        string projectId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = CompletenessAccountPageQuery.StreamSql(
            ValidationProcedures.CompletenessDiffCte, differencesOnly: true);
        await foreach (var account in CompletenessAccountPageQuery.StreamAsync(command, cancellationToken))
        {
            yield return account;
        }
    }
}
