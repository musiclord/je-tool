using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>SQL Server 鏡射本地 provider 的借貸不平傳票完整 GL 分錄 keyset 分頁。</summary>
public sealed class SqlServerUnbalancedGlEntryPageRepository(SqlServerProjectDatabase database)
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

        var hasCursor = PageCursor.TryDecode(request.Cursor, out var cursorKey);
        var keyset = hasCursor ? "AND g.entry_id > @cursor" : string.Empty;
        await using var command = database.CreateCommand(
            connection,
            projectId,
            "SELECT g.entry_id " +
            ValidationProcedures.UnbalancedDetailCore("{s}.", keyset) +
            " ORDER BY g.entry_id " + SqlServerDialect.Instance.LimitClause("@pageSize") + ";");

        if (hasCursor)
        {
            command.Parameters.AddWithValue("@cursor", long.Parse(cursorKey));
        }
        command.Parameters.AddWithValue("@pageSize", request.ClampedPageSize + 1);

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
