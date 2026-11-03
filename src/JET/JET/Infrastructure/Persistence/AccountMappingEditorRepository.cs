using System.Data.Common;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>三種 provider 共用同一份科目來源、局部更新及交易失效處理。</summary>
public sealed class AccountMappingEditorRepository : IAccountMappingEditorRepository
{
    public const string EditorSourceName = "JET 科目配對";
    private readonly Func<string, CancellationToken, Task> ensure;
    private readonly Func<string, DbConnection> connect;
    private readonly Func<string, string> prefix;
    private readonly ISqlDialect dialect;

    public AccountMappingEditorRepository(ILocalProjectDatabase database)
    {
        ensure = database.EnsureReadyAsync;
        connect = database.CreateConnection;
        prefix = _ => "";
        dialect = database.Dialect;
    }

    public AccountMappingEditorRepository(SqlServerProjectDatabase database)
    {
        ensure = database.EnsureCreatedAsync;
        connect = id => database.CreateConnection(id);
        prefix = SqlServerProjectSchema.QualifierFor;
        dialect = SqlServerDialect.Instance;
    }

    private static string AccountsCte(string p, ISqlDialect dialect) => AccountMappingPopulationQuery.Cte(p, dialect) + $"""
        , accounts AS (
            SELECT account_number, account_name FROM population
            UNION ALL
            SELECT m.account_number, m.account_name FROM {p}account_mapping m
            WHERE NOT EXISTS (SELECT 1 FROM population d WHERE d.account_number = m.account_number)
        )
        """;

