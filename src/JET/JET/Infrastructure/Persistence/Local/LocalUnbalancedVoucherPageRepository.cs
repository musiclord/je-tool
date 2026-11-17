using System.Data;
using System.Runtime.CompilerServices;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 分頁讀出借貸不平傳票裡每一筆納入測試分錄的 entry_id，依 entry_id 升冪，下一頁從上一頁最後一個 entry_id 之後接著讀。
/// 不平的傳票用 <see cref="ValidationProcedures.UnbalancedCore"/> 找出，和 validate.run 是同一段 SQL。
/// 每一頁都會重新彙總全部傳票的借貸；V_Report 6 要讀完全部分錄，改用 <see cref="IUnbalancedVoucherEntryStreamRepository"/>
/// 一個查詢讀完，讀出的分錄與順序和逐頁讀完相同。
/// </summary>
public sealed class LocalUnbalancedVoucherPageRepository(ILocalProjectDatabase database)
    : IUnbalancedVoucherPageRepository,
      IUnbalancedVoucherEntryStreamRepository
{
    public async Task<PageResult<long>> GetEntryIdsPageAsync(
        string projectId,
        string periodStart,
        string periodEnd,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var hasCursor = PageCursor.TryDecode(request.Cursor, out var cursorKey);
        var keyset = hasCursor ? "AND g.entry_id > @cursor" : string.Empty;
        if (hasCursor)
        {
            command.AddWithValue("@cursor", long.Parse(cursorKey));
        }

        command.AddWithValue("@pageSize", request.ClampedPageSize + 1);
        command.CommandText =
            "SELECT g.entry_id " +
            ValidationProcedures.UnbalancedDetailCore(string.Empty, keyset) +
            " ORDER BY g.entry_id " + database.Dialect.LimitClause("@pageSize") + ";";

        var rows = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(reader.GetInt64(0));
        }

        var hasMore = rows.Count > request.ClampedPageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var next = hasMore
            ? PageCursor.Encode(rows[^1].ToString())
            : null;
        return new PageResult<long>(rows, next);
    }

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
