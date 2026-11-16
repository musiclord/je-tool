using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 分頁讀出借貸不平傳票裡每一筆納入測試分錄的 entry_id，依 entry_id 升冪，下一頁從上一頁最後一個 entry_id 之後接著讀。
/// 不平的傳票用 <see cref="ValidationProcedures.UnbalancedCore"/> 找出，和 validate.run 是同一段 SQL。
/// 寫報告的程式再用一頁的 entry_id 去讀匯入暫存表的原始列，不必把整份 GL 讀進記憶體。
/// </summary>
public sealed class LocalUnbalancedVoucherPageRepository(ILocalProjectDatabase database)
    : IUnbalancedVoucherPageRepository
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
}