    internal static async Task<DataPreviewResult> PreviewSavedAsync(DbConnection connection, ISqlDialect dialect,
        string prefix, int limit, CancellationToken cancellationToken, int offset = 0)
    {
        long? count = null;
        if (offset == 0)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {prefix}account_mapping;";
            count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }
        await using var select = connection.CreateCommand();
        select.CommandText = $"""
            SELECT m.account_number, m.account_name, COALESCE(t.label, m.standardized_category)
            FROM {prefix}account_mapping m
            LEFT JOIN {prefix}account_category t ON t.category_id = m.category_id
            ORDER BY m.account_number
            """ + " " + (dialect is SqlServerDialect ? "OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY" : "LIMIT @limit OFFSET @offset") + ";";
        select.AddWithValue("@limit", limit);
        select.AddWithValue("@offset", offset);
        var rows = new List<IReadOnlyList<string?>>();
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add([reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)]);
        return new DataPreviewResult(["accountNumber", "accountName", "standardizedCategory"], rows, count, null);
    }

    public async Task<PageResult<AccountMappingEditRow>> GetPageAsync(
        string projectId, PageRequest request, string? search, CancellationToken cancellationToken,
        string? categoryId = null)
    {
        await ensure(projectId, cancellationToken);
        await using var connection = connect(projectId);
        await connection.OpenAsync(cancellationToken);
        var p = prefix(projectId);
        await using var command = connection.CreateCommand();
        var hasCursor = PageCursor.TryDecode(request.Cursor, out var key);
        var source = $"FROM accounts a LEFT JOIN {p}account_mapping m ON m.account_number = a.account_number WHERE 1 = 1"
            + (!string.IsNullOrEmpty(search) ? " AND (" + dialect.ContainsIgnoreCase("a.account_number", "@search")
                + " OR " + dialect.ContainsIgnoreCase("a.account_name", "@search") + ")" : "")
            + (!string.IsNullOrEmpty(categoryId) ? $" AND EXISTS (SELECT 1 FROM {p}account_category_path tp"
                + " WHERE tp.ancestor_id = @categoryId AND tp.descendant_id = m.category_id)" : "");
        command.CommandText = AccountsCte(p, dialect) + " SELECT a.account_number, a.account_name, m.category_id "
            + source + (hasCursor ? " AND a.account_number > @cursor" : "")
            + " ORDER BY a.account_number " + dialect.LimitClause("@limit") + ";";
        if (hasCursor) command.AddWithValue("@cursor", key);
        if (!string.IsNullOrEmpty(search)) command.AddWithValue("@search", search.ToUpperInvariant());
        if (!string.IsNullOrEmpty(categoryId)) command.AddWithValue("@categoryId", categoryId);
        command.AddWithValue("@limit", request.ClampedPageSize + 1);
        var count = dialect is SqlServerDialect ? "COUNT_BIG(*)" : "COUNT(*)";
        var totalCount = await PageTotalCount.ReadAsync(command, request,
            AccountsCte(p, dialect) + $" SELECT {count} " + source, cancellationToken);
        var rows = new List<AccountMappingEditRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        var more = rows.Count > request.ClampedPageSize;
        if (more) rows.RemoveAt(rows.Count - 1);
        return new(rows, more ? PageCursor.Encode(rows[^1].AccountNumber) : null, totalCount);
    }

    public async Task SaveAsync(string projectId, IReadOnlyList<AccountMappingChange> changes,
        CancellationToken cancellationToken)
    {
        if (changes.Count is < 1 or > 500 || changes.Select(x => x.AccountNumber).Distinct(StringComparer.Ordinal).Count() != changes.Count)
            throw new JetActionException(JetErrorCodes.InvalidPayload, "科目分類筆數不符或科目重複。");
        await ensure(projectId, cancellationToken);
        await using var connection = connect(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        var p = prefix(projectId);
        var batchId = Guid.NewGuid().ToString("N");
        var time = DateTimeOffset.UtcNow.ToString("O");
        var accounts = new Dictionary<string, string?>(StringComparer.Ordinal);
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        // 有界參數一次查完；不能為每個科目重新彙總整份 GL/TB。
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = tx;
            var parameters = changes.Select((change, index) =>
            {
                var parameter = "@code" + index;
                lookup.AddWithValue(parameter, change.AccountNumber);
                return parameter;
            }).ToArray();
            lookup.CommandText = AccountsCte(p, dialect) + " SELECT account_number, account_name FROM accounts WHERE account_number IN ("
                + string.Join(",", parameters) + ");";
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                accounts.Add(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
        }
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = tx;
            var parameters = changes.Select(x => x.CategoryId).Distinct(StringComparer.Ordinal).Select((id, index) =>
            {
                var parameter = "@category" + index;
                lookup.AddWithValue(parameter, id);
                return parameter;
            }).ToArray();
            lookup.CommandText = $"SELECT category_id, semantic_role FROM {p}account_category WHERE category_id IN ("
                + string.Join(",", parameters) + ");";
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) roles.Add(reader.GetString(0), reader.GetString(1));
        }
        if (changes.Any(x => !accounts.ContainsKey(x.AccountNumber) || !roles.ContainsKey(x.CategoryId)))
            throw new JetActionException(JetErrorCodes.InvalidPayload, "科目或分類已變更，請重新載入科目清單後再儲存。");
        await using (var batch = connection.CreateCommand())
        {
            batch.Transaction = tx;
            batch.CommandText = $"""
                INSERT INTO {p}import_batch
                    (batch_id, dataset_kind, source_file_path, source_file_name, imported_utc, row_count, columns_json)
                VALUES (@batch, @kind, @name, @name, @time, @count, @columns);
                INSERT INTO {p}import_batch_source
                    (batch_id, source_no, source_file_path, source_file_name, row_count, imported_utc)
                VALUES (@batch, 1, @name, @name, @count, @time);
                """;
            batch.AddWithValue("@batch", batchId);
            batch.AddWithValue("@kind", DatasetKind.AccountMapping.ToStorageName());
            batch.AddWithValue("@name", EditorSourceName);
            batch.AddWithValue("@time", time);
            batch.AddWithValue("@count", changes.Count);
            batch.AddWithValue("@columns", JsonSerializer.Serialize(new[] { "科目代號", "科目名稱", "分類" }));
            await batch.ExecuteNonQueryAsync(cancellationToken);
        }
        var rowNumber = 0;
        foreach (var change in changes)
        {
            var name = accounts[change.AccountNumber];
            var role = roles[change.CategoryId];
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = $"""
                INSERT INTO {p}pbc_account_mapping (batch_id, row_number, source_no, source_row_number, row_json)
                VALUES (@batch, @row, 1, @row, @json);
                DELETE FROM {p}account_mapping WHERE account_number = @code;
                INSERT INTO {p}account_mapping
                    (batch_id, source_row_number, account_number, account_name, standardized_category, category_id, classification_explicit)
                VALUES (@batch, @row, @code, @name, @legacy, @category, 1);
                """;
            command.AddWithValue("@batch", batchId);
            command.AddWithValue("@row", ++rowNumber);
            command.AddWithValue("@code", change.AccountNumber);
            command.AddWithValue("@name", (object?)name ?? DBNull.Value);
            command.AddWithValue("@category", change.CategoryId);
            command.AddWithValue("@legacy", AccountCategoryCatalog.LegacyLabelForSemanticRole(role));
            command.AddWithValue("@json", JsonSerializer.Serialize(new Dictionary<string, string?>
                { ["科目代號"] = change.AccountNumber, ["科目名稱"] = name, ["分類"] = change.CategoryId }));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await ResultReset.ClearWithinAsync(connection, tx, cancellationToken, AuditMutation.AccountMapping, p);
        await tx.CommitAsync(cancellationToken);
    }
}
