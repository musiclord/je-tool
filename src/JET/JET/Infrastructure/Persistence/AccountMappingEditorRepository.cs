using System.Data.Common;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>科目配對編輯的科目來源、局部更新及交易失效處理。</summary>
public sealed class AccountMappingEditorRepository : IAccountMappingEditorRepository
{
    public const string EditorSourceName = "JET 科目配對";
    private readonly Func<string, CancellationToken, Task> _ensure;
    private readonly Func<string, DbConnection> _connect;
    private readonly Func<string, string> _prefix;
    private readonly ISqlDialect _dialect;

    public AccountMappingEditorRepository(ILocalProjectDatabase database)
    {
        _ensure = database.EnsureReadyAsync;
        _connect = database.CreateConnection;
        _prefix = _ => "";
        _dialect = database.Dialect;
    }

    /// <param name="useTbAccountNames">名稱的取法照 <see cref="AccountMappingPopulationQuery.Cte"/> 的同名參數。</param>
    private static string AccountsCte(string p, ISqlDialect dialect, bool useTbAccountNames = false) =>
        AccountMappingPopulationQuery.Cte(p, dialect, useTbAccountNames) + $"""
        , accounts AS (
            SELECT account_number, account_name FROM population
            UNION ALL
            SELECT m.account_number, m.account_name FROM {p}account_mapping m
            WHERE NOT EXISTS (SELECT 1 FROM population d WHERE d.account_number = m.account_number)
        )
        """;

    /// <summary>
    /// 已儲存的科目配對有名稱時用這個名稱，沒有名稱（NULL、空字串或只有空白字元）時用科目母體的名稱。
    /// 使用者上傳的檔案寫了名稱，就一直照那個名稱；檔案沒有名稱或科目還沒存過時，才看試算表與總帳。
    /// </summary>
    private static string SavedNameOrPopulationName(ISqlDialect dialect, string savedName, string populationName) =>
        $"CASE WHEN {SqlBlankText.IsNotBlank(dialect, savedName)} THEN {savedName} ELSE {populationName} END";

    /// <summary>
    /// 資料預覽的已儲存科目配對，一次只讀一頁，分類照存好的值。
    /// 名稱欄先顯示已儲存的名稱；沒有存名稱時照全科目表的取法，先取 TB 的名稱，TB 沒有名稱時取 GL 的名稱，兩邊都沒有才留白。
    /// </summary>
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
        select.CommandText = AccountMappingPopulationQuery.Cte(prefix, dialect) + $"""

            SELECT m.account_number, {SavedNameOrPopulationName(dialect, "m.account_name", "p.account_name")},
                   COALESCE(t.label, m.standardized_category)
            FROM {prefix}account_mapping m
            LEFT JOIN population p ON p.account_number = m.account_number
            LEFT JOIN {prefix}account_category t ON t.category_id = m.category_id
            ORDER BY m.account_number
            """ + " LIMIT @limit OFFSET @offset;";
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
        await _ensure(projectId, cancellationToken);
        await using var connection = _connect(projectId);
        await connection.OpenAsync(cancellationToken);
        var p = _prefix(projectId);
        await using var command = connection.CreateCommand();
        var hasCursor = PageCursor.TryDecode(request.Cursor, out var key);
        var source = $"FROM accounts a LEFT JOIN {p}account_mapping m ON m.account_number = a.account_number WHERE 1 = 1"
            + (!string.IsNullOrEmpty(search) ? " AND (" + _dialect.ContainsIgnoreCase("a.account_number", "@search")
                + " OR " + _dialect.ContainsIgnoreCase("a.account_name", "@search") + ")" : "")
            + (!string.IsNullOrEmpty(categoryId) ? $" AND EXISTS (SELECT 1 FROM {p}account_category_path tp"
                + " WHERE tp.ancestor_id = @categoryId AND tp.descendant_id = m.category_id)" : "");
        command.CommandText = AccountsCte(p, _dialect) + " SELECT a.account_number, a.account_name, m.category_id "
            + source + (hasCursor ? " AND a.account_number > @cursor" : "")
            + " ORDER BY a.account_number " + _dialect.LimitClause("@limit") + ";";
        if (hasCursor) command.AddWithValue("@cursor", key);
        if (!string.IsNullOrEmpty(search)) command.AddWithValue("@search", SourceText.ComparisonKey(search));
        if (!string.IsNullOrEmpty(categoryId)) command.AddWithValue("@categoryId", categoryId);
        command.AddWithValue("@limit", request.ClampedPageSize + 1);
        var totalCount = await PageTotalCount.ReadAsync(command, request,
            AccountsCte(p, _dialect) + " SELECT COUNT(*) " + source, cancellationToken);
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
        await _ensure(projectId, cancellationToken);
        await using var connection = _connect(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        var p = _prefix(projectId);
        var batchId = Guid.NewGuid().ToString("N");
        var time = DateTimeOffset.UtcNow.ToString("O");
        var accounts = new Dictionary<string, string?>(StringComparer.Ordinal);
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        // 這次要改的科目最多 500 個，全部當成參數用一句查詢查完；不可以每個科目各查一次，那樣每次都要重新彙總整份 GL 與 TB。
        // 存下的名稱和科目配對範本匯回時相同：範本只寫 TB 的名稱，所以這裡也只取 TB 的名稱，TB 沒有名稱或科目只在 GL 時不存名稱，
        // 底稿對這種科目和範本一樣寫「Not in TB」。畫面清單顯示的 GL 名稱不存進來。已儲存的名稱有內容時保留，
        // 使用者在上傳的檔案寫的名稱不因為在畫面改分類而消失。
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = tx;
            var parameters = changes.Select((change, index) =>
            {
                var parameter = "@code" + index;
                lookup.AddWithValue(parameter, change.AccountNumber);
                return parameter;
            }).ToArray();
            lookup.CommandText = AccountsCte(p, _dialect, useTbAccountNames: true)
                + " SELECT a.account_number, " + SavedNameOrPopulationName(_dialect, "m.account_name", "a.account_name")
                + $" FROM accounts a LEFT JOIN {p}account_mapping m ON m.account_number = a.account_number"
                + " WHERE a.account_number IN (" + string.Join(",", parameters) + ");";
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
