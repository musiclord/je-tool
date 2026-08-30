using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 匯入批次的本地引擎實作（guide §3.1.4 多來源模型）。
/// row_number = 批次內單調遞增排序鍵：replace 沿用來源列號（與第 1 版語意一致，
/// 單來源批次的 V3 抽樣不因本版而改變），append 從既有最大值續編。
/// 診斷日誌（dev-only）:SQL 執行走 <see cref="DiagnosticDb"/> 擴充方法、transaction 走 scope。
/// </summary>
public sealed class LocalImportRepository(ILocalProjectDatabase database, ILogger<LocalImportRepository>? logger = null)
    : IImportRepository
{
    // 診斷 provider 標籤由方言注入（sqlite／duckdb），不再寫死——避免 DuckDB 執行被誤標成 sqlite。
    private readonly string _provider = database.Dialect.ProviderName;
    private readonly ILogger _log = logger ?? NullLogger<LocalImportRepository>.Instance;

    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    /// <summary>staging 批量寫入的欄位順序（各引擎的 <see cref="IBulkRowWriter"/> 依此對位）。</summary>
    private static readonly string[] StagingColumns =
        ["batch_id", "row_number", "source_no", "source_row_number", "row_json"];

    public async Task<ImportBatchResult> ReplaceBatchAsync(
        string projectId,
        DatasetKind kind,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken)
    {
        source = ImportSourceFileName.Normalize(source);
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        var importStopwatch = Stopwatch.StartNew();

        var batchId = Guid.NewGuid().ToString("N");
        var importedUtc = DateTimeOffset.UtcNow;
        var kindName = kind.ToStorageName();
        var stagingTable = StagingTableFor(kind);
        var targetTable = TargetTableFor(kind);
        var fieldDefinitions = LegacyFieldDefinitionAccumulator.Create(columns);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await database.ApplyImportSessionSettingsAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        using var txLog = DiagnosticDb.BeginTransaction(_log, _provider);

        // replace 語意：同一 transaction 內清除該 dataset 的全部舊狀態，
        // 包含 target rows 與 committed mapping（重匯入使配對失效）。
        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText =
                $"""
                DELETE FROM {stagingTable}
                WHERE batch_id IN (SELECT batch_id FROM import_batch WHERE dataset_kind = @kind);
                DELETE FROM import_field_definition
                WHERE batch_id IN (SELECT batch_id FROM import_batch WHERE dataset_kind = @kind);
                DELETE FROM import_batch_source
                WHERE batch_id IN (SELECT batch_id FROM import_batch WHERE dataset_kind = @kind);
                DELETE FROM import_batch WHERE dataset_kind = @kind;
                DELETE FROM target_gl_rde_value WHERE @kind = 'gl';
                DELETE FROM config_gl_rde_field WHERE @kind = 'gl';
                DELETE FROM {targetTable};
                DELETE FROM config_field_mapping WHERE dataset_kind = @kind;
                """;
            cleanup.AddWithValue("@kind", kindName);
            await cleanup.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
        }

        // target 已換,既有規則結果即失效(plan Phase 1)。
        await RuleRunResultReset.ClearWithinAsync(
            connection,
            transaction,
            cancellationToken,
            kind == DatasetKind.Gl
                ? AuditMutation.GlImport
                : AuditMutation.TbImport);

        await using (var insertBatch = connection.CreateCommand())
        {
            insertBatch.Transaction = transaction;
            insertBatch.CommandText =
                """
                INSERT INTO import_batch
                    (batch_id, dataset_kind, source_file_path, source_file_name, imported_utc, row_count, columns_json)
                VALUES (@batchId, @kind, @filePath, @fileName, @importedUtc, 0, @columnsJson);
                """;
            insertBatch.AddWithValue("@batchId", batchId);
            insertBatch.AddWithValue("@kind", kindName);
            // source_file_path 是 legacy 實體欄名；本地案件只保存可攜的檔名，
            // 不把來源機器的絕對路徑帶進可整夾搬移的專案資料庫。
            insertBatch.AddWithValue("@filePath", source.FileName);
            insertBatch.AddWithValue("@fileName", source.FileName);
            insertBatch.AddWithValue("@importedUtc", importedUtc.ToString("O"));
            insertBatch.AddWithValue("@columnsJson", JsonSerializer.Serialize(columns, JsonOptions));
            await insertBatch.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
        }

        await InsertSourceRecordAsync(connection, transaction, batchId, sourceNo: 1, source, importedUtc, cancellationToken);

        // replace 的 row_number 直接沿用來源列號（單調且唯一；標頭為列 1，資料列由 2 起）
        // 逐列 INSERT 不逐筆記事件（百萬列會爆 ring buffer、且與 SqlServer 的 SqlBulkCopy 路徑不等價）；
        // 改以階段結束後一筆 staging milestone 收斂。
        var rowCount = 0;
        var observedKeys = new HashSet<string>(StringComparer.Ordinal);
        var stagingStopwatch = Stopwatch.StartNew();
        // 批量列寫入（spec §7）：SQLite 包裝參數化 INSERT（行為凍結）、DuckDB 走 Appender。值依 StagingColumns 對位。
        await using (var writer = database.CreateBulkRowWriter(connection, transaction, stagingTable, StagingColumns))
        {
            // AppendAsync 完成時已消費 values；重用 buffer，避免大型匯入每列配置 5 欄 object[]。
            var stagingValues = new object?[StagingColumns.Length];
            stagingValues[0] = batchId;
            stagingValues[2] = 1;
            await foreach (var row in rows.WithCancellation(cancellationToken))
            {
                stagingValues[1] = row.SourceRowNumber;
                stagingValues[3] = row.SourceRowNumber;
                stagingValues[4] = JsonSerializer.Serialize(row.Values, JsonOptions);
                await writer.AppendAsync(stagingValues, cancellationToken);
                observedKeys.UnionWith(row.Values.Keys);
                fieldDefinitions.Observe(row);
                rowCount++;
            }

            await writer.CompleteAsync(cancellationToken);
        }

        stagingStopwatch.Stop();

        if (rowCount == 0)
        {
            // rollback 保留前一批資料（若有）
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            throw new JetActionException(
                JetErrorCodes.EmptyWorkbook,
                $"檔案 '{source.FileName}' 沒有任何資料列。");
        }

        DiagnosticDbLog.ImportMilestone(_log, "staging", rowCount, stagingStopwatch.ElapsedMilliseconds,
            rowCount * 1000.0 / Math.Max(1, stagingStopwatch.ElapsedMilliseconds));

        // 欄位收斂（guide §3.1.5）：佔位欄資格要看完整串流才知道，批次欄位於同一交易內回寫
        var effectiveColumns = TabularHeaderNormalizer.FinalizeBatchColumns(columns, observedKeys);
        var sourceDefinitions = fieldDefinitions.Build(effectiveColumns);
        await using (var updateColumns = connection.CreateCommand())
        {
            updateColumns.Transaction = transaction;
            updateColumns.CommandText = "UPDATE import_batch SET columns_json = @columnsJson WHERE batch_id = @batchId;";
            updateColumns.AddWithValue("@columnsJson", JsonSerializer.Serialize(effectiveColumns, JsonOptions));
            updateColumns.AddWithValue("@batchId", batchId);
            await updateColumns.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
        }

        await LocalFieldDefinitionPersistence.ReplaceScopeAsync(
            connection,
            transaction,
            batchId,
            LegacyFieldDefinitionScope.Source,
            sourceDefinitions,
            cancellationToken);
        await LocalFieldDefinitionPersistence.DeleteScopeAsync(
            connection,
            transaction,
            batchId,
            LegacyFieldDefinitionScope.Target,
            cancellationToken);

        await UpdateRowCountsAsync(connection, transaction, batchId, sourceNo: 1, addedRowCount: rowCount, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        txLog.Committed();
        DiagnosticDbLog.ImportMilestone(_log, "replace", rowCount, importStopwatch.ElapsedMilliseconds,
            rowCount * 1000.0 / Math.Max(1, importStopwatch.ElapsedMilliseconds));

        var sources = new[] { ToSourceInfo(sourceNo: 1, source, rowCount, importedUtc) };
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
        source = ImportSourceFileName.Normalize(source);
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        var importStopwatch = Stopwatch.StartNew();

        var kindName = kind.ToStorageName();
        var stagingTable = StagingTableFor(kind);
        var targetTable = TargetTableFor(kind);
        var importedUtc = DateTimeOffset.UtcNow;

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await database.ApplyImportSessionSettingsAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        using var txLog = DiagnosticDb.BeginTransaction(_log, _provider);

        string batchId;
        string batchFileName;
        DateTimeOffset batchImportedUtc;
        int existingRowCount;
        IReadOnlyList<string> batchColumns;

        await using (var findBatch = connection.CreateCommand())
        {
            findBatch.Transaction = transaction;
            findBatch.CommandText =
                """
                SELECT batch_id, source_file_name, imported_utc, row_count, columns_json
                FROM import_batch
                WHERE dataset_kind = @kind
                ORDER BY imported_utc DESC, batch_id DESC
                LIMIT 1;
                """;
            findBatch.AddWithValue("@kind", kindName);

            await using var reader = await findBatch.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new JetActionException(
                    JetErrorCodes.NoImportBatch,
                    $"尚未匯入任何 {kindName.ToUpperInvariant()} 資料，無法附加來源；第一個來源請以 mode 'replace' 匯入。");
            }

            batchId = reader.GetString(0);
            batchFileName = reader.GetString(1);
            batchImportedUtc = DateTimeOffset.Parse(reader.GetString(2));
            existingRowCount = reader.GetInt32(3);
            batchColumns = JsonSerializer.Deserialize<List<string>>(reader.GetString(4), JsonOptions) ?? [];
        }

        // 兩階段驗證之一（guide §3.1.4）：串流前只比具名標頭——佔位欄是否屬有效欄位
        // 要看完整串流才知道，但具名集合不合可以立即失敗，不浪費一次大檔讀取
        EnsureColumnSetsMatch(
            source.FileName,
            batchColumns.Where(c => !TabularHeaderNormalizer.IsPlaceholder(c)).ToList(),
            columns.Where(c => !TabularHeaderNormalizer.IsPlaceholder(c)).ToList());

        var persistedDefinitions = await LocalFieldDefinitionPersistence.ReadStatesAsync(
            connection,
            transaction,
            batchId,
            LegacyFieldDefinitionScope.Source,
            cancellationToken);
        if (persistedDefinitions.Count == 0)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidProjectSchema,
                $"{kindName.ToUpperInvariant()} 批次缺少 Legacy 欄位定義；本版不反推舊批次，請以 mode 'replace' 重新匯入。");
        }

        var fieldDefinitions = LegacyFieldDefinitionAccumulator.Restore(persistedDefinitions);

        int nextSourceNo;
        long nextRowNumber;

        await using (var maxQuery = connection.CreateCommand())
        {
            maxQuery.Transaction = transaction;
            maxQuery.CommandText =
                $"""
                SELECT
                    (SELECT COALESCE(MAX(source_no), 0) FROM import_batch_source WHERE batch_id = @batchId),
                    (SELECT COALESCE(MAX(row_number), 0) FROM {stagingTable} WHERE batch_id = @batchId);
                """;
            maxQuery.AddWithValue("@batchId", batchId);

            await using var reader = await maxQuery.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken);
            await reader.ReadAsync(cancellationToken);
            nextSourceNo = reader.GetInt32(0) + 1;
            nextRowNumber = reader.GetInt64(1) + 1;
        }

        await InsertSourceRecordAsync(connection, transaction, batchId, nextSourceNo, source, importedUtc, cancellationToken);

        // 逐列 INSERT 不逐筆記事件（同 replace；與 SqlServer SqlBulkCopy 路徑等價）→ 階段 milestone 收斂。
        var addedRowCount = 0;
        var observedKeys = new HashSet<string>(StringComparer.Ordinal);
        var stagingStopwatch = Stopwatch.StartNew();
        // 批量列寫入（spec §7）：row_number 由既有最大值續編（append 語意），source_no = nextSourceNo。
        await using (var writer = database.CreateBulkRowWriter(connection, transaction, stagingTable, StagingColumns))
        {
            var stagingValues = new object?[StagingColumns.Length];
            stagingValues[0] = batchId;
            stagingValues[2] = nextSourceNo;
            await foreach (var row in rows.WithCancellation(cancellationToken))
            {
                stagingValues[1] = nextRowNumber++;
                stagingValues[3] = row.SourceRowNumber;
                stagingValues[4] = JsonSerializer.Serialize(row.Values, JsonOptions);
                await writer.AppendAsync(stagingValues, cancellationToken);
                observedKeys.UnionWith(row.Values.Keys);
                fieldDefinitions.Observe(row);
                addedRowCount++;
            }

            await writer.CompleteAsync(cancellationToken);
        }

        stagingStopwatch.Stop();

        if (addedRowCount == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            throw new JetActionException(
                JetErrorCodes.EmptyWorkbook,
                $"檔案 '{source.FileName}' 沒有任何資料列，未附加（既有批次不受影響）。");
        }

        DiagnosticDbLog.ImportMilestone(_log, "staging", addedRowCount, stagingStopwatch.ElapsedMilliseconds,
            addedRowCount * 1000.0 / Math.Max(1, stagingStopwatch.ElapsedMilliseconds));

        // 兩階段驗證之二：串流後以收斂後的有效欄位集合終檢——佔位欄帶資料而批次沒有
        //（或反向）必須誠實拒絕，有資料的欄位不得靜默消失。批次欄位不因附加改寫（批次為權威）
        var effectiveColumns = TabularHeaderNormalizer.FinalizeBatchColumns(columns, observedKeys);
        try
        {
            EnsureColumnSetsMatch(source.FileName, batchColumns, effectiveColumns);
        }
        catch (JetActionException)
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            throw;
        }

        var sourceDefinitions = fieldDefinitions.Build(batchColumns);
        await LocalFieldDefinitionPersistence.ReplaceScopeAsync(
            connection,
            transaction,
            batchId,
            LegacyFieldDefinitionScope.Source,
            sourceDefinitions,
            cancellationToken);
        await LocalFieldDefinitionPersistence.DeleteScopeAsync(
            connection,
            transaction,
            batchId,
            LegacyFieldDefinitionScope.Target,
            cancellationToken);

        await UpdateRowCountsAsync(connection, transaction, batchId, nextSourceNo, addedRowCount, cancellationToken);

        // 附加使下游失效（與 replace 同語意）：母體變了，target 投影與已提交配對必須重做
        await using (var invalidate = connection.CreateCommand())
        {
            invalidate.Transaction = transaction;
            invalidate.CommandText =
                $"""
                DELETE FROM target_gl_rde_value WHERE @kind = 'gl';
                DELETE FROM config_gl_rde_field WHERE @kind = 'gl';
                DELETE FROM {targetTable};
                DELETE FROM config_field_mapping WHERE dataset_kind = @kind;
                """;
            invalidate.AddWithValue("@kind", kindName);
            await invalidate.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
        }

        // 母體變了,target 投影與已提交配對重做,既有規則結果一併失效(plan Phase 1)。
        await RuleRunResultReset.ClearWithinAsync(
            connection,
            transaction,
            cancellationToken,
            kind == DatasetKind.Gl
                ? AuditMutation.GlImport
                : AuditMutation.TbImport);

        var sources = await LoadSourcesAsync(connection, transaction, batchId, cancellationToken);
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
        sources = NormalizeSources(sources);
        EnsureNamedColumnsMatchFirstSource(sources);

        await database.EnsureCreatedAsync(projectId, cancellationToken);
        var importStopwatch = Stopwatch.StartNew();
        var first = sources[0];
        var batchId = Guid.NewGuid().ToString("N");
        var batchImportedUtc = DateTimeOffset.UtcNow;
        var kindName = kind.ToStorageName();
        var stagingTable = StagingTableFor(kind);
        var targetTable = TargetTableFor(kind);
        var fieldDefinitions = LegacyFieldDefinitionAccumulator.Create(first.Columns);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await database.ApplyImportSessionSettingsAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        using var txLog = DiagnosticDb.BeginTransaction(_log, _provider);

        try
        {
            await using (var cleanup = connection.CreateCommand())
            {
                cleanup.Transaction = transaction;
                cleanup.CommandText =
                    $"""
                    DELETE FROM {stagingTable}
                    WHERE batch_id IN (SELECT batch_id FROM import_batch WHERE dataset_kind = @kind);
                    DELETE FROM import_field_definition
                    WHERE batch_id IN (SELECT batch_id FROM import_batch WHERE dataset_kind = @kind);
                    DELETE FROM import_batch_source
                    WHERE batch_id IN (SELECT batch_id FROM import_batch WHERE dataset_kind = @kind);
                    DELETE FROM import_batch WHERE dataset_kind = @kind;
                    DELETE FROM target_gl_rde_value WHERE @kind = 'gl';
                    DELETE FROM config_gl_rde_field WHERE @kind = 'gl';
                    DELETE FROM {targetTable};
                    DELETE FROM config_field_mapping WHERE dataset_kind = @kind;
                    """;
                cleanup.AddWithValue("@kind", kindName);
                await cleanup.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
            }

            await RuleRunResultReset.ClearWithinAsync(
                connection,
                transaction,
                cancellationToken,
                kind == DatasetKind.Gl ? AuditMutation.GlImport : AuditMutation.TbImport);

            await using (var insertBatch = connection.CreateCommand())
            {
                insertBatch.Transaction = transaction;
                insertBatch.CommandText =
                    """
                    INSERT INTO import_batch
                        (batch_id, dataset_kind, source_file_path, source_file_name, imported_utc, row_count, columns_json)
                    VALUES (@batchId, @kind, @filePath, @fileName, @importedUtc, 0, @columnsJson);
                    """;
                insertBatch.AddWithValue("@batchId", batchId);
                insertBatch.AddWithValue("@kind", kindName);
                insertBatch.AddWithValue("@filePath", first.Source.FileName);
                insertBatch.AddWithValue("@fileName", first.Source.FileName);
                insertBatch.AddWithValue("@importedUtc", batchImportedUtc.ToString("O"));
                insertBatch.AddWithValue("@columnsJson", JsonSerializer.Serialize(first.Columns, JsonOptions));
                await insertBatch.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
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
                        batchId,
                        sourceNo,
                        input.Source,
                        importedUtc,
                        cancellationToken);

                    var write = await WriteBatchSourceAsync(
                        connection,
                        transaction,
                        stagingTable,
                        batchId,
                        sourceNo,
                        preserveSourceRowNumber: index == 0,
                        nextRowNumber,
                        input.Rows,
                        fieldDefinitions,
                        cancellationToken);
                    nextRowNumber = write.NextRowNumber;

                    if (write.RowCount == 0)
                    {
                        throw new JetActionException(
                            JetErrorCodes.EmptyWorkbook,
                            $"檔案 '{input.Source.FileName}' 沒有任何資料列。");
                    }

                    var effectiveColumns = TabularHeaderNormalizer.FinalizeBatchColumns(
                        input.Columns,
                        write.ObservedKeys);
                    if (index == 0)
                    {
                        batchColumns = effectiveColumns;
                        await using var updateColumns = connection.CreateCommand();
                        updateColumns.Transaction = transaction;
                        updateColumns.CommandText =
                            "UPDATE import_batch SET columns_json = @columnsJson WHERE batch_id = @batchId;";
                        updateColumns.AddWithValue(
                            "@columnsJson",
                            JsonSerializer.Serialize(batchColumns, JsonOptions));
                        updateColumns.AddWithValue("@batchId", batchId);
                        await updateColumns.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
                    }
                    else
                    {
                        EnsureColumnSetsMatch(input.Source.FileName, batchColumns, effectiveColumns);
                    }

                    await UpdateRowCountsAsync(
                        connection,
                        transaction,
                        batchId,
                        sourceNo,
                        write.RowCount,
                        cancellationToken);
                    totalAddedRowCount += write.RowCount;
                }
                catch (JetActionException error)
                {
                    throw AddBatchSourceContext(error, input.Source, sourceNo, sources.Count);
                }
            }

            await LocalFieldDefinitionPersistence.ReplaceScopeAsync(
                connection,
                transaction,
                batchId,
                LegacyFieldDefinitionScope.Source,
                fieldDefinitions.Build(batchColumns),
                cancellationToken);
            await LocalFieldDefinitionPersistence.DeleteScopeAsync(
                connection,
                transaction,
                batchId,
                LegacyFieldDefinitionScope.Target,
                cancellationToken);

            var importedSources = await LoadSourcesAsync(
                connection,
                transaction,
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
        sources = NormalizeSources(sources);
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        var importStopwatch = Stopwatch.StartNew();
        var kindName = kind.ToStorageName();
        var stagingTable = StagingTableFor(kind);
        var targetTable = TargetTableFor(kind);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await database.ApplyImportSessionSettingsAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        using var txLog = DiagnosticDb.BeginTransaction(_log, _provider);

        try
        {
            string batchId;
            string batchFileName;
            DateTimeOffset batchImportedUtc;
            int existingRowCount;
            IReadOnlyList<string> batchColumns;

            await using (var findBatch = connection.CreateCommand())
            {
                findBatch.Transaction = transaction;
                findBatch.CommandText =
                    """
                    SELECT batch_id, source_file_name, imported_utc, row_count, columns_json
                    FROM import_batch
                    WHERE dataset_kind = @kind
                    ORDER BY imported_utc DESC, batch_id DESC
                    LIMIT 1;
                    """;
                findBatch.AddWithValue("@kind", kindName);

                await using var reader = await findBatch.ExecuteReaderLoggedAsync(
                    _log,
                    _provider,
                    cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new JetActionException(
                        JetErrorCodes.NoImportBatch,
                        $"尚未匯入任何 {kindName.ToUpperInvariant()} 資料，無法附加來源；第一個來源請以 mode 'replace' 匯入。");
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
                    EnsureColumnSetsMatch(
                        input.Source.FileName,
                        namedBatchColumns,
                        input.Columns.Where(column => !TabularHeaderNormalizer.IsPlaceholder(column)).ToList());
                }
                catch (JetActionException error)
                {
                    throw AddBatchSourceContext(error, input.Source, index + 1, sources.Count);
                }
            }

            var persistedDefinitions = await LocalFieldDefinitionPersistence.ReadStatesAsync(
                connection,
                transaction,
                batchId,
                LegacyFieldDefinitionScope.Source,
                cancellationToken);
            if (persistedDefinitions.Count == 0)
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidProjectSchema,
                    $"{kindName.ToUpperInvariant()} 批次缺少 Legacy 欄位定義；本版不反推舊批次，請以 mode 'replace' 重新匯入。");
            }

            var fieldDefinitions = LegacyFieldDefinitionAccumulator.Restore(persistedDefinitions);
            int nextSourceNo;
            long nextRowNumber;
            await using (var maxQuery = connection.CreateCommand())
            {
                maxQuery.Transaction = transaction;
                maxQuery.CommandText =
                    $"""
                    SELECT
                        (SELECT COALESCE(MAX(source_no), 0) FROM import_batch_source WHERE batch_id = @batchId),
                        (SELECT COALESCE(MAX(row_number), 0) FROM {stagingTable} WHERE batch_id = @batchId);
                    """;
                maxQuery.AddWithValue("@batchId", batchId);
                await using var reader = await maxQuery.ExecuteReaderLoggedAsync(
                    _log,
                    _provider,
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
                        batchId,
                        sourceNo,
                        input.Source,
                        DateTimeOffset.UtcNow,
                        cancellationToken);
                    var write = await WriteBatchSourceAsync(
                        connection,
                        transaction,
                        stagingTable,
                        batchId,
                        sourceNo,
                        preserveSourceRowNumber: false,
                        nextRowNumber,
                        input.Rows,
                        fieldDefinitions,
                        cancellationToken);
                    nextRowNumber = write.NextRowNumber;

                    if (write.RowCount == 0)
                    {
                        throw new JetActionException(
                            JetErrorCodes.EmptyWorkbook,
                            $"檔案 '{input.Source.FileName}' 沒有任何資料列，未附加（既有批次不受影響）。");
                    }

                    var effectiveColumns = TabularHeaderNormalizer.FinalizeBatchColumns(
                        input.Columns,
                        write.ObservedKeys);
                    EnsureColumnSetsMatch(input.Source.FileName, batchColumns, effectiveColumns);
                    await UpdateRowCountsAsync(
                        connection,
                        transaction,
                        batchId,
                        sourceNo,
                        write.RowCount,
                        cancellationToken);
                    totalAddedRowCount += write.RowCount;
                }
                catch (JetActionException error)
                {
                    throw AddBatchSourceContext(error, input.Source, index + 1, sources.Count);
                }
            }

            await LocalFieldDefinitionPersistence.ReplaceScopeAsync(
                connection,
                transaction,
                batchId,
                LegacyFieldDefinitionScope.Source,
                fieldDefinitions.Build(batchColumns),
                cancellationToken);
            await LocalFieldDefinitionPersistence.DeleteScopeAsync(
                connection,
                transaction,
                batchId,
                LegacyFieldDefinitionScope.Target,
                cancellationToken);

            await using (var invalidate = connection.CreateCommand())
            {
                invalidate.Transaction = transaction;
                invalidate.CommandText =
                    $"""
                    DELETE FROM target_gl_rde_value WHERE @kind = 'gl';
                    DELETE FROM config_gl_rde_field WHERE @kind = 'gl';
                    DELETE FROM {targetTable};
                    DELETE FROM config_field_mapping WHERE dataset_kind = @kind;
                    """;
                invalidate.AddWithValue("@kind", kindName);
                await invalidate.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
            }

            await RuleRunResultReset.ClearWithinAsync(
                connection,
                transaction,
                cancellationToken,
                kind == DatasetKind.Gl ? AuditMutation.GlImport : AuditMutation.TbImport);

            var importedSources = await LoadSourcesAsync(
                connection,
                transaction,
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

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT batch_id, source_file_name, imported_utc, row_count, columns_json
                FROM import_batch
                WHERE dataset_kind = @kind
                ORDER BY imported_utc DESC, batch_id DESC
                LIMIT 1;
                """;
            command.AddWithValue("@kind", kind.ToStorageName());

            await using var reader = await command.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken);
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

        var sources = await LoadSourcesAsync(connection, transaction: null, batchId, cancellationToken);

        return new ImportBatchInfo(batchId, kind, fileName, importedUtc, rowCount, columns, sources);
    }

    /// <summary>欄名「集合」一致性（順序無關；欄序以第一個來源為準）。不一致 → column_mismatch。</summary>
    internal static void EnsureColumnSetsMatch(
        string sourceFileName,
        IReadOnlyList<string> batchColumns,
        IReadOnlyList<string> sourceColumns)
    {
        var batchSet = new HashSet<string>(batchColumns, StringComparer.Ordinal);
        var sourceSet = new HashSet<string>(sourceColumns, StringComparer.Ordinal);

        if (batchSet.SetEquals(sourceSet))
        {
            return;
        }

        var extra = sourceColumns.Where(c => !batchSet.Contains(c)).ToList();
        var missing = batchColumns.Where(c => !sourceSet.Contains(c)).ToList();

        var parts = new List<string>();
        if (extra.Count > 0)
        {
            parts.Add($"來源多出：{string.Join("、", extra)}");
        }

        if (missing.Count > 0)
        {
            parts.Add($"來源缺少：{string.Join("、", missing)}");
        }

        throw new JetActionException(
            JetErrorCodes.ColumnMismatch,
            $"檔案 '{sourceFileName}' 的欄位集合與既有批次不一致（{string.Join("；", parts)}）。");
    }

    private async Task<BatchSourceWriteResult> WriteBatchSourceAsync(
        DbConnection connection,
        DbTransaction transaction,
        string stagingTable,
        string batchId,
        int sourceNo,
        bool preserveSourceRowNumber,
        long nextRowNumber,
        IAsyncEnumerable<StagingRow> rows,
        LegacyFieldDefinitionAccumulator fieldDefinitions,
        CancellationToken cancellationToken)
    {
        var rowCount = 0;
        var observedKeys = new HashSet<string>(StringComparer.Ordinal);
        var stagingStopwatch = Stopwatch.StartNew();
        await using (var writer = database.CreateBulkRowWriter(
            connection,
            transaction,
            stagingTable,
            StagingColumns))
        {
            var values = new object?[StagingColumns.Length];
            values[0] = batchId;
            values[2] = sourceNo;
            await foreach (var row in rows.WithCancellation(cancellationToken))
            {
                if (preserveSourceRowNumber)
                {
                    values[1] = row.SourceRowNumber;
                    nextRowNumber = Math.Max(nextRowNumber, (long)row.SourceRowNumber + 1);
                }
                else
                {
                    values[1] = nextRowNumber++;
                }

                values[3] = row.SourceRowNumber;
                values[4] = JsonSerializer.Serialize(row.Values, JsonOptions);
                await writer.AppendAsync(values, cancellationToken);
                observedKeys.UnionWith(row.Values.Keys);
                fieldDefinitions.Observe(row);
                rowCount++;
            }

            await writer.CompleteAsync(cancellationToken);
        }

        stagingStopwatch.Stop();
        DiagnosticDbLog.ImportMilestone(
            _log,
            "staging",
            rowCount,
            stagingStopwatch.ElapsedMilliseconds,
            rowCount * 1000.0 / Math.Max(1, stagingStopwatch.ElapsedMilliseconds));
        return new BatchSourceWriteResult(rowCount, observedKeys, nextRowNumber);
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
                EnsureColumnSetsMatch(
                    input.Source.FileName,
                    firstNamedColumns,
                    input.Columns.Where(column => !TabularHeaderNormalizer.IsPlaceholder(column)).ToList());
            }
            catch (JetActionException error)
            {
                throw AddBatchSourceContext(error, input.Source, index + 1, sources.Count);
            }
        }
    }

    internal static JetActionException AddBatchSourceContext(
        JetActionException error,
        ImportSourceDescriptor source,
        int sourceNo,
        int sourceCount)
    {
        if (sourceCount == 1
            || (error.Message.Contains(source.FileName, StringComparison.Ordinal)
                && (source.SheetName is null
                    || error.Message.Contains(source.SheetName, StringComparison.Ordinal))))
        {
            return error;
        }

        var context = source.SheetName is null
            ? $"來源 {sourceNo}/{sourceCount}，檔案 '{source.FileName}'"
            : $"來源 {sourceNo}/{sourceCount}，檔案 '{source.FileName}'，工作表 '{source.SheetName}'";
        return new JetActionException(error.Code, $"{context}：{error.Message}");
    }

    private static async Task RollbackQuietlyAsync(DbTransaction transaction)
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

    private sealed record BatchSourceWriteResult(
        int RowCount,
        HashSet<string> ObservedKeys,
        long NextRowNumber);

    private static IReadOnlyList<ImportSourceInput> NormalizeSources(
        IReadOnlyList<ImportSourceInput> sources) =>
        sources
            .Select(input => input with { Source = ImportSourceFileName.Normalize(input.Source) })
            .ToArray();

    private async Task InsertSourceRecordAsync(
        DbConnection connection,
        DbTransaction transaction,
        string batchId,
        int sourceNo,
        ImportSourceDescriptor source,
        DateTimeOffset importedUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO import_batch_source
                (batch_id, source_no, source_file_path, source_file_name, sheet_name, encoding, delimiter, row_count, imported_utc)
            VALUES (@batchId, @sourceNo, @filePath, @fileName, @sheetName, @encoding, @delimiter, 0, @importedUtc);
            """;
        command.AddWithValue("@batchId", batchId);
        command.AddWithValue("@sourceNo", sourceNo);
        command.AddWithValue("@filePath", source.FileName);
        command.AddWithValue("@fileName", source.FileName);
        command.AddWithValue("@sheetName", (object?)source.SheetName ?? DBNull.Value);
        command.AddWithValue("@encoding", (object?)source.EncodingName ?? DBNull.Value);
        command.AddWithValue("@delimiter", (object?)source.Delimiter ?? DBNull.Value);
        command.AddWithValue("@importedUtc", importedUtc.ToString("O"));
        await command.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
    }

    private async Task UpdateRowCountsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string batchId,
        int sourceNo,
        int addedRowCount,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE import_batch_source SET row_count = @added WHERE batch_id = @batchId AND source_no = @sourceNo;
            UPDATE import_batch SET row_count = row_count + @added WHERE batch_id = @batchId;
            """;
        command.AddWithValue("@added", addedRowCount);
        command.AddWithValue("@batchId", batchId);
        command.AddWithValue("@sourceNo", sourceNo);
        await command.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
    }

    private async Task<IReadOnlyList<ImportSourceInfo>> LoadSourcesAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string batchId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT source_no, source_file_name, sheet_name, encoding, delimiter, row_count, imported_utc
            FROM import_batch_source
            WHERE batch_id = @batchId
            ORDER BY source_no;
            """;
        command.AddWithValue("@batchId", batchId);

        var sources = new List<ImportSourceInfo>();
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken);
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

    internal static ImportSourceInfo ToSourceInfo(
        int sourceNo,
        ImportSourceDescriptor source,
        int rowCount,
        DateTimeOffset importedUtc)
    {
        return new ImportSourceInfo(
            sourceNo, source.FileName, source.SheetName, source.EncodingName, source.Delimiter, rowCount, importedUtc);
    }

    internal static string StagingTableFor(DatasetKind kind) => kind switch
    {
        DatasetKind.Gl => "staging_gl_raw_row",
        DatasetKind.Tb => "staging_tb_raw_row",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    internal static string TargetTableFor(DatasetKind kind) => kind switch
    {
        DatasetKind.Gl => "target_gl_entry",
        DatasetKind.Tb => "target_tb_balance",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
