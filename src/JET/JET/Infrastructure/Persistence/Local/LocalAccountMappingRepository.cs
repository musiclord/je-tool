using System.Data;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 科目配對的匯入與 presence 查詢（manifest import.accountMapping.fromFile）。
/// 與 GL/TB 匯入的差異：格式固定三欄、無欄位配對步驟——staging 寫入與
/// target 投影在**同一 transaction**（任一列分類非法即整批 rollback）。
/// replace-only：科目配對是整份替換的設定檔，不做多來源合併。
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
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        projection ??= JetAuditProgram.PrepareAccountMappingProjection(
            columns,
            AccountTaxonomyCatalog.BuiltInSnapshot);

        var batchId = Guid.NewGuid().ToString("N");
        var importedUtc = DateTimeOffset.UtcNow;
        var kindName = DatasetKind.AccountMapping.ToStorageName();

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // replace 語意：同一 transaction 清除舊批次、staging 與 target。
        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText =
                """
                DELETE FROM staging_account_mapping_raw_row
                WHERE batch_id IN (SELECT batch_id FROM import_batch WHERE dataset_kind = @kind);
                DELETE FROM import_batch_source
                WHERE batch_id IN (SELECT batch_id FROM import_batch WHERE dataset_kind = @kind);
                DELETE FROM import_batch WHERE dataset_kind = @kind;
                DELETE FROM target_account_mapping;
                """;
            cleanup.AddWithValue("@kind", kindName);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        // 科目配對換版,未預期借貸組合等規則結果即失效(plan Phase 1)。
        await RuleRunResultReset.ClearWithinAsync(
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
            // source_file_path 是 legacy 實體欄名；本地案件只保存可攜的檔名。
            insertBatch.AddWithValue("@filePath", source.FileName);
            insertBatch.AddWithValue("@fileName", source.FileName);
            insertBatch.AddWithValue("@sheetName", (object?)source.SheetName ?? DBNull.Value);
            insertBatch.AddWithValue("@encoding", (object?)source.EncodingName ?? DBNull.Value);
            insertBatch.AddWithValue("@delimiter", (object?)source.Delimiter ?? DBNull.Value);
            insertBatch.AddWithValue("@importedUtc", importedUtc.ToString("O"));
            insertBatch.AddWithValue("@columnsJson", JsonSerializer.Serialize(columns, JsonOptions));
            await insertBatch.ExecuteNonQueryAsync(cancellationToken);
        }

        // 串流寫 staging，同步投影（last-wins 去重：同科目代號後列覆蓋前列）。
        var rowCount = 0;

        await using (var insertRow = connection.CreateCommand())
        {
            insertRow.Transaction = transaction;
            insertRow.CommandText =
                """
                INSERT INTO staging_account_mapping_raw_row
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
                source.FileName,
                AccountMappingFailureStyle.Local);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        await using (var insertTarget = connection.CreateCommand())
        {
            insertTarget.Transaction = transaction;
            insertTarget.CommandText =
                """
                INSERT INTO target_account_mapping
                    (batch_id, source_row_number, account_code, account_name,
                     standardized_category, category_id)
                VALUES (@batchId, @sourceRowNumber, @accountCode, @accountName,
                        @category, @categoryId);
                """;
            insertTarget.AddWithValue("@batchId", batchId);
            var sourceRowParam = insertTarget.AddParameter("@sourceRowNumber", DbType.Int64);
            var codeParam = insertTarget.AddParameter("@accountCode", DbType.String);
            var nameParam = insertTarget.AddParameter("@accountName", DbType.String);
            var categoryParam = insertTarget.AddParameter("@category", DbType.String);
            var categoryIdParam = insertTarget.AddParameter("@categoryId", DbType.String);

            foreach (var mapping in projected)
            {
                sourceRowParam.Value = mapping.SourceRowNumber;
                codeParam.Value = mapping.AccountCode;
                nameParam.Value = (object?)mapping.AccountName ?? DBNull.Value;
                categoryParam.Value = mapping.LegacyCategory;
                categoryIdParam.Value = mapping.CategoryId;
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

        return new AccountMappingImportResult(batchId, rowCount, columns, source.FileName, importedUtc);
    }

    public async Task<AccountMappingState?> FindStateAsync(string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT b.batch_id, b.row_count, b.source_file_name, b.imported_utc,
                   EXISTS (SELECT 1 FROM target_account_mapping),
                   EXISTS (SELECT 1
                           FROM target_account_mapping m
                           JOIN config_account_taxonomy t
                             ON t.category_id = m.category_id
                             OR (m.category_id IS NULL AND t.is_builtin = 1 AND t.label = m.standardized_category)
                           WHERE t.semantic_role = @revenue),
                   EXISTS (SELECT 1
                           FROM target_account_mapping m
                           JOIN config_account_taxonomy t
                             ON t.category_id = m.category_id
                             OR (m.category_id IS NULL AND t.is_builtin = 1 AND t.label = m.standardized_category)
                           WHERE t.semantic_role IN (@receivables, @cash, @receiptInAdvance))
            FROM import_batch b
            WHERE b.dataset_kind = @kind
            ORDER BY b.imported_utc DESC, b.batch_id DESC
            LIMIT 1;
            """;
        command.AddWithValue("@kind", DatasetKind.AccountMapping.ToStorageName());
        command.AddWithValue("@revenue", AccountTaxonomyBuiltIns.RevenueRole);
        command.AddWithValue("@receivables", AccountTaxonomyBuiltIns.ReceivablesRole);
        command.AddWithValue("@cash", AccountTaxonomyBuiltIns.CashRole);
        command.AddWithValue("@receiptInAdvance", AccountTaxonomyBuiltIns.ReceiptInAdvanceRole);

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
            // EXISTS(...) 的結果型別跨本地引擎不同：SQLite 回 INTEGER 0/1，DuckDB 回 BOOLEAN。
            // 以 Convert.ToBoolean(值) 統一（int 0/1 與 bool 皆正確），SQL 文本不動、SQLite 行為不變。
            Convert.ToBoolean(reader.GetValue(4)),
            Convert.ToBoolean(reader.GetValue(5)),
            Convert.ToBoolean(reader.GetValue(6)));
    }
}
