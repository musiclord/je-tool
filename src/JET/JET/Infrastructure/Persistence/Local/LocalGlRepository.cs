using System.Diagnostics;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// GL staging → target 投影（import-stage normalization）：
/// streaming 讀 staging row_json，C# decimal 解析金額後轉 scaled integer，
/// prepared statement 批次插入。任一列失敗整批 rollback。
/// 診斷日誌（dev-only）：一次性 clear/select 走 <see cref="DiagnosticDb"/>、transaction 走 scope；
/// 逐列 INSERT 不逐筆記事件（會爆 ring buffer），改以投影結束後一筆 projection.milestone 收斂。
/// </summary>
public sealed class LocalGlRepository(ILocalProjectDatabase database, ILogger<LocalGlRepository>? logger = null)
    : IGlRepository
{
    private const int ProgressRowInterval = 20_000;

    private readonly string _provider = database.Dialect.ProviderName;

    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    /// <summary>GL target 批量寫入的欄位順序（<see cref="IBulkRowWriter"/> 依此對位；entry_id 由引擎補齊）。</summary>
    private static readonly string[] TargetColumns =
    [
        "batch_id", "source_row_number",
        "voucher_number", "line_item", "gl_date", "approval_date",
        "account_number", "account_name", "description",
        "source_module", "created_by", "approved_by", "is_manual",
        "amount_scaled", "debit_amount_scaled", "credit_amount_scaled", "dr_cr",
        "line_item_numeric_sort_key", "posting_status", "is_excluded", "exclusion_reason"
    ];

    private static readonly string[] RdeValueColumns =
    [
        "entry_id", "field_id", "value_type", "text_value", "date_value", "amount_scaled"
    ];

    private readonly ILogger _log = logger ?? NullLogger<LocalGlRepository>.Instance;

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
        var canonicalPostingStatusPolicy = GlExcludedPopulation.NormalizePolicy(
            postingStatusMapped,
            postingStatusPolicy);
        var projectionOptions = spec.Options with
        {
            PostingStatusPolicy = canonicalPostingStatusPolicy
        };
        var postingStatusMatcher = canonicalPostingStatusPolicy is null
            ? null
            : new GlExcludedPopulationMatcher(canonicalPostingStatusPolicy);

        await database.EnsureReadyAsync(projectId, cancellationToken);
        var stopwatch = Stopwatch.StartNew();

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        using var txLog = DiagnosticDb.BeginTransaction(_log, _provider);

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText =
                "DELETE FROM gl_rde_value; "
                + "DELETE FROM gl_rde_field; "
                + "DELETE FROM gl;";
            await clear.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
        }

        // 重投影改寫 target,既有規則結果失效(投影失敗 rollback 時清除一併回退)。
        await ResultReset.ClearWithinAsync(
            connection,
            transaction,
            cancellationToken,
            AuditMutation.GlProjection);

        // 多來源批次的錯誤訊息需要「哪個檔案的第幾列」；單來源維持無前綴（與單檔時代訊息一致）
        var sourceLabels = await ProjectionSourceLabels.LoadAsync(connection, transaction, batchId, cancellationToken);

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            """
            SELECT row_number, source_no, source_row_number, row_json
            FROM pbc_gl
            WHERE batch_id = @batchId
            ORDER BY row_number;
            """;
        select.AddWithValue("@batchId", batchId);

        await using var insert = database.CreateBulkRowWriter(connection, transaction, JetSchemaCatalog.GlTableName, TargetColumns);

        var errors = new ProjectionErrorCollector();
        var insertedCount = 0;
        // 只列一側的人工/自動清單代碼有沒有出現在來源裡，逐列記錄，提交後給不擋的提醒。
        var manualAutoCodes = new ManualAutoListedCodeAudit(spec);
        // 審計員勾選的欄位把空白補成同一來源內上一列的值；暫存表不動，只改交給投影器的那一列。
        var fillDown = GlStagingFillDown.Create(spec);
        // 存入筆數與合計核對要用的數字累計:來源列數（每讀一列 staging）、母體借/貸總額（成功插入後）。
        long pbcRowCount = 0;
        long targetDebit = 0;
        long targetCredit = 0;
        long glRowCount = 0;
        long excludedByPeriodCount = 0;
        long excludedByPostingStatusCount = 0;
        long glDebit = 0;
        long glCredit = 0;
        // AppendAsync 完成時 writer 已消費該次 values（IBulkRowWriter 契約）；重用單一 buffer，
        // 避免大型 GL 為每列配置一個 19 欄 object[]（20M 列即 20M 個短命陣列）。
        var targetValues = new object?[TargetColumns.Length];
        targetValues[0] = batchId;

        await using (var reader = await select.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                pbcRowCount++;
                if (pbcRowCount % ProgressRowInterval == 0)
                {
                    progress?.Invoke(new ProjectionProgress(pbcRowCount));
                }

                var rowNumber = reader.GetInt64(0);
                var sourceNo = reader.GetInt32(1);
                var sourceRowNumber = reader.GetInt32(2);
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(3), JsonOptions)
                    ?? [];

                // 投影純函式只看來源列號（錯誤指回使用者所見的檔內列）
                var stagingRow = new StagingRow(sourceRowNumber, fillDown is null ? values : fillDown.Apply(sourceNo, values));

                if (!GlRowProjector.TryProject(
                        stagingRow,
                        spec,
                        moneyScale,
                        dateOptions,
                        out var projected,
                        out var error,
                        cancellationToken,
                        collectRdeValues: false))
                {
                    errors.Observe(error! with { SourceLabel = sourceLabels?.GetValueOrDefault(sourceNo) });

                    continue; // 續掃描以回報多筆錯誤，最終整批 rollback
                }

                if (errors.TotalErrorCount > 0)
                {
                    continue; // 已確定失敗，不再插入
                }

                manualAutoCodes.Observe(stagingRow);

                // target 的 source_row_number 存批次排序鍵（V3 抽樣基礎；單來源批次時 == 來源列號）。
                // 值依 TargetColumns 順序對位；null 由寫入器轉 NULL；is_manual 存 1/0/NULL。
                targetValues[1] = rowNumber;
                targetValues[2] = projected!.VoucherNumber;
                targetValues[3] = projected.LineItem;
                targetValues[4] = projected.GlDate;
                targetValues[5] = projected.ApprovalDate;
                targetValues[6] = projected.AccountNumber;
                targetValues[7] = projected.AccountName;
                targetValues[8] = projected.Description;
                targetValues[9] = projected.SourceModule;
                targetValues[10] = projected.CreatedBy;
                targetValues[11] = projected.ApprovedBy;
                targetValues[12] = projected.IsManual is null ? null : projected.IsManual.Value ? 1 : 0;
                targetValues[13] = projected.AmountScaled;
                targetValues[14] = projected.DebitAmountScaled;
                targetValues[15] = projected.CreditAmountScaled;
                targetValues[16] = projected.DrCr;
                targetValues[17] = LineItemNumericSortKey.CreateOrNull(projected.LineItem);
                targetValues[18] = projected.PostingStatus;
                var glDate = projected.GlDate is null
                    ? (DateOnly?)null
                    : DateOnly.ParseExact(
                        projected.GlDate,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture);
                var classification = GlExcludedPopulation.ClassifyWithMatcher(
                    glDate,
                    projected.PostingStatus,
                    periodStart,
                    periodEnd,
                    postingStatusMapped,
                    postingStatusMatcher);

                long nextTargetDebit;
                long nextTargetCredit;
                var nextGlDebit = glDebit;
                var nextGlCredit = glCredit;
                try
                {
                    nextTargetDebit = checked(targetDebit + projected.DebitAmountScaled);
                    nextTargetCredit = checked(targetCredit + projected.CreditAmountScaled);
                    if (!classification.IsExcluded)
                    {
                        nextGlDebit = checked(glDebit + projected.DebitAmountScaled);
                        nextGlCredit = checked(glCredit + projected.CreditAmountScaled);
                    }
                }
                catch (OverflowException)
                {
                    errors.Observe(GlRowProjector.CreateControlTotalOverflowError(
                        stagingRow,
                        spec,
                        projected.AmountScaled) with
                    {
                        SourceLabel = sourceLabels?.GetValueOrDefault(sourceNo)
                    });

                    continue;
                }

                targetValues[19] = classification.IsExcluded ? 1 : 0;
                targetValues[20] = classification.StorageReason;
                await insert.AppendAsync(targetValues, cancellationToken);
                insertedCount++;
                targetDebit = nextTargetDebit;
                targetCredit = nextTargetCredit;
                switch (classification.Disposition)
                {
                    case GlExcludedPopulationDisposition.Gl:
                        glRowCount++;
                        glDebit = nextGlDebit;
                        glCredit = nextGlCredit;
                        break;
                    case GlExcludedPopulationDisposition.ExcludedByPeriod:
                        excludedByPeriodCount++;
                        break;
                    case GlExcludedPopulationDisposition.ExcludedByPostingStatus:
                        excludedByPostingStatusCount++;
                        break;
                    default:
                        throw new InvalidOperationException("未知的 GL 分類：每一列只能列入 GL 或 GL(未納入測試)。");
                }
            }
        }

        // flush 之後同交易內可見——line_item 補值、控制總數、空欄稽核皆讀得到已寫入的 target 列。
        await insert.CompleteAsync(cancellationToken);

        if (pbcRowCount > 0 && pbcRowCount % ProgressRowInterval != 0)
        {
            progress?.Invoke(new ProjectionProgress(pbcRowCount));
        }

        if (errors.TotalErrorCount > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            return errors.FailedResult();
        }

        if (glRowCount == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            throw new JetActionException(
                JetErrorCodes.EmptyGl,
                GlProjectionGuard.EmptyGlMessage);
        }

        // 退化母體守門:投影無列級錯誤、母體非空,但借貸總額皆為 0(金額欄誤配到傳票總額或空欄)。
        // 此母體無法用於完整性與後續規則,整批 rollback 並回明確錯誤(Domain GlProjectionGuard 為單一事實)。
        if (GlProjectionGuard.IsDegenerateAmountPopulation(
                glRowCount,
                glDebit,
                glCredit))
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            throw new JetActionException(JetErrorCodes.GlAmountsAllZero, GlProjectionGuard.DegenerateAmountMessage);
        }

        // lineItem 未對應:投影後逐傳票自動編號（衍生顯示值；不參與任何規則計算、不作任何鍵）。
        // ROW_NUMBER 是視窗函式,逐列串流的投影做不到,故在此以 set-based SQL 於同一交易補值。
        if (!spec.HasLineItem)
        {
            await using var number = connection.CreateCommand();
            number.Transaction = transaction;
            number.CommandText =
                """
                UPDATE gl
                SET line_item = CAST(s.rn AS TEXT),
                    line_item_numeric_sort_key =
                        '2'
                        || printf('%010d', 2147483648 + length(CAST(s.rn AS TEXT)))
                        || replace(printf('%-29s', CAST(s.rn AS TEXT)), ' ', '0')
                FROM (
                    SELECT rowid AS rid,
                           ROW_NUMBER() OVER (PARTITION BY voucher_number ORDER BY source_row_number) AS rn
                    FROM gl
                ) AS s
                WHERE gl.rowid = s.rid;
                """;
            await number.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
        }

        // 存入筆數與合計核對要用的數字落地（同一交易、commit 之前;投影失敗已於上方 rollback 回退）。
        await using (var ct = connection.CreateCommand())
        {
            ct.Transaction = transaction;
            ct.CommandText =
                """
                INSERT INTO gl_control_total (
                    singleton, pbc_row_count, stored_row_count, stored_debit_scaled, stored_credit_scaled,
                    gl_row_count, excluded_by_period_count,
                    excluded_by_posting_status_count, gl_debit_scaled, gl_credit_scaled)
                VALUES (1, @pbc, @tgt, @debit, @credit, @glRowCount, @excludedPeriod,
                        @excludedPostingStatus, @glDebit, @glCredit)
                ON CONFLICT(singleton) DO UPDATE SET
                    pbc_row_count = excluded.pbc_row_count,
                    stored_row_count = excluded.stored_row_count,
                    stored_debit_scaled = excluded.stored_debit_scaled,
                    stored_credit_scaled = excluded.stored_credit_scaled,
                    gl_row_count = excluded.gl_row_count,
                    excluded_by_period_count = excluded.excluded_by_period_count,
                    excluded_by_posting_status_count = excluded.excluded_by_posting_status_count,
                    gl_debit_scaled = excluded.gl_debit_scaled,
                    gl_credit_scaled = excluded.gl_credit_scaled;
                """;
            ct.AddWithValue("@pbc", pbcRowCount);
            ct.AddWithValue("@tgt", insertedCount);
            ct.AddWithValue("@debit", targetDebit);
            ct.AddWithValue("@credit", targetCredit);
            ct.AddWithValue("@glRowCount", glRowCount);
            ct.AddWithValue("@excludedPeriod", excludedByPeriodCount);
            ct.AddWithValue("@excludedPostingStatus", excludedByPostingStatusCount);
            ct.AddWithValue("@glDebit", glDebit);
            ct.AddWithValue("@glCredit", glCredit);
            await ct.ExecuteNonQueryAsync(cancellationToken);
        }

        // 必填文字欄整欄空白偵測（非阻斷提醒；疑似配錯欄，如來源重複標頭中的空白欄）。同交易內查 target。
        // 空母體（insertedCount==0）不偵測：無資料可判，避免把「沒資料」誤報成每一欄都配錯。
        IReadOnlyList<string> warnings = [];
        if (insertedCount > 0)
        {
            var blankColumns = await GlMappedColumnAudit.ReadAsync(
                connection, transaction, spec, insertedCount, database.Dialect, cancellationToken);
            warnings = [.. blankColumns, .. manualAutoCodes.Warnings()];
        }

        await ReplaceRdeProjectionAsync(
            database,
            connection,
            transaction,
            batchId,
            spec,
            moneyScale,
            dateOptions,
            projectionOptions.RdeFields,
            cancellationToken);

        var sourceDefinitions = await LocalFieldInfoPersistence.ReadStatesAsync(
            connection,
            transaction,
            batchId,
            FieldInfoScope.Source,
            cancellationToken);
        await LocalFieldInfoPersistence.ReplaceScopeAsync(
            connection,
            transaction,
            batchId,
            FieldInfoScope.Target,
            FieldInfoProjector.ProjectGl(sourceDefinitions, spec, moneyScale),
            cancellationToken);

        var committedMapping = new CommittedMapping(
            DatasetKind.Gl,
            spec.Mapping,
            GlAmountModeNames.ToWireName(spec.AmountMode),
            batchId,
            committedUtc,
            MappingMetadataFormat.CurrentVersion,
            // 填了幾格跟著配對保存，重開案件與報告的配對資訊讀得到。
            projectionOptions with { FillDownCount = fillDown?.FilledCellCount ?? 0 });
        await LocalMappingStateStore.SaveWithinAsync(
            connection,
            transaction,
            committedMapping,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        txLog.Committed();
        DiagnosticDbLog.ProjectionMilestone(_log, "gl-projection", insertedCount, stopwatch.ElapsedMilliseconds,
            insertedCount * 1000.0 / Math.Max(1, stopwatch.ElapsedMilliseconds));
        return new ProjectionResult(insertedCount, [])
        {
            Warnings = warnings,
            GlTotals = new GlTotals(
                pbcRowCount,
                glRowCount,
                excludedByPeriodCount,
                excludedByPostingStatusCount,
                glDebit,
                glCredit),
            FillDownCellCount = fillDown?.FilledCellCount ?? 0
        };
    }

    private static async Task ReplaceRdeProjectionAsync(
        ILocalProjectDatabase database,
        DbConnection connection,
        DbTransaction transaction,
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
            await using var insertField = connection.CreateCommand();
            insertField.Transaction = transaction;
            insertField.CommandText =
                "INSERT INTO gl_rde_field "
                + "(field_id, source_column, label, value_type, ordinal, is_rde) "
                + "VALUES (@fieldId, @sourceColumn, @label, @valueType, @ordinal, 1);";
            insertField.AddWithValue("@fieldId", field.FieldId);
            insertField.AddWithValue("@sourceColumn", field.SourceColumn);
            insertField.AddWithValue("@label", field.Label);
            insertField.AddWithValue("@valueType", field.ValueType);
            insertField.AddWithValue("@ordinal", ordinal);
            await insertField.ExecuteNonQueryAsync(cancellationToken);
        }

        if (fields.Count == 0)
        {
            return;
        }

        var requiredColumns = GlProjectionSourceBuffer.RequiredColumns(spec);
        var pageSize = GlProjectionSourceBuffer.EntryPageSize(requiredColumns.Count);
        // 第二遍照第一遍的順序（entry_id 就是暫存列的順序）重新餵同一份往下填補，核心欄位才解析得出同一個值。
        var fillDown = GlStagingFillDown.Create(spec);
        long cursor = 0;
        while (true)
        {
            var page = new List<(long EntryId, int SourceNo, int SourceRowNumber, Dictionary<string, string> Values)>(pageSize);
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText =
                    "SELECT g.entry_id, s.source_no, s.source_row_number, s.row_json "
                    + "FROM gl g "
                    + "JOIN pbc_gl s ON s.batch_id = g.batch_id "
                    + "AND s.row_number = g.source_row_number "
                    + "WHERE g.batch_id = @batchId AND g.entry_id > @cursor "
                    + "ORDER BY g.entry_id "
                    + database.Dialect.LimitClause("@pageSize") + ";";
                select.AddWithValue("@batchId", batchId);
                select.AddWithValue("@cursor", cursor);
                select.AddWithValue("@pageSize", pageSize);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var sourceValues = JsonSerializer.Deserialize<Dictionary<string, string>>(
                                           reader.GetString(3),
                                           JsonOptions)
                                       ?? [];
                    page.Add((
                        reader.GetInt64(0),
                        reader.GetInt32(1),
                        reader.GetInt32(2),
                        GlProjectionSourceBuffer.SelectRequiredValues(sourceValues, requiredColumns)));
                }
            }

            if (page.Count == 0)
            {
                return;
            }

            await using var writer = database.CreateBulkRowWriter(
                connection,
                transaction,
                "gl_rde_value",
                RdeValueColumns);
            var values = new object?[RdeValueColumns.Length];
            foreach (var item in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var staging = new StagingRow(
                    item.SourceRowNumber,
                    fillDown is null ? item.Values : fillDown.Apply(item.SourceNo, item.Values));
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
                    values[0] = item.EntryId;
                    values[1] = rde.FieldId;
                    values[2] = rde.ValueType;
                    values[3] = rde.TextValue;
                    values[4] = rde.DateValue;
                    values[5] = rde.AmountScaled;
                    await writer.AppendAsync(values, cancellationToken);
                }
            }
            await writer.CompleteAsync(cancellationToken);
            cursor = page[^1].EntryId;
        }
    }
}
