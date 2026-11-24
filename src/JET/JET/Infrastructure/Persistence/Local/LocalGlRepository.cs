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
/// 確認 GL 配對時，把匯入暫存表 <c>pbc_gl</c> 的每一列轉成 <c>gl</c> 的分錄，並把金額、日期等值正規化；以下稱投影。
/// 一列一列讀暫存表的 row_json，用 C# 的 decimal 解析金額再放大成整數，交給 <see cref="IBulkRowWriter"/> 寫入。
/// 任何一列失敗，整批 rollback。
/// 診斷日誌（開發用）：只執行一次的清除與查詢經 <see cref="DiagnosticDb"/> 記錄，交易的開始與結束也記一筆；
/// 逐列的 INSERT 不一筆一筆記，否則會把日誌的緩衝區塞滿，改在投影結束後記一筆 projection.milestone。
/// </summary>
public sealed class LocalGlRepository(ILocalProjectDatabase database, ILogger<LocalGlRepository>? logger = null)
    : IGlRepository
{
    private const int ProgressRowInterval = 20_000;

    private readonly string _provider = database.Dialect.ProviderName;

    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    /// <summary>批量寫入 gl 時的欄位順序，交給 <see cref="IBulkRowWriter"/> 的值照這個順序排；entry_id 由資料庫自動編號。</summary>
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

        await using var connection = await database.OpenGlProjectionConnectionAsync(projectId, cancellationToken);
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

        // gl 的索引先刪掉，逐列寫完再一次建回，比每寫一列就更新每個索引快。
        var glIndexes = await database.DeferIndexesAsync(connection, transaction, JetSchemaCatalog.GlTableName, cancellationToken);

        // 重新投影會改寫 gl，已有的規則結果跟著失效，在同一個交易裡清除；投影失敗 rollback 時，這次清除也一起回復。
        await ResultReset.ClearWithinAsync(
            connection,
            transaction,
            cancellationToken,
            AuditMutation.GlProjection);

        // 一個批次有多個來源時，錯誤訊息要寫出是哪個檔案的第幾列；只有一個來源時不加檔名。
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
        // 讀不成日期的值與年份不在 1900 到 2100 年之間的日期不擋確認配對，逐列記下來，提交後給畫面的日期提醒。
        var dateNotices = new GlDateNoticeCollector();
        // 讀不成日期的總帳日期另外照無法轉換的寫法記一份：整批沒有一列納入 GL 時，用它說明是哪些值讀不成日期。
        var unrecognizedGlDates = new ProjectionErrorCollector();
        var insertedCount = 0;
        // 只指定一側的代碼時，人工/自動清單代碼有沒有出現在來源裡，逐列記錄，提交後給不擋的提醒。
        var manualAutoCodes = new ManualAutoListedCodeAudit(spec);
        // 審計員勾選的欄位把空白補成同一來源內上一列的值；暫存表不動，只改交給投影器的那一列。
        var fillDown = GlStagingFillDown.Create(spec);
        // 累計存入筆數與合計核對要用的數字：每讀一列暫存表，來源列數加一；每成功寫入一筆，累加借方與貸方總額。
        long pbcRowCount = 0;
        long targetDebit = 0;
        long targetCredit = 0;
        long glRowCount = 0;
        long excludedByPeriodCount = 0;
        long excludedByPostingStatusCount = 0;
        long glDebit = 0;
        long glCredit = 0;
        // AppendAsync 回來時已經把那一次的 values 用完（IBulkRowWriter 的使用規則），所以整個迴圈重用同一個陣列，
        // 不必為每一列配置一個新的 object[]；2,000 萬列的 GL 就少配置 2,000 萬個用完即丟的陣列。
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

                // 交給 GlRowProjector 的列號是來源檔裡的列號，錯誤訊息才會指到審計員在檔案裡看到的那一列。
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
                foreach (var issue in projected!.DateIssues)
                {
                    var sourceLabel = sourceLabels?.GetValueOrDefault(sourceNo);
                    dateNotices.Observe(issue, sourceRowNumber, sourceLabel);
                    if (issue.Kind == GlDateIssueKind.Unrecognized && issue.Field == GlMappingKeys.GlDate)
                    {
                        unrecognizedGlDates.Observe(new RowProjectionError(
                            sourceRowNumber, issue.SourceColumn, issue.RawValue, ProjectionErrorReasons.DateInvalid, sourceLabel));
                    }
                }

                // gl 的 source_row_number 存的是暫存表的 row_number，也就是批次內的排序鍵，INF 抽樣以它為準；
                // 批次只有一個來源時它等於來源列號。
                // 值照 TargetColumns 的順序放；null 由寫入器轉成 NULL；is_manual 存 1、0 或 NULL。
                targetValues[1] = rowNumber;
                targetValues[2] = projected.VoucherNumber;
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
                    postingStatusMatcher,
                    projected.GlDateUnrecognized);

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

        // CompleteAsync 之後，同一個交易裡接下來的命令才讀得到剛寫入的 gl 列。
        // 下面補 line_item、寫控制總數與檢查整欄空白都排在這之後，讀得到這些列。
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
            // 沒有一列納入 GL，而且有總帳日期讀不成日期：多半是選錯日期欄或日期寫法不合，列出那些值與列號。
            if (unrecognizedGlDates.TotalErrorCount > 0)
            {
                return unrecognizedGlDates.FailedResult();
            }

            throw new JetActionException(
                JetErrorCodes.EmptyGl,
                GlProjectionGuard.EmptyGlMessage);
        }

        // 每一列都投影成功、GL 也不是空的，但借方與貸方總額都是 0，通常是金額欄配到傳票總額或空白欄。
        // 這樣的 GL 不能拿來做完整性測試與後面的規則，所以整批 rollback 並回明確的錯誤。
        // 判斷條件與訊息都定義在 Domain 的 GlProjectionGuard。
        if (GlProjectionGuard.IsDegenerateAmountPopulation(
                glRowCount,
                glDebit,
                glCredit))
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            throw new JetActionException(JetErrorCodes.GlAmountsAllZero, GlProjectionGuard.DegenerateAmountMessage);
        }

        // 逐列寫入與上面的檢查都通過以後，才建回 gl 的索引，之後補序號與檢查空白欄都在有索引的 gl 上執行。
        // 上面擋下或中途取消時交易回復，刪掉的索引也跟著回復。
        await glIndexes.RebuildAsync(cancellationToken);

        // 沒有配對 lineItem 時，投影完再替每張傳票的分錄依序編號。這個編號只用來顯示，不參與任何規則計算，也不當成任何鍵。
        // 編號要用視窗函式 ROW_NUMBER，一列一列處理的投影做不到，所以在這裡用一句集合式 SQL 在同一個交易裡補上。
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

        // 把存入筆數與合計核對要用的數字寫進 gl_control_total，和投影在同一個交易裡、commit 之前。
        // 投影失敗的情形在上面已經 rollback，不會走到這裡。
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

        // 檢查有沒有必填的文字欄整欄都是空白；有的話可能是配錯欄，例如配到來源裡標頭重複、內容空白的那一欄。
        // 這只是提醒，不擋下確認配對。檢查在同一個交易裡讀 gl。
        // 一筆都沒寫入時不檢查：沒有資料可以判斷，免得把「沒有資料」報成每一欄都配錯。
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
            DateNotices = dateNotices.Notices(),
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
