using JET.Domain;

namespace JET.Infrastructure;

/// <summary>分類欄留白科目的 keyset 分頁（本地引擎）：排序鍵 account_code ASC，游標為展開布林式。</summary>
public sealed class LocalAccountMappingBlankPageRepository(ILocalProjectDatabase database)
    : IAccountMappingBlankPageRepository
{
    public async Task<PageResult<AccountMappingBlankAccount>> GetPageAsync(
        string projectId, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var hasCursor = PageCursor.TryDecode(request.Cursor, out var cursorKey);
        if (hasCursor)
        {
            command.AddWithValue("@cursor", cursorKey);
        }

        command.AddWithValue("@pageSize", request.ClampedPageSize + 1);
        command.CommandText =
            "SELECT account_code, account_name FROM target_account_mapping "
            + "WHERE classification_explicit = 0 " + (hasCursor ? "AND account_code > @cursor " : string.Empty)
            + "ORDER BY account_code " + database.Dialect.LimitClause("@pageSize") + ";";

        var rows = new List<AccountMappingBlankAccount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AccountMappingBlankAccount(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        var hasMore = rows.Count > request.ClampedPageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return new PageResult<AccountMappingBlankAccount>(rows, hasMore ? PageCursor.Encode(rows[^1].AccountCode) : null);
    }
}
