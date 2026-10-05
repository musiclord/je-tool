using System.Diagnostics;
using System.Data;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// GL staging → target 投影的 SQL Server 實作(對應 <see cref="LocalGlRepository"/>)。
/// 重用 Domain 純函式 <see cref="GlRowProjector"/>/<see cref="MoneyScaling"/>(零 DB 耦合),
/// 差異僅在:批次插入用 <see cref="SqlBulkCopy"/>、以串流投影 reader 餵入。
///
/// 連線拆兩條:staging 為已提交的不可變上游,於獨立 read 連線串流;DELETE target /
/// 結果失效 / bulk insert 在 write 連線的單一交易內完成(避免同連線同時開 reader 又寫入的
/// MARS 限制)。錯誤/原子性語意與 SQLite 一致:任一列投影失敗 → 整批 rollback、回 (0, errors)。
/// 診斷日誌（dev-only）：一次性 clear/select 走 <see cref="DiagnosticDb"/>、transaction 走 scope；
/// SqlBulkCopy 不逐列記事件，改以投影結束後一筆 projection.milestone 收斂（與 SQLite 事件等價）。
/// </summary>
public sealed class SqlServerGlRepository(SqlServerProjectDatabase database, ILogger<SqlServerGlRepository>? logger = null)
    : IGlRepository
{
    private const string Provider = "sqlServer";
    private const int ProgressRowInterval = 20_000;

    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    private readonly ILogger _log = logger ?? NullLogger<SqlServerGlRepository>.Instance;

    public async Task<ProjectionResult> ProjectStagingToTargetAsync(
        string projectId,
        string batchId,
        GlMappingSpec spec,
        int moneyScale,
        DateParseOptions dateOptions,
        DateOnly periodStart,
        DateOnly periodEnd,
        bool postingStatusMapped,
        GlPostingStatusPolicy? postingStatusPolicy,
        DateTimeOffset committedUtc,
        CancellationToken cancellationToken,
        Action<ProjectionProgress>? progress = null)
    {
        var mappingHasPostingStatus = spec.Mapping.TryGetValue(
                GlMappingKeys.PostingStatus,
                out var postingStatusColumn)
            && !string.IsNullOrWhiteSpace(postingStatusColumn);
        if (mappingHasPostingStatus != postingStatusMapped)
        {
            throw new InvalidOperationException(
                "GL projection plan 的 PostingStatusMapped 與 mapping 不一致。");
        }
        var canonicalPostingStatusPolicy = GlEffectivePopulation.NormalizePolicy(
            postingStatusMapped,
            postingStatusPolicy);
        var projectionOptions = spec.Options with
        {
            PostingStatusPolicy = canonicalPostingStatusPolicy
        };

        await database.EnsureCreatedAsync(projectId, cancellationToken);
        var stopwatch = Stopwatch.StartNew();

        // write 連線:DELETE target + 結果失效 + bulk insert,單一交易(全有或全無)。
        await using var writeConnection = database.CreateConnection(projectId);
        await writeConnection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await writeConnection.BeginTransactionAsync(cancellationToken);
        using var txLog = DiagnosticDb.BeginTransaction(_log, Provider);

        await using (var clear = database.CreateCommand(
                         writeConnection,
                         projectId,
                         "DELETE FROM {s}.target_gl_rde_value; "
                         + "DELETE FROM {s}.config_gl_rde_field; "
                         + "DELETE FROM {s}.target_gl_entry;"))
        {
            clear.Transaction = transaction;
            await clear.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        // 重投影改寫 target,既有規則結果失效(投影失敗 rollback 時清除一併回退)。
        await RuleRunResultReset.ClearWithinAsync(
            writeConnection,
            transaction,
            cancellationToken,
            AuditMutation.GlProjection,
            SqlServerProjectSchema.QualifierFor(projectId));

        var sourceLabels = await ProjectionSourceLabels.LoadAsync(writeConnection, transaction, batchId, cancellationToken, SqlServerProjectSchema.QualifierFor(projectId));

        // read 連線:staging 已提交不可變,獨立連線串流(不參與 write 交易,避免 MARS 衝突)。
        await using var readConnection = database.CreateConnection(projectId);
        await readConnection.OpenAsync(cancellationToken);
        await using var select = database.CreateCommand(readConnection, projectId,
            """
            SELECT row_number, source_no, source_row_number, row_json
            FROM {s}.staging_gl_raw_row
            WHERE batch_id = @batchId
            ORDER BY row_number;
            """);
        var batchParam = select.CreateParameter();
        batchParam.ParameterName = "@batchId";
        batchParam.Value = batchId;
        select.Parameters.Add(batchParam);

        await using var stagingReader = await select.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        using var projectionReader = new GlProjectionDataReader(
            stagingReader, batchId, spec, moneyScale, dateOptions,
            periodStart, periodEnd, postingStatusMapped, canonicalPostingStatusPolicy,
            sourceLabels, JsonOptions, cancellationToken,
            progress, ProgressRowInterval);

        var schema = SqlServerProjectSchema.For(projectId);
        using (var bulk = new SqlBulkCopy(writeConnection, SqlBulkCopyOptions.Default, transaction))
        {
            bulk.DestinationTableName = $"[{schema}].[target_gl_entry]";
            bulk.EnableStreaming = true;
            bulk.BulkCopyTimeout = 0; // 大資料路徑不設逾時
            foreach (var column in GlProjectionDataReader.ColumnNames)
            {
                bulk.ColumnMappings.Add(column, column);
            }

            await bulk.WriteToServerAsync(projectionReader, cancellationToken);
        }

        projectionReader.ReportFinalProgress();

        if (projectionReader.TotalErrorCount > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            return projectionReader.FailedResult();
        }

        if (projectionReader.EffectiveRowCount == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            throw new JetActionException(
                JetErrorCodes.EmptyEffectivePopulation,
                "GL 依案件期間與過帳狀態政策篩選後沒有有效分錄；本次投影已全部 rollback。");
        }

        // 退化母體守門(語意對齊 LocalGlRepository):母體非空但借貸總額皆為 0 → 金額欄誤配,整批 rollback。
        if (GlProjectionGuard.IsDegenerateAmountPopulation(
                projectionReader.EffectiveRowCount,
                projectionReader.EffectiveDebitScaled,
                projectionReader.EffectiveCreditScaled))
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            throw new JetActionException(JetErrorCodes.GlAmountsAllZero, GlProjectionGuard.DegenerateAmountMessage);
        }

        // lineID 未對應:投影後逐傳票自動編號（語意對齊 LocalGlRepository;衍生值、不參與計算）。
        if (!spec.HasLineItem)
        {
            await using var number = database.CreateCommand(writeConnection, projectId,
                """
                WITH c AS (
                    SELECT line_item, line_item_numeric_sort_key,
                           ROW_NUMBER() OVER (PARTITION BY document_number ORDER BY source_row_number) AS rn
                    FROM {s}.target_gl_entry
                )
                UPDATE c
                SET line_item = CAST(rn AS NVARCHAR(20)),
                    line_item_numeric_sort_key =
                        N'2'
                        + RIGHT(
                            REPLICATE(N'0', 10)
                            + CAST(2147483648 + LEN(CAST(rn AS NVARCHAR(20))) AS NVARCHAR(10)),
                            10)
                        + CAST(rn AS NVARCHAR(20))
                        + REPLICATE(N'0', 29 - LEN(CAST(rn AS NVARCHAR(20))));
                """);
            number.Transaction = transaction;
            await number.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        // 完整性測試的匯入控制總數落地（同一交易、commit 之前;MERGE 單列 upsert,語意對齊 SQLite ON CONFLICT）。
        await using (var ct = database.CreateCommand(writeConnection, projectId,
            """
                MERGE {s}.gl_control_total AS target
                USING (SELECT 1 AS singleton, @src AS source_row_count, @tgt AS target_row_count,
                              @debit AS target_debit_scaled, @credit AS target_credit_scaled,
                              @raw AS raw_row_count, @effective AS effective_row_count,
                              @excludedPeriod AS excluded_by_period_count,
                              @excludedPostingStatus AS excluded_by_posting_status_count,
                              @effectiveDebit AS effective_debit_scaled,
                              @effectiveCredit AS effective_credit_scaled) AS source
                ON target.singleton = source.singleton
                WHEN MATCHED THEN UPDATE SET
                    source_row_count = source.source_row_count,
                    target_row_count = source.target_row_count,
                    target_debit_scaled = source.target_debit_scaled,
                    target_credit_scaled = source.target_credit_scaled,
                    raw_row_count = source.raw_row_count,
                    effective_row_count = source.effective_row_count,
                    excluded_by_period_count = source.excluded_by_period_count,
                    excluded_by_posting_status_count = source.excluded_by_posting_status_count,
                    effective_debit_scaled = source.effective_debit_scaled,
                    effective_credit_scaled = source.effective_credit_scaled
                WHEN NOT MATCHED THEN
                    INSERT (singleton, source_row_count, target_row_count, target_debit_scaled, target_credit_scaled,
                            raw_row_count, effective_row_count, excluded_by_period_count,
                            excluded_by_posting_status_count, effective_debit_scaled, effective_credit_scaled)
                    VALUES (source.singleton, source.source_row_count, source.target_row_count,
                            source.target_debit_scaled, source.target_credit_scaled,
                            source.raw_row_count, source.effective_row_count, source.excluded_by_period_count,
                            source.excluded_by_posting_status_count, source.effective_debit_scaled,
                            source.effective_credit_scaled);
                """))
        {
            ct.Transaction = transaction;
            ct.Parameters.AddWithValue("@src", projectionReader.SourceRowCount);
            ct.Parameters.AddWithValue("@tgt", (long)projectionReader.ValidRowCount);
            ct.Parameters.AddWithValue("@debit", projectionReader.TotalDebitScaled);
            ct.Parameters.AddWithValue("@credit", projectionReader.TotalCreditScaled);
            ct.Parameters.AddWithValue("@raw", projectionReader.SourceRowCount);
            ct.Parameters.AddWithValue("@effective", projectionReader.EffectiveRowCount);
            ct.Parameters.AddWithValue("@excludedPeriod", projectionReader.ExcludedByPeriodCount);
            ct.Parameters.AddWithValue("@excludedPostingStatus", projectionReader.ExcludedByPostingStatusCount);
            ct.Parameters.AddWithValue("@effectiveDebit", projectionReader.EffectiveDebitScaled);
            ct.Parameters.AddWithValue("@effectiveCredit", projectionReader.EffectiveCreditScaled);
            await ct.ExecuteNonQueryAsync(cancellationToken);
        }

        // 必填文字欄整欄空白偵測（非阻斷提醒；疑似配錯欄）。語意對齊 SQLite；空白判定用 LTRIM(RTRIM)、
        // 計數 CAST AS BIGINT 避免大母體 INT 溢位。空母體不偵測。
        IReadOnlyList<string> warnings = [];
        if (projectionReader.ValidRowCount > 0)
        {
            var emptyTextColumns = new HashSet<string>();
            await using (var probe = database.CreateCommand(writeConnection, projectId,
                $$"""
                    SELECT
                      SUM(CAST(CASE WHEN document_number IS NOT NULL AND {{SqlServerDialect.Instance.Trim("document_number")}} <> '' THEN 1 ELSE 0 END AS BIGINT)),
                      SUM(CAST(CASE WHEN account_code IS NOT NULL AND {{SqlServerDialect.Instance.Trim("account_code")}} <> '' THEN 1 ELSE 0 END AS BIGINT)),
                      SUM(CAST(CASE WHEN account_name IS NOT NULL AND {{SqlServerDialect.Instance.Trim("account_name")}} <> '' THEN 1 ELSE 0 END AS BIGINT)),
                      SUM(CAST(CASE WHEN document_description IS NOT NULL AND {{SqlServerDialect.Instance.Trim("document_description")}} <> '' THEN 1 ELSE 0 END AS BIGINT))
                    FROM {s}.target_gl_entry;
                    """))
            {
                probe.Transaction = transaction;
                await using var reader = await probe.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                if (reader.GetInt64(0) == 0) { emptyTextColumns.Add("document_number"); }
                if (reader.GetInt64(1) == 0) { emptyTextColumns.Add("account_code"); }
                if (reader.GetInt64(2) == 0) { emptyTextColumns.Add("account_name"); }
                if (reader.GetInt64(3) == 0) { emptyTextColumns.Add("document_description"); }
            }
            warnings = [.. GlMappedColumnAudit.Build(spec, emptyTextColumns), .. projectionReader.ManualAutoCodes.Warnings()];
        }

        await ReplaceRdeProjectionAsync(
            database,
            writeConnection,
            transaction,
            projectId,
            batchId,
            spec,
            moneyScale,
            dateOptions,
            projectionOptions.RdeFields,
            cancellationToken);

        var sourceDefinitions = await SqlServerFieldDefinitionPersistence.LoadStatesAsync(
            database,
            writeConnection,
            transaction,
            projectId,
            batchId,
            LegacyFieldDefinitionScope.Source,
            cancellationToken);
        await SqlServerFieldDefinitionPersistence.ReplaceAsync(
            database,
            writeConnection,
            transaction,
            projectId,
            batchId,
            LegacyFieldDefinitionScope.Target,
            LegacyFieldDefinitionProjector.ProjectGl(sourceDefinitions, spec, moneyScale),
            cancellationToken);

        var committedMapping = new CommittedMapping(
            DatasetKind.Gl,
            spec.Mapping,
            GlAmountModeNames.ToWireName(spec.AmountMode),
            batchId,
            committedUtc,
            MappingMetadataFormat.CurrentVersion,
            projectionOptions);
        await SqlServerMappingStateStore.SaveWithinAsync(
            database,
            writeConnection,
            transaction,
            projectId,
            committedMapping,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        txLog.Committed();
        DiagnosticDbLog.ProjectionMilestone(_log, "gl-projection", projectionReader.ValidRowCount,
            stopwatch.ElapsedMilliseconds,
            projectionReader.ValidRowCount * 1000.0 / Math.Max(1, stopwatch.ElapsedMilliseconds));
        return new ProjectionResult(projectionReader.ValidRowCount, [])
        {
            Warnings = warnings,
            EffectivePopulation = new GlEffectivePopulationTotals(
                projectionReader.SourceRowCount,
                projectionReader.EffectiveRowCount,
                projectionReader.ExcludedByPeriodCount,
                projectionReader.ExcludedByPostingStatusCount,
                projectionReader.EffectiveDebitScaled,
                projectionReader.EffectiveCreditScaled)
        };
    }

    private static async Task ReplaceRdeProjectionAsync(
        SqlServerProjectDatabase database,
        SqlConnection connection,
        SqlTransaction transaction,
        string projectId,
        string batchId,
        GlMappingSpec spec,
        int moneyScale,
        DateParseOptions dateOptions,
        IReadOnlyList<GlRdeFieldMetadata> fields,
        CancellationToken cancellationToken)
    {
        for (var ordinal = 0; ordinal < fields.Count; ordinal++)
        {
            var field = fields[ordinal];
            await using var insertField = database.CreateCommand(
                connection,
                projectId,
                "INSERT INTO {s}.config_gl_rde_field "
                + "(field_id, source_column, label, value_type, ordinal, is_rde) "
                + "VALUES (@fieldId, @sourceColumn, @label, @valueType, @ordinal, 1);");
            insertField.Transaction = transaction;
            insertField.Parameters.AddWithValue("@fieldId", field.FieldId);
            insertField.Parameters.AddWithValue("@sourceColumn", field.SourceColumn);
            insertField.Parameters.AddWithValue("@label", field.Label);
            insertField.Parameters.AddWithValue("@valueType", field.ValueType);
            insertField.Parameters.AddWithValue("@ordinal", ordinal);
            await insertField.ExecuteNonQueryAsync(cancellationToken);
        }

        if (fields.Count == 0)
        {
            return;
        }

        var requiredColumns = GlProjectionSourceBuffer.RequiredColumns(spec);
        var pageSize = GlProjectionSourceBuffer.EntryPageSize(requiredColumns.Count);
        var schema = SqlServerProjectSchema.For(projectId);
        long cursor = 0;
        while (true)
        {
            var page = new List<(long EntryId, int SourceRowNumber, Dictionary<string, string> Values)>(pageSize);
            await using (var select = database.CreateCommand(
                             connection,
                             projectId,
                             "SELECT g.entry_id, s.source_row_number, s.row_json "
                             + "FROM {s}.target_gl_entry g "
                             + "JOIN {s}.staging_gl_raw_row s ON s.batch_id = g.batch_id "
                             + "AND s.row_number = g.source_row_number "
                             + "WHERE g.batch_id = @batchId AND g.entry_id > @cursor "
                             + "ORDER BY g.entry_id "
                             + SqlServerDialect.Instance.LimitClause("@pageSize") + ";"))
            {
                select.Transaction = transaction;
                select.Parameters.AddWithValue("@batchId", batchId);
                select.Parameters.AddWithValue("@cursor", cursor);
                select.Parameters.AddWithValue("@pageSize", pageSize);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var sourceValues = JsonSerializer.Deserialize<Dictionary<string, string>>(
                                           reader.GetString(2),
                                           JsonOptions)
                                       ?? [];
                    page.Add((
                        reader.GetInt64(0),
                        reader.GetInt32(1),
                        GlProjectionSourceBuffer.SelectRequiredValues(sourceValues, requiredColumns)));
                }
            }

            if (page.Count == 0)
            {
                return;
            }

            const int valueBatchSize = 2_000;
            var table = new DataTable();
            table.Columns.Add("entry_id", typeof(long));
            table.Columns.Add("field_id", typeof(string));
            table.Columns.Add("value_type", typeof(string));
            table.Columns.Add("text_value", typeof(string));
            table.Columns.Add("date_value", typeof(string));
            table.Columns.Add("amount_scaled", typeof(long));

            async Task FlushValuesAsync()
            {
                if (table.Rows.Count == 0)
                {
                    return;
                }

                using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
                {
                    DestinationTableName = $"[{schema}].[target_gl_rde_value]",
                    EnableStreaming = true,
                    BulkCopyTimeout = 0
                };
                foreach (DataColumn column in table.Columns)
                {
                    bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                }
                await bulk.WriteToServerAsync(table, cancellationToken);
                table.Clear();
            }

            foreach (var item in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var staging = new StagingRow(item.SourceRowNumber, item.Values);
                if (!GlRowProjector.TryProject(
                        staging,
                        spec,
                        moneyScale,
                        dateOptions,
                        out var projected,
                        out var error,
                        cancellationToken))
                {
                    throw new InvalidOperationException(
                        $"RDE second-pass projection 與已驗證的第一遍不一致：{error?.Field}。");
                }

                foreach (var rde in projected!.RdeValues)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    table.Rows.Add(
                        item.EntryId,
                        rde.FieldId,
                        rde.ValueType,
                        (object?)rde.TextValue ?? DBNull.Value,
                        (object?)rde.DateValue ?? DBNull.Value,
                        (object?)rde.AmountScaled ?? DBNull.Value);
                    if (table.Rows.Count >= valueBatchSize)
                    {
                        await FlushValuesAsync();
                    }
                }
            }
            await FlushValuesAsync();

            cursor = page[^1].EntryId;
        }
    }
}
