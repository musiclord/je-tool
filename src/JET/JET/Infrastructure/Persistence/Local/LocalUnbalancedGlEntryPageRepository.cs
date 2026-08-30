using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 本地 provider 的借貸不平傳票完整 GL 分錄 keyset 分頁。先以與 validate.run 同源的
/// <see cref="ValidationProcedures.UnbalancedCore"/> 找出不平傳票，再回傳其有效 entry_id；
/// writer 以一頁 ids 回取 staging 原始列，避免把整份 GL 載入記憶體。
/// </summary>
public sealed class LocalUnbalancedGlEntryPageRepository(ILocalProjectDatabase database)
    : IUnbalancedGlEntryPageRepository
{
    public async Task<PageResult<long>> GetEntryIdsPageAsync(
        string projectId,
        string periodStart,
        string periodEnd,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
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
