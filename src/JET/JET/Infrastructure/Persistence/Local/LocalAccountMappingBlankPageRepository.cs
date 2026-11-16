using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 分頁讀出分類欄留白的科目。依 account_number 升冪排序，
/// 下一頁從上一頁最後一個科目編號之後接著讀（<c>account_number &gt; @cursor</c>）。
/// </summary>
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
            "SELECT account_number, account_name FROM account_mapping "
            + "WHERE classification_explicit = 0 " + (hasCursor ? "AND account_number > @cursor " : string.Empty)
            + "ORDER BY account_number " + database.Dialect.LimitClause("@pageSize") + ";";

        var totalCount = await PageTotalCount.ReadAsync(command, request,
            "SELECT COUNT(*) FROM account_mapping WHERE classification_explicit = 0;", cancellationToken);
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

        return new PageResult<AccountMappingBlankAccount>(rows, hasMore ? PageCursor.Encode(rows[^1].AccountNumber) : null, totalCount);
    }
}
