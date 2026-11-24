using System.Data;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 科目配對的匯入與匯入狀態查詢（import.accountMapping.fromFile）。
/// 和 GL、TB 匯入不同的地方：檔案格式固定三欄，沒有欄位配對這一步。所以寫匯入暫存表 <c>pbc_account_mapping</c>、
/// 轉寫進 <c>account_mapping</c> 與新增自訂分類都在同一個交易裡完成，任何一列讀不出來就整批 rollback。
/// 只支援取代匯入：科目配對是整份替換的設定檔，不合併多個來源。
/// </summary>
public sealed class LocalAccountMappingRepository(ILocalProjectDatabase database)
    : IAccountMappingStore, IAccountMappingImportPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    public Task<AccountMappingImportResult> ImportAsync(
        string projectId,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken) =>
        ImportCoreAsync(
            projectId,
            source,
            columns,
            projection: null,
            rows: rows,
            cancellationToken: cancellationToken);

    Task<AccountMappingImportResult> IAccountMappingImportPersistence.ImportAsync(
        string projectId,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        AccountMappingProjection projection,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken) =>
        ImportCoreAsync(
            projectId,
            source,
            columns,
            projection,
            rows,
            cancellationToken);

    private async Task<AccountMappingImportResult> ImportCoreAsync(
        string projectId,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        AccountMappingProjection? projection,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken)
    {
        source = ImportSourceFileName.Normalize(source);
        await database.EnsureReadyAsync(projectId, cancellationToken);

        var batchId = Guid.NewGuid().ToString("N");
        var importedUtc = DateTimeOffset.UtcNow;
        var kindName = DatasetKind.AccountMapping.ToStorageName();

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // 呼叫端沒有先準備投影時，用案件目前的科目分類解析分類欄。
        projection ??= JetAuditProgram.PrepareAccountMappingProjection(
            columns,
            await LocalAccountCategoryStore.ReadWithinAsync(connection, transaction, cancellationToken));

        // 取代匯入：在同一個交易裡清掉舊批次、匯入暫存表 pbc_account_mapping 與 account_mapping。
        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText =
                """
                DELETE FROM pbc_account_mapping
                WHERE batch_id IN (SELECT batch_id FROM import_batch WHERE dataset_kind = @kind);
                DELETE FROM import_batch_source
                WHERE batch_id IN (SELECT batch_id FROM import_batch WHERE dataset_kind = @kind);
                DELETE FROM import_batch WHERE dataset_kind = @kind;
                DELETE FROM account_mapping;
                """;
            cleanup.AddWithValue("@kind", kindName);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        // 科目配對換版,未預期借貸組合等規則結果即失效。
        await ResultReset.ClearWithinAsync(
            connection,
            transaction,
            cancellationToken,
            AuditMutation.AccountMapping);

        await using (var insertBatch = connection.CreateCommand())
        {
            insertBatch.Transaction = transaction;
            insertBatch.CommandText =
                """
                INSERT INTO import_batch
                    (batch_id, dataset_kind, source_file_path, source_file_name, imported_utc, row_count, columns_json)
                VALUES (@batchId, @kind, @filePath, @fileName, @importedUtc, 0, @columnsJson);
                INSERT INTO import_batch_source
                    (batch_id, source_no, source_file_path, source_file_name, sheet_name, encoding, delimiter, row_count, imported_utc)
                VALUES (@batchId, 1, @filePath, @fileName, @sheetName, @encoding, @delimiter, 0, @importedUtc);
                """;
            insertBatch.AddWithValue("@batchId", batchId);
            insertBatch.AddWithValue("@kind", kindName);
            // source_file_path 這一欄的名稱帶 path，存的只是可攜的檔名，不是本機完整路徑。
            insertBatch.AddWithValue("@filePath", source.FileName);
            insertBatch.AddWithValue("@fileName", source.FileName);
            insertBatch.AddWithValue("@sheetName", (object?)source.SheetName ?? DBNull.Value);
            insertBatch.AddWithValue("@encoding", (object?)source.EncodingName ?? DBNull.Value);
            insertBatch.AddWithValue("@delimiter", (object?)source.Delimiter ?? DBNull.Value);
            insertBatch.AddWithValue("@importedUtc", importedUtc.ToString("O"));
            insertBatch.AddWithValue("@columnsJson", JsonSerializer.Serialize(columns, JsonOptions));
            await insertBatch.ExecuteNonQueryAsync(cancellationToken);
        }

        // 一邊讀來源一邊寫匯入暫存表，同時把每一列交給 projection 整理；同一個科目編號出現多次時，後面的列蓋掉前面的列。
        var rowCount = 0;

        await using (var insertRow = connection.CreateCommand())
        {
            insertRow.Transaction = transaction;
            insertRow.CommandText =
                """
                INSERT INTO pbc_account_mapping
                    (batch_id, row_number, source_no, source_row_number, row_json)
                VALUES (@batchId, @rowNumber, 1, @sourceRowNumber, @rowJson);
                """;
            insertRow.AddWithValue("@batchId", batchId);
            var rowNumberParam = insertRow.AddParameter("@rowNumber", DbType.Int64);
            var sourceRowParam = insertRow.AddParameter("@sourceRowNumber", DbType.Int64);
            var rowJsonParam = insertRow.AddParameter("@rowJson", DbType.String);

            await foreach (var row in rows.WithCancellation(cancellationToken))
            {
                rowNumberParam.Value = row.SourceRowNumber;
                sourceRowParam.Value = row.SourceRowNumber;
                rowJsonParam.Value = JsonSerializer.Serialize(row.Values, JsonOptions);
                await insertRow.ExecuteNonQueryAsync(cancellationToken);
                rowCount++;
                projection.Observe(row);
            }
        }

        IReadOnlyList<AccountMappingRow> projected;
        try
        {
            projected = projection.Complete(
                rowCount,
                source.FileName);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        // 匯入檔帶來案件還沒有的分類名稱時，新分類和科目配對一起寫入，匯入失敗就一起回復。
        // 科目配對匯入本身已經清除預篩選、篩選命中與篩選情境，這裡不必再清一次。
        var categories = projection.CategorySnapshot;
        if (projection.AddedCategories.Count > 0)
        {
            categories = await LocalAccountCategoryStore.ReplaceWithinAsync(
                connection,
                transaction,
                categories.Revision,
                [.. categories.Categories, .. projection.AddedCategories],
                cancellationToken);
        }

        await using (var insertTarget = connection.CreateCommand())
        {
            insertTarget.Transaction = transaction;
            insertTarget.CommandText =
                """
                INSERT INTO account_mapping
                    (batch_id, source_row_number, account_number, account_name,
                     standardized_category, category_id, classification_explicit)
                VALUES (@batchId, @sourceRowNumber, @accountNumber, @accountName,
                        @category, @categoryId, @explicit);
                """;
            insertTarget.AddWithValue("@batchId", batchId);
            var sourceRowParam = insertTarget.AddParameter("@sourceRowNumber", DbType.Int64);
            var codeParam = insertTarget.AddParameter("@accountNumber", DbType.String);
            var nameParam = insertTarget.AddParameter("@accountName", DbType.String);
            var categoryParam = insertTarget.AddParameter("@category", DbType.String);
            var categoryIdParam = insertTarget.AddParameter("@categoryId", DbType.String);
            var explicitParam = insertTarget.AddParameter("@explicit", DbType.Int32);

            foreach (var mapping in projected)
            {
                sourceRowParam.Value = mapping.SourceRowNumber;
                codeParam.Value = mapping.AccountNumber;
                nameParam.Value = (object?)mapping.AccountName ?? DBNull.Value;
                categoryParam.Value = mapping.LegacyCategory;
                categoryIdParam.Value = mapping.CategoryId;
                explicitParam.Value = mapping.HasExplicitCategory ? 1 : 0;
                await insertTarget.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var updateCounts = connection.CreateCommand())
        {
            updateCounts.Transaction = transaction;
            updateCounts.CommandText =
                """
                UPDATE import_batch SET row_count = @rowCount WHERE batch_id = @batchId;
                UPDATE import_batch_source SET row_count = @rowCount WHERE batch_id = @batchId AND source_no = 1;
                """;
            updateCounts.AddWithValue("@rowCount", rowCount);
            updateCounts.AddWithValue("@batchId", batchId);
            await updateCounts.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new AccountMappingImportResult(batchId, rowCount, columns, source.FileName, importedUtc, categories);
    }

    public async Task<AccountMappingState?> FindStateAsync(string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var counterpartRoles = GlRulePredicates.RevenueCounterpartRoles;
        var counterpartParameters = string.Join(
            ", ",
            Enumerable.Range(0, counterpartRoles.Count).Select(static index => "@counterpart" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        command.CommandText =
            $"""
            SELECT b.batch_id,
                   CASE WHEN b.source_file_name = @editorName THEN (SELECT COUNT(*) FROM account_mapping) ELSE b.row_count END,
                   b.source_file_name, b.imported_utc,
                   EXISTS (SELECT 1 FROM account_mapping),
                   EXISTS (SELECT 1
                           FROM account_mapping m
                           JOIN account_category t
                             ON t.category_id = m.category_id
                           WHERE t.semantic_role = @revenue),
                   EXISTS (SELECT 1
                           FROM account_mapping m
                           JOIN account_category t
                             ON t.category_id = m.category_id
                           WHERE t.semantic_role IN ({counterpartParameters})),
                   (SELECT COUNT(*) FROM account_mapping m WHERE m.classification_explicit = 0)
            FROM import_batch b
            WHERE b.dataset_kind = @kind
            ORDER BY b.imported_utc DESC, b.batch_id DESC
            LIMIT 1;
            """;
        command.AddWithValue("@kind", DatasetKind.AccountMapping.ToStorageName());
        command.AddWithValue("@editorName", AccountMappingEditorRepository.EditorSourceName);
        command.AddWithValue("@revenue", AccountCategoryBuiltIns.RevenueRole);
        for (var index = 0; index < counterpartRoles.Count; index++)
        {
            command.AddWithValue(
                "@counterpart" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                counterpartRoles[index]);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AccountMappingState(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3)),
            // EXISTS(...) 在 SQLite 回 INTEGER 0/1，以 Convert.ToBoolean(值) 轉成布林。
            Convert.ToBoolean(reader.GetValue(4)),
            Convert.ToBoolean(reader.GetValue(5)),
            Convert.ToBoolean(reader.GetValue(6)),
            Convert.ToInt32(reader.GetValue(7)));
    }
}
