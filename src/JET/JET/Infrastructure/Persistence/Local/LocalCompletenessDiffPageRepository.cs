using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 完整性差異 keyset 分頁(本地引擎)。共用 <see cref="ValidationProcedures.CompletenessDiffCte"/>;
/// 排序鍵 account_code ASC、游標展開布林式 <c>AND account_code &gt; @cursor</c>(首頁省略)、
/// limit 由注入的 <see cref="ISqlDialect"/> 出 <c>LIMIT @pageSize</c>。not_in_tb 旗標本地引擎用 GetInt64。
/// </summary>
public sealed class LocalCompletenessDiffPageRepository(ILocalProjectDatabase database)
    : ICompletenessDiffPageRepository
{
    public async Task<PageResult<CompletenessDiffAccount>> GetPageAsync(
        string projectId, int moneyScale, string periodStart, string periodEnd, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var hasCursor = PageCursor.TryDecode(request.Cursor, out var cursorKey);
        var keyset = hasCursor ? "AND account_code > @cursor" : string.Empty;
        if (hasCursor)
        {
            command.AddWithValue("@cursor", cursorKey);
        }

        command.AddWithValue("@pageSize", request.ClampedPageSize + 1);

        command.CommandText =
            ValidationProcedures.CompletenessDiffCte +
            "\nSELECT account_code, account_name, tb_s, gl_s, tb_s - gl_s, not_in_tb " +
            "FROM diff WHERE tb_s <> gl_s " + keyset +
            " ORDER BY account_code " + database.Dialect.LimitClause("@pageSize") + ";";

        var rows = new List<CompletenessDiffAccount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CompletenessDiffAccount(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
                reader.GetInt64(5) != 0));
        }

        var hasMore = rows.Count > request.ClampedPageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var next = hasMore
            ? PageCursor.Encode(rows[^1].AccountCode)
            : null;
        return new PageResult<CompletenessDiffAccount>(rows, next);
    }
}
