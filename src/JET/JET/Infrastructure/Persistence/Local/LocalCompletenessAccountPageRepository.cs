using System.Runtime.CompilerServices;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 完整性全科目 keyset 分頁，包含差異為 0 的科目。
/// 與差異頁共用 <see cref="ValidationProcedures.CompletenessDiffCte"/>，科目名稱的取法也相同：
/// 先取 TB 的名稱，TB 沒有名稱或科目只在 GL 時取 GL 的名稱。排序與換頁見 <see cref="CompletenessAccountPageQuery"/>：
/// 空白科目在最前，換頁只比較固定順序的序號。
/// 正式輸出讀完整份清單時改用 <see cref="ICompletenessAccountStreamRepository"/>，一個查詢讀完，彙總只算一次。
/// </summary>
public sealed class LocalCompletenessAccountPageRepository(ILocalProjectDatabase database)
    : ICompletenessAccountPageRepository,
      ICompletenessAccountStreamRepository
{
    private readonly string _accountCte = ValidationProcedures.CompletenessDiffCte(database.Dialect);

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
            _accountCte, differencesOnly: false, paging, database.Dialect);
        return await CompletenessAccountPageQuery.ReadAsync(command, paging, request, cancellationToken, database.Dialect, _accountCte);
    }

    async IAsyncEnumerable<CompletenessDiffAccount> ICompletenessAccountStreamRepository.StreamAsync(
        string projectId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = CompletenessAccountPageQuery.StreamSql(_accountCte, differencesOnly: false);
        await foreach (var account in CompletenessAccountPageQuery.StreamAsync(command, cancellationToken))
        {
            yield return account;
        }
    }
}
