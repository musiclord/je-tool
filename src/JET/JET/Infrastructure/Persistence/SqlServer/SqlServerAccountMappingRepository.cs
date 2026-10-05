using System.Data;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 科目配對的 SQL Server 實作(對應 <see cref="LocalAccountMappingRepository"/>;機械式移植,
/// 流程設計仍 parked)。replace-only;staging 寫入與 target 投影同一交易,任一列分類非法整批 rollback。
/// FindState 的 EXISTS 在 T-SQL 不可置於 SELECT 清單,改用 CASE WHEN EXISTS;LIMIT 1 → TOP 1。
/// </summary>
public sealed class SqlServerAccountMappingRepository(SqlServerProjectDatabase database)
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
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        projection ??= JetAuditProgram.PrepareAccountMappingProjection(
            columns,
            AccountTaxonomyCatalog.BuiltInSnapshot);

        var batchId = Guid.NewGuid().ToString("N");
        var importedUtc = DateTimeOffset.UtcNow;
        var kindName = DatasetKind.AccountMapping.ToStorageName();

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var cleanup = database.CreateCommand(connection, projectId,
            """
            DELETE FROM {s}.staging_account_mapping_raw_row
            WHERE batch_id IN (SELECT batch_id FROM {s}.import_batch WHERE dataset_kind = @kind);
            DELETE FROM {s}.import_batch_source
            WHERE batch_id IN (SELECT batch_id FROM {s}.import_batch WHERE dataset_kind = @kind);
            DELETE FROM {s}.import_batch WHERE dataset_kind = @kind;
            DELETE FROM {s}.target_account_mapping;
            """))
        {
            cleanup.Transaction = transaction;
            cleanup.Parameters.AddWithValue("@kind", kindName);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        // 科目配對換版,未預期借貸組合等規則結果即失效。
        await RuleRunResultReset.ClearWithinAsync(
            connection,
            transaction,
            cancellationToken,
            AuditMutation.AccountMapping,
            SqlServerProjectSchema.QualifierFor(projectId));

        await using (var insertBatch = database.CreateCommand(connection, projectId,
            """
            INSERT INTO {s}.import_batch
                (batch_id, dataset_kind, source_file_path, source_file_name, imported_utc, row_count, columns_json)
            VALUES (@batchId, @kind, @filePath, @fileName, @importedUtc, 0, @columnsJson);
            INSERT INTO {s}.import_batch_source
                (batch_id, source_no, source_file_path, source_file_name, sheet_name, encoding, delimiter, row_count, imported_utc)
            VALUES (@batchId, 1, @filePath, @fileName, @sheetName, @encoding, @delimiter, 0, @importedUtc);
            """))
        {
            insertBatch.Transaction = transaction;
            insertBatch.Parameters.AddWithValue("@batchId", batchId);
            insertBatch.Parameters.AddWithValue("@kind", kindName);
            insertBatch.Parameters.AddWithValue("@filePath", source.FilePath);
            insertBatch.Parameters.AddWithValue("@fileName", source.FileName);
            insertBatch.Parameters.AddWithValue("@sheetName", (object?)source.SheetName ?? DBNull.Value);
            insertBatch.Parameters.AddWithValue("@encoding", (object?)source.EncodingName ?? DBNull.Value);
            insertBatch.Parameters.AddWithValue("@delimiter", (object?)source.Delimiter ?? DBNull.Value);
            insertBatch.Parameters.AddWithValue("@importedUtc", importedUtc.ToString("O"));
            insertBatch.Parameters.AddWithValue("@columnsJson", JsonSerializer.Serialize(columns, JsonOptions));
            await insertBatch.ExecuteNonQueryAsync(cancellationToken);
        }

        var rowCount = 0;

        await using (var insertRow = database.CreateCommand(connection, projectId,
            """
            INSERT INTO {s}.staging_account_mapping_raw_row
                (batch_id, row_number, source_no, source_row_number, row_json)
            VALUES (@batchId, @rowNumber, 1, @sourceRowNumber, @rowJson);
            """))
        {
            insertRow.Transaction = transaction;
            insertRow.Parameters.AddWithValue("@batchId", batchId);
            var rowNumberParam = insertRow.Parameters.Add("@rowNumber", SqlDbType.BigInt);
            var sourceRowParam = insertRow.Parameters.Add("@sourceRowNumber", SqlDbType.Int);
            var rowJsonParam = insertRow.Parameters.Add("@rowJson", SqlDbType.NVarChar, -1);

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
                AccountMappingFailureStyle.SqlServer);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        await using (var insertTarget = database.CreateCommand(connection, projectId,
            """
            INSERT INTO {s}.target_account_mapping
                (batch_id, source_row_number, account_code, account_name,
                 standardized_category, category_id, classification_explicit)
            VALUES (@batchId, @sourceRowNumber, @accountCode, @accountName,
                    @category, @categoryId, @explicit);
            """))
        {
            insertTarget.Transaction = transaction;
            insertTarget.Parameters.AddWithValue("@batchId", batchId);
            var sourceRowParam = insertTarget.Parameters.Add("@sourceRowNumber", SqlDbType.Int);
            var codeParam = insertTarget.Parameters.Add("@accountCode", SqlDbType.NVarChar, 450);
            var nameParam = insertTarget.Parameters.Add("@accountName", SqlDbType.NVarChar, 400);
            var categoryParam = insertTarget.Parameters.Add("@category", SqlDbType.NVarChar, 40);
            var categoryIdParam = insertTarget.Parameters.Add("@categoryId", SqlDbType.NVarChar, 64);
            var explicitParam = insertTarget.Parameters.Add("@explicit", SqlDbType.Int);

            foreach (var mapping in projected)
            {
                sourceRowParam.Value = mapping.SourceRowNumber;
                codeParam.Value = mapping.AccountCode;
                nameParam.Value = (object?)mapping.AccountName ?? DBNull.Value;
                categoryParam.Value = mapping.LegacyCategory;
                categoryIdParam.Value = mapping.CategoryId;
                explicitParam.Value = mapping.HasExplicitCategory ? 1 : 0;
                await insertTarget.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var updateCounts = database.CreateCommand(connection, projectId,
            """
            UPDATE {s}.import_batch SET row_count = @rowCount WHERE batch_id = @batchId;
            UPDATE {s}.import_batch_source SET row_count = @rowCount WHERE batch_id = @batchId AND source_no = 1;
            """))
        {
            updateCounts.Transaction = transaction;
            updateCounts.Parameters.AddWithValue("@rowCount", rowCount);
            updateCounts.Parameters.AddWithValue("@batchId", batchId);
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

        await using var command = database.CreateCommand(connection, projectId,
            """
            SELECT TOP 1 b.batch_id,
                   CASE WHEN b.source_file_name = @editorName THEN (SELECT COUNT(*) FROM {s}.target_account_mapping) ELSE b.row_count END,
                   b.source_file_name, b.imported_utc,
                   CASE WHEN EXISTS (SELECT 1 FROM {s}.target_account_mapping) THEN 1 ELSE 0 END,
                   CASE WHEN EXISTS (SELECT 1
                                     FROM {s}.target_account_mapping m
                                     JOIN {s}.config_account_taxonomy t
                                       ON t.category_id = m.category_id
                                     WHERE t.semantic_role = @revenue)
                        THEN 1 ELSE 0 END,
                   CASE WHEN EXISTS (SELECT 1
                                     FROM {s}.target_account_mapping m
                                     JOIN {s}.config_account_taxonomy t
                                       ON t.category_id = m.category_id
                                     WHERE t.semantic_role IN (@receivables, @cash, @receiptInAdvance))
                        THEN 1 ELSE 0 END,
                   (SELECT COUNT(*) FROM {s}.target_account_mapping m WHERE m.classification_explicit = 0)
            FROM {s}.import_batch b
            WHERE b.dataset_kind = @kind
            ORDER BY b.imported_utc DESC, b.batch_id DESC;
            """);
        command.Parameters.AddWithValue("@kind", DatasetKind.AccountMapping.ToStorageName());
        command.Parameters.AddWithValue("@editorName", AccountMappingEditorRepository.EditorSourceName);
        command.Parameters.AddWithValue("@revenue", AccountTaxonomyBuiltIns.RevenueRole);
        command.Parameters.AddWithValue("@receivables", AccountTaxonomyBuiltIns.ReceivablesRole);
        command.Parameters.AddWithValue("@cash", AccountTaxonomyBuiltIns.CashRole);
        command.Parameters.AddWithValue("@receiptInAdvance", AccountTaxonomyBuiltIns.ReceiptInAdvanceRole);

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
            reader.GetInt32(4) == 1,
            reader.GetInt32(5) == 1,
            reader.GetInt32(6) == 1,
            reader.GetInt32(7));
    }
}
