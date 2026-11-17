using System.Runtime.CompilerServices;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 完整性全科目 keyset 分頁，包含差異為 0 的科目。
/// 與差異頁共用 <see cref="ValidationProcedures.CompletenessDiffCteFor"/>，但科目名稱只取 TB，
/// TB 名稱空白或 GL 有、TB 無時不以 GL 名稱補值。排序與換頁見 <see cref="CompletenessAccountPageQuery"/>：
/// 空白科目在最前，換頁只比較固定順序的序號。
/// 正式輸出讀完整份清單時改用 <see cref="ICompletenessAccountStreamRepository"/>，一個查詢讀完，彙總只算一次。
/// </summary>
public sealed class LocalCompletenessAccountPageRepository(ILocalProjectDatabase database)
    : ICompletenessAccountPageRepository,
      ICompletenessAccountStreamRepository
{
    private static readonly string AccountCte = ValidationProcedures.CompletenessDiffCteFor("", useTbAccountNames: true);

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
            AccountCte, differencesOnly: false, paging, database.Dialect);
        return await CompletenessAccountPageQuery.ReadAsync(command, paging, request, cancellationToken, database.Dialect, AccountCte);
    }

    async IAsyncEnumerable<CompletenessDiffAccount> ICompletenessAccountStreamRepository.StreamAsync(
        string projectId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = CompletenessAccountPageQuery.StreamSql(AccountCte, differencesOnly: false);
        await foreach (var account in CompletenessAccountPageQuery.StreamAsync(command, cancellationToken))
        {
            yield return account;
        }
    }
}
