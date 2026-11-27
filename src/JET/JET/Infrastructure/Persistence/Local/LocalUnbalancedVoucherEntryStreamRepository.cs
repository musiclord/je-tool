using System.Data;
using System.Runtime.CompilerServices;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 資料驗證報告 V_Report 6 讀借貸不平傳票裡每一筆納入測試的分錄：一個查詢讀完全部 entry_id，依 entry_id 升冪。
/// 不平的傳票用 <see cref="ValidationProcedures.UnbalancedCore"/> 找出，和 validate.run 是同一段 SQL。
/// </summary>
public sealed class LocalUnbalancedVoucherEntryStreamRepository(ILocalProjectDatabase database)
    : IUnbalancedVoucherEntryStreamRepository
{
    async IAsyncEnumerable<long> IUnbalancedVoucherEntryStreamRepository.StreamEntryIdsAsync(
        string projectId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT g.entry_id " +
            ValidationProcedures.UnbalancedDetailCore() +
            " ORDER BY g.entry_id;";
        command.CommandTimeout = 0;

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return reader.GetInt64(0);
        }
    }
}
