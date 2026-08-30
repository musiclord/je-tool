using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 匯入批次的 SQL Server 實作(對應 <see cref="LocalImportRepository"/>,guide §3.1.4 多來源模型)。
/// 語意與 SQLite 一致:replace 沿用來源列號為 row_number、append 從最大值續編;任一空檔 rollback;
/// replace/append 皆使下游(target/config_field_mapping)與規則結果失效。
/// 純輔助(欄位集合檢查、表名映射、來源資訊組裝)重用 <see cref="LocalImportRepository"/> 的 internal static。
/// staging 寫入採 <see cref="SqlBulkCopy"/> 串流(對齊 GL 投影;來源 IAsyncEnumerable 經有界 channel
/// producer-consumer 橋接到 DbDataReader.ReadAsync)。進度推播在 handler 層。
/// 診斷日誌（dev-only）：一次性 SQL 走 <see cref="DiagnosticDb"/>、transaction 走 scope；
/// SqlBulkCopy 不逐列記事件，改以 staging/replace/append 階段 milestone 收斂（與 SQLite import 事件等價）。
/// </summary>
public sealed class SqlServerImportRepository(SqlServerProjectDatabase database, ILogger<SqlServerImportRepository>? logger = null)
    : IImportRepository
{
    private const string Provider = "sqlServer";

    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    /// <summary>producer-consumer 有界緩衝:背壓避免百萬列堆積,同時讓解析/序列化與 bulk 送出重疊。</summary>
    private const int BulkChannelCapacity = 8192;

    private readonly ILogger _log = logger ?? NullLogger<SqlServerImportRepository>.Instance;

    public async Task<ImportBatchResult> ReplaceBatchAsync(
        string projectId,
        DatasetKind kind,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        var importStopwatch = Stopwatch.StartNew();

        var batchId = Guid.NewGuid().ToString("N");
        var importedUtc = DateTimeOffset.UtcNow;
        var kindName = kind.ToStorageName();
        var stagingTable = LocalImportRepository.StagingTableFor(kind);
        var targetTable = LocalImportRepository.TargetTableFor(kind);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        using var txLog = DiagnosticDb.BeginTransaction(_log, Provider);

        // replace 語意:同一交易清除該 dataset 全部舊狀態(含 target 與已提交配對)。
        await using (var cleanup = database.CreateCommand(connection, projectId,
            $$"""
                DELETE FROM {s}.{{stagingTable}}
                WHERE batch_id IN (SELECT batch_id FROM {s}.import_batch WHERE dataset_kind = @kind);
                DELETE FROM {s}.import_batch_source
                WHERE batch_id IN (SELECT batch_id FROM {s}.import_batch WHERE dataset_kind = @kind);
                DELETE FROM {s}.import_field_definition
                WHERE batch_id IN (SELECT batch_id FROM {s}.import_batch WHERE dataset_kind = @kind);
                DELETE FROM {s}.import_batch WHERE dataset_kind = @kind;
                DELETE FROM {s}.target_gl_rde_value WHERE @kind = 'gl';
                DELETE FROM {s}.config_gl_rde_field WHERE @kind = 'gl';
                DELETE FROM {s}.{{targetTable}};
                DELETE FROM {s}.config_field_mapping WHERE dataset_kind = @kind;
                """))
        {
            cleanup.Transaction = transaction;
            cleanup.CommandTimeout = 0; // 重匯入清除百萬列 staging/target 屬長批次,不設 30s 逾時
            cleanup.Parameters.AddWithValue("@kind", kindName);
            await cleanup.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        await RuleRunResultReset.ClearWithinAsync(
            connection,
            transaction,
            cancellationToken,
            kind == DatasetKind.Gl
                ? AuditMutation.GlImport
                : AuditMutation.TbImport,
            SqlServerProjectSchema.QualifierFor(projectId));

        await using (var insertBatch = database.CreateCommand(connection, projectId,
            """
                INSERT INTO {s}.import_batch
                    (batch_id, dataset_kind, source_file_path, source_file_name, imported_utc, row_count, columns_json)
                VALUES (@batchId, @kind, @filePath, @fileName, @importedUtc, 0, @columnsJson);
                """))
        {
            insertBatch.Transaction = transaction;
            insertBatch.Parameters.AddWithValue("@batchId", batchId);
            insertBatch.Parameters.AddWithValue("@kind", kindName);
            insertBatch.Parameters.AddWithValue("@filePath", source.FilePath);
            insertBatch.Parameters.AddWithValue("@fileName", source.FileName);
            insertBatch.Parameters.AddWithValue("@importedUtc", importedUtc.ToString("O"));
            insertBatch.Parameters.AddWithValue("@columnsJson", JsonSerializer.Serialize(columns, JsonOptions));
            await insertBatch.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        await InsertSourceRecordAsync(connection, transaction, projectId, batchId, sourceNo: 1, source, importedUtc, cancellationToken);

        var fieldDefinitions = LegacyFieldDefinitionAccumulator.Create(columns);
        var bulkStopwatch = Stopwatch.StartNew();
        var (rowCount, observedKeys, _) = await BulkCopyStagingAsync(
            connection, transaction, projectId, stagingTable, batchId, sourceNo: 1,
            isAppend: false, appendStartRowNumber: 0, rows, fieldDefinitions, cancellationToken);
        bulkStopwatch.Stop();

        if (rowCount == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            throw new JetActionException(
                JetErrorCodes.EmptyWorkbook,
                $"檔案 '{source.FileName}' 沒有任何資料列。");
        }

        DiagnosticDbLog.ImportMilestone(_log, "staging", rowCount, bulkStopwatch.ElapsedMilliseconds,
            rowCount * 1000.0 / Math.Max(1, bulkStopwatch.ElapsedMilliseconds));

        var effectiveColumns = TabularHeaderNormalizer.FinalizeBatchColumns(columns, observedKeys);
        await using (var updateColumns = database.CreateCommand(connection, projectId,
            "UPDATE {s}.import_batch SET columns_json = @columnsJson WHERE batch_id = @batchId;"))
        {
            updateColumns.Transaction = transaction;
            updateColumns.Parameters.AddWithValue("@columnsJson", JsonSerializer.Serialize(effectiveColumns, JsonOptions));
            updateColumns.Parameters.AddWithValue("@batchId", batchId);
            await updateColumns.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        await SqlServerFieldDefinitionPersistence.ReplaceAsync(
            database,
            connection,
            transaction,
            projectId,
            batchId,
            LegacyFieldDefinitionScope.Source,
            fieldDefinitions.Build(effectiveColumns),
            cancellationToken);

        await UpdateRowCountsAsync(connection, transaction, projectId, batchId, sourceNo: 1, addedRowCount: rowCount, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        txLog.Committed();
        DiagnosticDbLog.ImportMilestone(_log, "replace", rowCount, importStopwatch.ElapsedMilliseconds,
            rowCount * 1000.0 / Math.Max(1, importStopwatch.ElapsedMilliseconds));

        var sources = new[] { LocalImportRepository.ToSourceInfo(sourceNo: 1, source, rowCount, importedUtc) };
        return new ImportBatchResult(
            new ImportBatchInfo(batchId, kind, source.FileName, importedUtc, rowCount, effectiveColumns, sources),
            rowCount);
    }

    public async Task<ImportBatchResult> AppendToBatchAsync(
        string projectId,
        DatasetKind kind,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        var importStopwatch = Stopwatch.StartNew();

        var kindName = kind.ToStorageName();
        var stagingTable = LocalImportRepository.StagingTableFor(kind);
        var targetTable = LocalImportRepository.TargetTableFor(kind);
        var importedUtc = DateTimeOffset.UtcNow;

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        using var txLog = DiagnosticDb.BeginTransaction(_log, Provider);

        string batchId;
        string batchFileName;
        DateTimeOffset batchImportedUtc;
        int existingRowCount;
        IReadOnlyList<string> batchColumns;

        await using (var findBatch = database.CreateCommand(connection, projectId,
            """
                SELECT TOP 1 batch_id, source_file_name, imported_utc, row_count, columns_json
                FROM {s}.import_batch
                WHERE dataset_kind = @kind
                ORDER BY imported_utc DESC, batch_id DESC;
                """))
        {
            findBatch.Transaction = transaction;
            findBatch.Parameters.AddWithValue("@kind", kindName);

            await using var reader = await findBatch.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new JetActionException(
                    JetErrorCodes.NoImportBatch,
                    $"尚未匯入任何 {kindName.ToUpperInvariant()} 資料,無法附加來源;第一個來源請以 mode 'replace' 匯入。");
            }

            batchId = reader.GetString(0);
            batchFileName = reader.GetString(1);
            batchImportedUtc = DateTimeOffset.Parse(reader.GetString(2));
            existingRowCount = reader.GetInt32(3);
            batchColumns = JsonSerializer.Deserialize<List<string>>(reader.GetString(4), JsonOptions) ?? [];
        }

        LocalImportRepository.EnsureColumnSetsMatch(
            source.FileName,
            batchColumns.Where(c => !TabularHeaderNormalizer.IsPlaceholder(c)).ToList(),
            columns.Where(c => !TabularHeaderNormalizer.IsPlaceholder(c)).ToList());

        int nextSourceNo;
        long nextRowNumber;

        await using (var maxQuery = database.CreateCommand(connection, projectId,
            $$"""
                SELECT
                    (SELECT COALESCE(MAX(source_no), 0) FROM {s}.import_batch_source WHERE batch_id = @batchId),
                    (SELECT COALESCE(MAX(row_number), 0) FROM {s}.{{stagingTable}} WHERE batch_id = @batchId);
                """))
        {
            maxQuery.Transaction = transaction;
            maxQuery.Parameters.AddWithValue("@batchId", batchId);

            await using var reader = await maxQuery.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
            await reader.ReadAsync(cancellationToken);
            nextSourceNo = reader.GetInt32(0) + 1;
            nextRowNumber = reader.GetInt64(1) + 1;
        }

        var existingDefinitions = await SqlServerFieldDefinitionPersistence.LoadStatesAsync(
            database,
            connection,
            transaction,
            projectId,
            batchId,
            LegacyFieldDefinitionScope.Source,
            cancellationToken);
        if (existingDefinitions.Count == 0)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidProjectSchema,
                $"{kindName.ToUpperInvariant()} 批次缺少 Legacy 欄位定義；本版不反推舊批次，請以 mode 'replace' 重新匯入。");
        }

        var fieldDefinitions = LegacyFieldDefinitionAccumulator.Restore(existingDefinitions);

        await InsertSourceRecordAsync(connection, transaction, projectId, batchId, nextSourceNo, source, importedUtc, cancellationToken);

        var bulkStopwatch = Stopwatch.StartNew();
        var (addedRowCount, observedKeys, _) = await BulkCopyStagingAsync(
            connection, transaction, projectId, stagingTable, batchId, nextSourceNo,
            isAppend: true, appendStartRowNumber: nextRowNumber, rows, fieldDefinitions, cancellationToken);
        bulkStopwatch.Stop();

        if (addedRowCount == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            throw new JetActionException(
                JetErrorCodes.EmptyWorkbook,
                $"檔案 '{source.FileName}' 沒有任何資料列,未附加(既有批次不受影響)。");
        }

        DiagnosticDbLog.ImportMilestone(_log, "staging", addedRowCount, bulkStopwatch.ElapsedMilliseconds,
            addedRowCount * 1000.0 / Math.Max(1, bulkStopwatch.ElapsedMilliseconds));

        var effectiveColumns = TabularHeaderNormalizer.FinalizeBatchColumns(columns, observedKeys);
        try
        {
            LocalImportRepository.EnsureColumnSetsMatch(source.FileName, batchColumns, effectiveColumns);
        }
        catch (JetActionException)
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            throw;
        }

        await SqlServerFieldDefinitionPersistence.ReplaceAsync(
            database,
            connection,
            transaction,
            projectId,
            batchId,
            LegacyFieldDefinitionScope.Source,
            fieldDefinitions.Build(batchColumns),
            cancellationToken);
        await SqlServerFieldDefinitionPersistence.ReplaceAsync(
            database,
            connection,
            transaction,
            projectId,
            batchId,
            LegacyFieldDefinitionScope.Target,
            [],
            cancellationToken);

        await UpdateRowCountsAsync(connection, transaction, projectId, batchId, nextSourceNo, addedRowCount, cancellationToken);

        // 附加使下游失效(與 replace 同語意):母體變了,target 投影與已提交配對必須重做。
        await using (var invalidate = database.CreateCommand(connection, projectId,
            $$"""
                DELETE FROM {s}.target_gl_rde_value WHERE @kind = 'gl';
                DELETE FROM {s}.config_gl_rde_field WHERE @kind = 'gl';
                DELETE FROM {s}.{{targetTable}};
                DELETE FROM {s}.config_field_mapping WHERE dataset_kind = @kind;
                """))
        {
            invalidate.Transaction = transaction;
            invalidate.CommandTimeout = 0; // 附加使下游失效時 DELETE 百萬列 target,屬長批次,不設 30s 逾時
            invalidate.Parameters.AddWithValue("@kind", kindName);
            await invalidate.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        await RuleRunResultReset.ClearWithinAsync(
            connection,
            transaction,
            cancellationToken,
            kind == DatasetKind.Gl
                ? AuditMutation.GlImport
                : AuditMutation.TbImport,
            SqlServerProjectSchema.QualifierFor(projectId));

        var sources = await LoadSourcesAsync(connection, transaction, projectId, batchId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        txLog.Committed();
        DiagnosticDbLog.ImportMilestone(_log, "append", addedRowCount, importStopwatch.ElapsedMilliseconds,
            addedRowCount * 1000.0 / Math.Max(1, importStopwatch.ElapsedMilliseconds));

        return new ImportBatchResult(
            new ImportBatchInfo(
                batchId, kind, batchFileName, batchImportedUtc,
                existingRowCount + addedRowCount, batchColumns, sources),
            addedRowCount);
    }

    public async Task<ImportBatchResult> ReplaceBatchAsync(
        string projectId,
        DatasetKind kind,
        IReadOnlyList<ImportSourceInput> sources,
        CancellationToken cancellationToken)
    {
        ValidateBatchSources(sources);
        EnsureNamedColumnsMatchFirstSource(sources);

        await database.EnsureCreatedAsync(projectId, cancellationToken);
        var importStopwatch = Stopwatch.StartNew();
        var first = sources[0];
        var batchId = Guid.NewGuid().ToString("N");
        var batchImportedUtc = DateTimeOffset.UtcNow;
        var kindName = kind.ToStorageName();
        var stagingTable = LocalImportRepository.StagingTableFor(kind);
        var targetTable = LocalImportRepository.TargetTableFor(kind);
        var fieldDefinitions = LegacyFieldDefinitionAccumulator.Create(first.Columns);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        using var txLog = DiagnosticDb.BeginTransaction(_log, Provider);

        try
        {
            await using (var cleanup = database.CreateCommand(connection, projectId,
                $$"""
                    DELETE FROM {s}.{{stagingTable}}
                    WHERE batch_id IN (SELECT batch_id FROM {s}.import_batch WHERE dataset_kind = @kind);
                    DELETE FROM {s}.import_batch_source
                    WHERE batch_id IN (SELECT batch_id FROM {s}.import_batch WHERE dataset_kind = @kind);
                    DELETE FROM {s}.import_field_definition
                    WHERE batch_id IN (SELECT batch_id FROM {s}.import_batch WHERE dataset_kind = @kind);
                    DELETE FROM {s}.import_batch WHERE dataset_kind = @kind;
                    DELETE FROM {s}.target_gl_rde_value WHERE @kind = 'gl';
                    DELETE FROM {s}.config_gl_rde_field WHERE @kind = 'gl';
                    DELETE FROM {s}.{{targetTable}};
                    DELETE FROM {s}.config_field_mapping WHERE dataset_kind = @kind;
                    """))
            {
                cleanup.Transaction = transaction;
                cleanup.CommandTimeout = 0;
                cleanup.Parameters.AddWithValue("@kind", kindName);
                await cleanup.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
            }

            await RuleRunResultReset.ClearWithinAsync(
                connection,
                transaction,
                cancellationToken,
                kind == DatasetKind.Gl ? AuditMutation.GlImport : AuditMutation.TbImport,
                SqlServerProjectSchema.QualifierFor(projectId));

            await using (var insertBatch = database.CreateCommand(connection, projectId,
                """
                    INSERT INTO {s}.import_batch
                        (batch_id, dataset_kind, source_file_path, source_file_name, imported_utc, row_count, columns_json)
                    VALUES (@batchId, @kind, @filePath, @fileName, @importedUtc, 0, @columnsJson);
                    """))
            {
                insertBatch.Transaction = transaction;
                insertBatch.Parameters.AddWithValue("@batchId", batchId);
                insertBatch.Parameters.AddWithValue("@kind", kindName);
                insertBatch.Parameters.AddWithValue("@filePath", first.Source.FilePath);
                insertBatch.Parameters.AddWithValue("@fileName", first.Source.FileName);
                insertBatch.Parameters.AddWithValue("@importedUtc", batchImportedUtc.ToString("O"));
                insertBatch.Parameters.AddWithValue(
                    "@columnsJson",
                    JsonSerializer.Serialize(first.Columns, JsonOptions));
                await insertBatch.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
            }

            IReadOnlyList<string> batchColumns = first.Columns;
            var totalAddedRowCount = 0;
            var nextRowNumber = 0L;
            for (var index = 0; index < sources.Count; index++)
            {
                var input = sources[index];
                var sourceNo = index + 1;
                var importedUtc = index == 0 ? batchImportedUtc : DateTimeOffset.UtcNow;
                try
                {
                    await InsertSourceRecordAsync(
                        connection,
                        transaction,
                        projectId,
                        batchId,
                        sourceNo,
                        input.Source,
                        importedUtc,
                        cancellationToken);

                    var bulkStopwatch = Stopwatch.StartNew();
                    var write = await BulkCopyStagingAsync(
                        connection,
                        transaction,
                        projectId,
                        stagingTable,
                        batchId,
                        sourceNo,
                        isAppend: index > 0,
                        appendStartRowNumber: nextRowNumber,
                        input.Rows,
                        fieldDefinitions,
                        cancellationToken);
                    bulkStopwatch.Stop();
                    nextRowNumber = write.NextRowNumber;

                    if (write.RowCount == 0)
                    {
                        throw new JetActionException(
                            JetErrorCodes.EmptyWorkbook,
                            $"檔案 '{input.Source.FileName}' 沒有任何資料列。");
                    }

                    DiagnosticDbLog.ImportMilestone(
                        _log,
                        "staging",
                        write.RowCount,
                        bulkStopwatch.ElapsedMilliseconds,
                        write.RowCount * 1000.0 / Math.Max(1, bulkStopwatch.ElapsedMilliseconds));

                    var effectiveColumns = TabularHeaderNormalizer.FinalizeBatchColumns(
                        input.Columns,
                        write.ObservedKeys);
                    if (index == 0)
                    {
                        batchColumns = effectiveColumns;
                        await using var updateColumns = database.CreateCommand(
                            connection,
                            projectId,
                            "UPDATE {s}.import_batch SET columns_json = @columnsJson WHERE batch_id = @batchId;");
                        updateColumns.Transaction = transaction;
                        updateColumns.Parameters.AddWithValue(
                            "@columnsJson",
                            JsonSerializer.Serialize(batchColumns, JsonOptions));
                        updateColumns.Parameters.AddWithValue("@batchId", batchId);
                        await updateColumns.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
                    }
                    else
                    {
                        LocalImportRepository.EnsureColumnSetsMatch(
                            input.Source.FileName,
                            batchColumns,
                            effectiveColumns);
                    }

                    await UpdateRowCountsAsync(
                        connection,
                        transaction,
                        projectId,
                        batchId,
                        sourceNo,
                        write.RowCount,
                        cancellationToken);
                    totalAddedRowCount += write.RowCount;
                }
                catch (JetActionException error)
                {
                    throw LocalImportRepository.AddBatchSourceContext(
                        error,
                        input.Source,
                        sourceNo,
                        sources.Count);
                }
            }

            await SqlServerFieldDefinitionPersistence.ReplaceAsync(
                database,
                connection,
                transaction,
                projectId,
                batchId,
                LegacyFieldDefinitionScope.Source,
                fieldDefinitions.Build(batchColumns),
                cancellationToken);

            var importedSources = await LoadSourcesAsync(
                connection,
                transaction,
                projectId,
                batchId,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
            txLog.Committed();
            DiagnosticDbLog.ImportMilestone(
                _log,
                "replace",
                totalAddedRowCount,
                importStopwatch.ElapsedMilliseconds,
                totalAddedRowCount * 1000.0 / Math.Max(1, importStopwatch.ElapsedMilliseconds));

            return new ImportBatchResult(
                new ImportBatchInfo(
                    batchId,
                    kind,
                    first.Source.FileName,
                    batchImportedUtc,
                    totalAddedRowCount,
                    batchColumns,
                    importedSources),
                totalAddedRowCount);
        }
        catch
        {
            await RollbackQuietlyAsync(transaction);
            txLog.RolledBack();
            throw;
        }
    }

    public async Task<ImportBatchResult> AppendToBatchAsync(
        string projectId,
        DatasetKind kind,
        IReadOnlyList<ImportSourceInput> sources,
        CancellationToken cancellationToken)
    {
        ValidateBatchSources(sources);
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        var importStopwatch = Stopwatch.StartNew();
        var kindName = kind.ToStorageName();
        var stagingTable = LocalImportRepository.StagingTableFor(kind);
        var targetTable = LocalImportRepository.TargetTableFor(kind);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        using var txLog = DiagnosticDb.BeginTransaction(_log, Provider);

        try
        {
            string batchId;
            string batchFileName;
            DateTimeOffset batchImportedUtc;
            int existingRowCount;
            IReadOnlyList<string> batchColumns;
            await using (var findBatch = database.CreateCommand(connection, projectId,
                """
                    SELECT TOP 1 batch_id, source_file_name, imported_utc, row_count, columns_json
                    FROM {s}.import_batch
                    WHERE dataset_kind = @kind
                    ORDER BY imported_utc DESC, batch_id DESC;
                    """))
            {
                findBatch.Transaction = transaction;
                findBatch.Parameters.AddWithValue("@kind", kindName);
                await using var reader = await findBatch.ExecuteReaderLoggedAsync(
                    _log,
                    Provider,
                    cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new JetActionException(
                        JetErrorCodes.NoImportBatch,
                        $"尚未匯入任何 {kindName.ToUpperInvariant()} 資料,無法附加來源;第一個來源請以 mode 'replace' 匯入。");
                }

                batchId = reader.GetString(0);
                batchFileName = reader.GetString(1);
                batchImportedUtc = DateTimeOffset.Parse(reader.GetString(2));
                existingRowCount = reader.GetInt32(3);
                batchColumns = JsonSerializer.Deserialize<List<string>>(reader.GetString(4), JsonOptions) ?? [];
            }

            var namedBatchColumns = batchColumns
                .Where(column => !TabularHeaderNormalizer.IsPlaceholder(column))
                .ToList();
            for (var index = 0; index < sources.Count; index++)
            {
                var input = sources[index];
                try
                {
                    LocalImportRepository.EnsureColumnSetsMatch(
                        input.Source.FileName,
                        namedBatchColumns,
                        input.Columns.Where(column => !TabularHeaderNormalizer.IsPlaceholder(column)).ToList());
                }
                catch (JetActionException error)
                {
                    throw LocalImportRepository.AddBatchSourceContext(
                        error,
                        input.Source,
                        index + 1,
                        sources.Count);
                }
            }

            var existingDefinitions = await SqlServerFieldDefinitionPersistence.LoadStatesAsync(
                database,
                connection,
                transaction,
                projectId,
                batchId,
                LegacyFieldDefinitionScope.Source,
                cancellationToken);
            if (existingDefinitions.Count == 0)
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidProjectSchema,
                    $"{kindName.ToUpperInvariant()} 批次缺少 Legacy 欄位定義；本版不反推舊批次，請以 mode 'replace' 重新匯入。");
            }

            var fieldDefinitions = LegacyFieldDefinitionAccumulator.Restore(existingDefinitions);
            int nextSourceNo;
            long nextRowNumber;
            await using (var maxQuery = database.CreateCommand(connection, projectId,
                $$"""
                    SELECT
                        (SELECT COALESCE(MAX(source_no), 0) FROM {s}.import_batch_source WHERE batch_id = @batchId),
                        (SELECT COALESCE(MAX(row_number), 0) FROM {s}.{{stagingTable}} WHERE batch_id = @batchId);
                    """))
            {
                maxQuery.Transaction = transaction;
                maxQuery.Parameters.AddWithValue("@batchId", batchId);
                await using var reader = await maxQuery.ExecuteReaderLoggedAsync(
                    _log,
                    Provider,
                    cancellationToken);
                await reader.ReadAsync(cancellationToken);
                nextSourceNo = reader.GetInt32(0) + 1;
                nextRowNumber = reader.GetInt64(1) + 1;
            }

            var totalAddedRowCount = 0;
            for (var index = 0; index < sources.Count; index++)
            {
                var input = sources[index];
                var sourceNo = nextSourceNo + index;
                try
                {
                    await InsertSourceRecordAsync(
                        connection,
                        transaction,
                        projectId,
                        batchId,
                        sourceNo,
                        input.Source,
                        DateTimeOffset.UtcNow,
                        cancellationToken);
                    var bulkStopwatch = Stopwatch.StartNew();
                    var write = await BulkCopyStagingAsync(
                        connection,
                        transaction,
                        projectId,
                        stagingTable,
                        batchId,
                        sourceNo,
                        isAppend: true,
                        appendStartRowNumber: nextRowNumber,
                        input.Rows,
                        fieldDefinitions,
                        cancellationToken);
                    bulkStopwatch.Stop();
                    nextRowNumber = write.NextRowNumber;

                    if (write.RowCount == 0)
                    {
                        throw new JetActionException(
                            JetErrorCodes.EmptyWorkbook,
                            $"檔案 '{input.Source.FileName}' 沒有任何資料列,未附加(既有批次不受影響)。");
                    }

                    DiagnosticDbLog.ImportMilestone(
                        _log,
                        "staging",
                        write.RowCount,
                        bulkStopwatch.ElapsedMilliseconds,
                        write.RowCount * 1000.0 / Math.Max(1, bulkStopwatch.ElapsedMilliseconds));
                    var effectiveColumns = TabularHeaderNormalizer.FinalizeBatchColumns(
                        input.Columns,
                        write.ObservedKeys);
                    LocalImportRepository.EnsureColumnSetsMatch(
                        input.Source.FileName,
                        batchColumns,
                        effectiveColumns);
                    await UpdateRowCountsAsync(
                        connection,
                        transaction,
                        projectId,
                        batchId,
                        sourceNo,
                        write.RowCount,
                        cancellationToken);
                    totalAddedRowCount += write.RowCount;
                }
                catch (JetActionException error)
                {
                    throw LocalImportRepository.AddBatchSourceContext(
                        error,
                        input.Source,
                        index + 1,
                        sources.Count);
                }
            }

            await SqlServerFieldDefinitionPersistence.ReplaceAsync(
                database,
                connection,
                transaction,
                projectId,
                batchId,
                LegacyFieldDefinitionScope.Source,
                fieldDefinitions.Build(batchColumns),
                cancellationToken);
            await SqlServerFieldDefinitionPersistence.ReplaceAsync(
                database,
                connection,
                transaction,
                projectId,
                batchId,
                LegacyFieldDefinitionScope.Target,
                [],
                cancellationToken);

            await using (var invalidate = database.CreateCommand(connection, projectId,
                $$"""
                    DELETE FROM {s}.target_gl_rde_value WHERE @kind = 'gl';
                    DELETE FROM {s}.config_gl_rde_field WHERE @kind = 'gl';
                    DELETE FROM {s}.{{targetTable}};
                    DELETE FROM {s}.config_field_mapping WHERE dataset_kind = @kind;
                    """))
            {
                invalidate.Transaction = transaction;
                invalidate.CommandTimeout = 0;
                invalidate.Parameters.AddWithValue("@kind", kindName);
                await invalidate.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
            }

            await RuleRunResultReset.ClearWithinAsync(
                connection,
                transaction,
                cancellationToken,
                kind == DatasetKind.Gl ? AuditMutation.GlImport : AuditMutation.TbImport,
                SqlServerProjectSchema.QualifierFor(projectId));

            var importedSources = await LoadSourcesAsync(
                connection,
                transaction,
                projectId,
                batchId,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
            txLog.Committed();
            DiagnosticDbLog.ImportMilestone(
                _log,
                "append",
                totalAddedRowCount,
                importStopwatch.ElapsedMilliseconds,
                totalAddedRowCount * 1000.0 / Math.Max(1, importStopwatch.ElapsedMilliseconds));

            return new ImportBatchResult(
                new ImportBatchInfo(
                    batchId,
                    kind,
                    batchFileName,
                    batchImportedUtc,
                    existingRowCount + totalAddedRowCount,
                    batchColumns,
                    importedSources),
                totalAddedRowCount);
        }
        catch
        {
            await RollbackQuietlyAsync(transaction);
            txLog.RolledBack();
            throw;
        }
    }

    public async Task<ImportBatchInfo?> GetLatestBatchAsync(
        string projectId,
        DatasetKind kind,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        string batchId;
        string fileName;
        DateTimeOffset importedUtc;
        int rowCount;
        IReadOnlyList<string> columns;

        await using (var command = database.CreateCommand(connection, projectId,
            """
                SELECT TOP 1 batch_id, source_file_name, imported_utc, row_count, columns_json
                FROM {s}.import_batch
                WHERE dataset_kind = @kind
                ORDER BY imported_utc DESC, batch_id DESC;
                """))
        {
            command.Parameters.AddWithValue("@kind", kind.ToStorageName());

            await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            batchId = reader.GetString(0);
            fileName = reader.GetString(1);
            importedUtc = DateTimeOffset.Parse(reader.GetString(2));
            rowCount = reader.GetInt32(3);
            columns = JsonSerializer.Deserialize<List<string>>(reader.GetString(4), JsonOptions) ?? [];
        }

        var sources = await LoadSourcesAsync(connection, transaction: null, projectId, batchId, cancellationToken);
        return new ImportBatchInfo(batchId, kind, fileName, importedUtc, rowCount, columns, sources);
    }

    private static void ValidateBatchSources(IReadOnlyList<ImportSourceInput> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "匯入來源清單不得為空。");
        }
    }

    private static void EnsureNamedColumnsMatchFirstSource(IReadOnlyList<ImportSourceInput> sources)
    {
        var firstNamedColumns = sources[0].Columns
            .Where(column => !TabularHeaderNormalizer.IsPlaceholder(column))
            .ToList();
        for (var index = 1; index < sources.Count; index++)
        {
            var input = sources[index];
            try
            {
                LocalImportRepository.EnsureColumnSetsMatch(
                    input.Source.FileName,
                    firstNamedColumns,
                    input.Columns.Where(column => !TabularHeaderNormalizer.IsPlaceholder(column)).ToList());
            }
            catch (JetActionException error)
            {
                throw LocalImportRepository.AddBatchSourceContext(
                    error,
                    input.Source,
                    index + 1,
                    sources.Count);
            }
        }
    }

    private static async Task RollbackQuietlyAsync(SqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch
        {
            // 保留觸發 rollback 的原始例外；transaction dispose 仍會做最後清理。
        }
    }

    private async Task InsertSourceRecordAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string projectId,
        string batchId,
        int sourceNo,
        ImportSourceDescriptor source,
        DateTimeOffset importedUtc,
        CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand(connection, projectId,
            """
            INSERT INTO {s}.import_batch_source
                (batch_id, source_no, source_file_path, source_file_name, sheet_name, encoding, delimiter, row_count, imported_utc)
            VALUES (@batchId, @sourceNo, @filePath, @fileName, @sheetName, @encoding, @delimiter, 0, @importedUtc);
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@batchId", batchId);
        command.Parameters.AddWithValue("@sourceNo", sourceNo);
        command.Parameters.AddWithValue("@filePath", source.FilePath);
        command.Parameters.AddWithValue("@fileName", source.FileName);
        command.Parameters.AddWithValue("@sheetName", (object?)source.SheetName ?? DBNull.Value);
        command.Parameters.AddWithValue("@encoding", (object?)source.EncodingName ?? DBNull.Value);
        command.Parameters.AddWithValue("@delimiter", (object?)source.Delimiter ?? DBNull.Value);
        command.Parameters.AddWithValue("@importedUtc", importedUtc.ToString("O"));
        await command.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
    }

    /// <summary>
    /// 以 <see cref="SqlBulkCopy"/> 串流寫入 staging(既有 transaction、EnableStreaming、無逾時)。
    /// 來源 <see cref="IAsyncEnumerable{T}"/> 由背景 producer task 餵入有界 channel,
    /// consumer(<see cref="StagingBulkCopyDataReader"/>)以 ReadAsync 給 SqlBulkCopy——
    /// 不在任何同步點阻塞 async。回傳本次寫入列數與觀察到的欄名集合(供 effectiveColumns 收斂)。
    /// 取消/失敗:producerCts 解除卡住的 producer、await 收束 producer task(無洩漏),例外交呼叫端 rollback。
    /// </summary>
    private static async Task<(int RowCount, HashSet<string> ObservedKeys, long NextRowNumber)> BulkCopyStagingAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string projectId,
        string stagingTable,
        string batchId,
        int sourceNo,
        bool isAppend,
        long appendStartRowNumber,
        IAsyncEnumerable<StagingRow> rows,
        LegacyFieldDefinitionAccumulator fieldDefinitions,
        CancellationToken cancellationToken)
    {
        var observedKeys = new HashSet<string>(StringComparer.Ordinal);
        var rowCount = 0;
        var nextRowNumber = appendStartRowNumber;
        var channel = Channel.CreateBounded<StagingBulkRecord>(new BoundedChannelOptions(BulkChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        async Task ProduceAsync()
        {
            try
            {
                await foreach (var row in rows.WithCancellation(producerCts.Token).ConfigureAwait(false))
                {
                    observedKeys.UnionWith(row.Values.Keys);
                    fieldDefinitions.Observe(row);
                    var json = JsonSerializer.Serialize(row.Values, JsonOptions);
                    var assigned = isAppend ? nextRowNumber++ : row.SourceRowNumber;
                    if (!isAppend)
                    {
                        nextRowNumber = Math.Max(nextRowNumber, (long)row.SourceRowNumber + 1);
                    }
                    await channel.Writer.WriteAsync(
                        new StagingBulkRecord(assigned, row.SourceRowNumber, json), producerCts.Token).ConfigureAwait(false);
                    rowCount++;
                }

                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex); // 故障傳給 consumer 的 WaitToReadAsync
                throw;
            }
        }

        // CPU 密集的解析/序列化必須在獨立執行緒,才能與 bulk copy 的網路 I/O 重疊
        // (否則 producer 在呼叫緒上同步跑滿,與 consumer 爭用而退化成序列執行)。
        var producerTask = Task.Run(ProduceAsync);
        try
        {
            using var reader = new StagingBulkCopyDataReader(channel.Reader, batchId, sourceNo);
            var schema = SqlServerProjectSchema.For(projectId);
            using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
            {
                DestinationTableName = $"[{schema}].[{stagingTable}]",
                EnableStreaming = true,
                BulkCopyTimeout = 0, // 大資料路徑不設逾時(對齊 GL 投影)
            };
            foreach (var column in StagingBulkCopyDataReader.ColumnNames)
            {
                bulk.ColumnMappings.Add(column, column);
            }

            await bulk.WriteToServerAsync(reader, cancellationToken);
            await producerTask; // 收 producer 例外、確保 observedKeys/rowCount 落定
        }
        catch
        {
            producerCts.Cancel(); // 解除可能卡在 WriteAsync 的 producer
            Exception? producerError = null;
            try
            {
                await producerTask;
            }
            catch (Exception error)
            {
                producerError = error;
            }

            // Channel/SqlBulkCopy 可能把 producer fault 包成 consumer 例外；來源串流的原始
            // JetActionException 必須優先保留，才能維持 error code、檔名與原原因。
            if (producerError is not null and not OperationCanceledException)
            {
                ExceptionDispatchInfo.Capture(producerError).Throw();
            }

            throw; // SQL／取消主因交呼叫端 transaction rollback 與 dispatcher mapping
        }

        return (rowCount, observedKeys, nextRowNumber);
    }

    private async Task UpdateRowCountsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string projectId,
        string batchId,
        int sourceNo,
        int addedRowCount,
        CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand(connection, projectId,
            """
            UPDATE {s}.import_batch_source SET row_count = @added WHERE batch_id = @batchId AND source_no = @sourceNo;
            UPDATE {s}.import_batch SET row_count = row_count + @added WHERE batch_id = @batchId;
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@added", addedRowCount);
        command.Parameters.AddWithValue("@batchId", batchId);
        command.Parameters.AddWithValue("@sourceNo", sourceNo);
        await command.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
    }

    private async Task<IReadOnlyList<ImportSourceInfo>> LoadSourcesAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string projectId,
        string batchId,
        CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand(connection, projectId,
            """
            SELECT source_no, source_file_name, sheet_name, encoding, delimiter, row_count, imported_utc
            FROM {s}.import_batch_source
            WHERE batch_id = @batchId
            ORDER BY source_no;
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@batchId", batchId);

        var sources = new List<ImportSourceInfo>();
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sources.Add(new ImportSourceInfo(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5),
                DateTimeOffset.Parse(reader.GetString(6))));
        }

        return sources;
    }
}
