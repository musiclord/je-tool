using JET.Domain;

namespace JET.Infrastructure;

/// <summary>分類欄留白科目的 keyset 分頁（SQL Server）：與本地引擎同一份排序鍵與游標語意。</summary>
public sealed class SqlServerAccountMappingBlankPageRepository(SqlServerProjectDatabase database)
    : IAccountMappingBlankPageRepository
{
    public async Task<PageResult<AccountMappingBlankAccount>> GetPageAsync(
        string projectId, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        var hasCursor = PageCursor.TryDecode(request.Cursor, out var cursorKey);
        await using var command = database.CreateCommand(connection, projectId,
            "SELECT account_code, account_name FROM {s}.target_account_mapping "
            + "WHERE classification_explicit = 0 " + (hasCursor ? "AND account_code > @cursor " : string.Empty)
            + "ORDER BY account_code " + SqlServerDialect.Instance.LimitClause("@pageSize") + ";");
        if (hasCursor)
        {
            command.Parameters.AddWithValue("@cursor", cursorKey);
        }

        command.Parameters.AddWithValue("@pageSize", request.ClampedPageSize + 1);

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
