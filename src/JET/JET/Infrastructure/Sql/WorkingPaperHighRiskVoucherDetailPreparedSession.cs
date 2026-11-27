using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 匯出工作底稿 step4-1 時讀取資料的流程。建立時開一個交易，先把命中傳票與每筆分錄命中的情境寫進兩張暫存表，
/// 再用一個查詢依序讀出全部分錄，結果只能往前讀一次；結束時清掉暫存表並結束交易。
/// 這個流程不分資料庫種類：資料庫實作只提供連線、SQL 方言與資料表名稱的前綴。
/// 所有值都用參數綁定，暫存表的名稱是程式裡的常數。
/// </summary>
internal sealed class WorkingPaperHighRiskVoucherDetailPreparedSession : IWorkingPaperHighRiskVoucherDetailPreparedSession
{
    private const string LocalVoucherTable = "jet_high_risk_voucher_detail_hit_voucher";
    private const string LocalTagTable = "jet_high_risk_voucher_detail_row_tag";
    private const int CleanupCommandTimeoutSeconds = 5;
    private static readonly TimeSpan CleanupDeadline = TimeSpan.FromSeconds(10);

    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;
    private readonly ISqlDialect _dialect;
    private readonly GlPopulationContext _context;
    private readonly HighRiskSummaryScenarioSqlScope _scenarioScope;
    private readonly HighRiskSummaryScenarioSqlScope _hitVoucherScenarioScope;
    private readonly LegacyFieldKind _lineItemKind;
    private readonly string _schemaPrefix;
    private readonly string _voucherTable;
    private readonly string _tagTable;
    private int _readStarted;
    private int _readCompleted;
    private int _disposed;

    private WorkingPaperHighRiskVoucherDetailPreparedSession(
        DbConnection connection,
        DbTransaction transaction,
        ISqlDialect dialect,
        GlPopulationContext context,
        HighRiskSummaryScenarioSqlScope scenarioScope,
        HighRiskSummaryScenarioSqlScope hitVoucherScenarioScope,
        LegacyFieldKind lineItemKind,
        string schemaPrefix,
        WorkingPaperHighRiskVoucherDetailPreparedSessionMetrics metrics)
    {
        _connection = connection;
        _transaction = transaction;
        _dialect = dialect;
        _context = context;
        _scenarioScope = scenarioScope;
        _hitVoucherScenarioScope = hitVoucherScenarioScope;
        _lineItemKind = lineItemKind;
        _schemaPrefix = schemaPrefix;
        _voucherTable = LocalVoucherTable;
        _tagTable = LocalTagTable;
        Metrics = metrics;
    }

    public WorkingPaperHighRiskVoucherDetailPreparedSessionMetrics Metrics { get; }

    internal static async Task<IWorkingPaperHighRiskVoucherDetailPreparedSession> CreateAsync(
        DbConnection connection,
        IProviderSqlDialect dialect,
        string schemaPrefix,
        GlPopulationContext context,
        IReadOnlyList<int> scenarioPositions,
        LegacyFieldKind lineItemKind,
        bool dedicatedConnection,
        CancellationToken cancellationToken,
        IReadOnlyList<int>? hitVoucherScenarioPositions = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        WorkingPaperHighRiskVoucherDetailPreparedSessionMetrics? metrics = null;
        DbTransaction? transaction = null;
        try
        {
            ArgumentNullException.ThrowIfNull(dialect);
            ArgumentNullException.ThrowIfNull(scenarioPositions);
            if (lineItemKind is not (
                    LegacyFieldKind.Text
                    or LegacyFieldKind.Number
                    or LegacyFieldKind.Date
                    or LegacyFieldKind.Time))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lineItemKind),
                    lineItemKind,
                    "Unsupported step4-1 line item field kind.");
            }

            var selectedPositions = scenarioPositions.ToHashSet();
            var hitVoucherPositions = hitVoucherScenarioPositions ?? [];
            if (hitVoucherPositions.Any(position => !selectedPositions.Contains(position)))
            {
                throw new ArgumentException(
                    "Hit-voucher tag positions must be a subset of selected scenario positions.",
                    nameof(hitVoucherScenarioPositions));
            }

            var scenarioScope = HighRiskSummaryScenarioSqlScope.Create(scenarioPositions);
            var hitVoucherScenarioScope =
                HighRiskSummaryScenarioSqlScope.CreateHitVoucher(hitVoucherPositions);

            metrics = new WorkingPaperHighRiskVoucherDetailPreparedSessionMetrics(dialect.ProviderName)
            {
                DedicatedConnection = dedicatedConnection,
                ConsistencyMode = "transaction-snapshot"
            };
            metrics.Connections++;
            await connection.OpenAsync(cancellationToken);
            transaction = await connection.BeginTransactionAsync(cancellationToken);
            metrics.Transactions++;
            metrics.TransactionIsolationLevel = transaction.IsolationLevel.ToString();

            var session = new WorkingPaperHighRiskVoucherDetailPreparedSession(
                connection,
                transaction,
                dialect,
                context,
                scenarioScope,
                hitVoucherScenarioScope,
                lineItemKind,
                schemaPrefix,
                metrics);
            await session.InitializeTemporaryTablesAsync(cancellationToken);
            await session.MaterializeHitVouchersAsync(cancellationToken);
            await session.MaterializeRowTagsAsync(cancellationToken);
            return session;
        }
        catch
        {
            if (transaction is not null)
            {
                if (await TryRollbackAsync(transaction))
                {
                    if (metrics is not null)
                    {
                        metrics.TransactionRolledBack = true;
                    }
                }
                if (await TryDisposeTransactionAsync(transaction))
                {
                    if (metrics is not null)
                    {
                        metrics.TransactionDisposed = true;
                    }
                }
            }
            if (await TryDisposeConnectionAsync(connection))
            {
                if (metrics is not null)
                {
                    metrics.ConnectionDisposed = true;
                }
            }
            if (metrics is not null)
            {
                metrics.Disposed =
                    metrics.TransactionDisposed && metrics.ConnectionDisposed;
                metrics.TemporaryObjectsCleared =
                    metrics.DedicatedConnection && metrics.ConnectionDisposed;
            }
            throw;
        }
    }

    public async IAsyncEnumerable<WorkingPaperHighRiskVoucherDetailSourceRow> ReadRowsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _readStarted, 1) != 0)
        {
            throw new InvalidOperationException(
                "WorkingPaper step4-1 prepared rows 只能 forward-read 一次。");
        }

        await using var command = CreateCommand();
        GlPopulationScopeSql.Plan(_dialect, _context, "g").BindParametersTo(command);
        command.CommandText = OrderedRowsSql();
        command.CommandTimeout = 0;
        Metrics.OrderedReaderCommands++;
        RecordCommand("orderedRows", command);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var voucherNumber = reader.IsDBNull(0) ? null : reader.GetString(0);
            var lineItem = reader.IsDBNull(1) ? null : reader.GetString(1);
            var glDate = reader.IsDBNull(2) ? null : reader.GetString(2);
            var approvalDate = reader.IsDBNull(3) ? null : reader.GetString(3);
            var createdBy = reader.IsDBNull(4) ? null : reader.GetString(4);
            var approvedBy = reader.IsDBNull(5) ? null : reader.GetString(5);
            var accountNumber = reader.IsDBNull(6) ? null : reader.GetString(6);
            var accountName = reader.IsDBNull(7) ? null : reader.GetString(7);
            var amountScaled = reader.GetInt64(8);
            var description = reader.IsDBNull(9) ? null : reader.GetString(9);
            var sourceModule = reader.IsDBNull(10) ? null : reader.GetString(10);
            bool? isManual = reader.IsDBNull(11)
                ? null
                : Convert.ToInt64(
                    reader.GetValue(11),
                    CultureInfo.InvariantCulture) != 0;
            var rawJson = reader.IsDBNull(12) ? null : reader.GetString(12);
            var numericSortKey = reader.IsDBNull(13) ? null : reader.GetString(13);
            var entryId = reader.GetInt64(14);
            var tagMask = reader.IsDBNull(15)
                ? 0L
                : Convert.ToInt64(reader.GetValue(15), CultureInfo.InvariantCulture);
            var postingStatus = reader.IsDBNull(16) ? null : reader.GetString(16);

            if (rawJson is null)
            {
                throw MissingWorkingPaperRawSource();
            }

            if (_lineItemKind == LegacyFieldKind.Number
                && lineItem is not null
                && numericSortKey is null)
            {
                throw MissingWorkingPaperNumericLineItemSortKey();
            }

            var matchedPositions = ScenarioTagMask.DecodePositions(tagMask);
            Metrics.RowsRead = checked(Metrics.RowsRead + 1);
            yield return new WorkingPaperHighRiskVoucherDetailSourceRow(
                entryId,
                voucherNumber,
                lineItem,
                glDate,
                approvalDate,
                createdBy,
                approvedBy,
                accountNumber,
                accountName,
                amountScaled,
                description,
                sourceModule,
                isManual,
                rawJson,
                matchedPositions,
                postingStatus);
        }
        Interlocked.Exchange(ref _readCompleted, 1);
        Metrics.ReaderCompleted = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var readerCompleted = Volatile.Read(ref _readCompleted) != 0;
        var effectiveCleanup = false;
        Exception? completionFailure = null;
        try
        {
            if (readerCompleted)
            {
                var cleanupInTransaction = await TryCleanupAsync(
                    enlistInTransaction: true);
                if (cleanupInTransaction && await TryCommitAsync(_transaction))
                {
                    Metrics.TransactionCommitted = true;
                    effectiveCleanup = true;
                }
                else
                {
                    if (await TryRollbackAsync(_transaction))
                    {
                        Metrics.TransactionRolledBack = true;
                    }
                    effectiveCleanup = await TryCleanupAsync(
                        enlistInTransaction: false);
                }
            }
            else
            {
                if (await TryRollbackAsync(_transaction))
                {
                    Metrics.TransactionRolledBack = true;
                }
                effectiveCleanup = await TryCleanupAsync(
                    enlistInTransaction: false);
            }
        }
        finally
        {
            if (await TryDisposeTransactionAsync(_transaction))
            {
                Metrics.TransactionDisposed = true;
            }
            else
            {
                completionFailure = new InvalidOperationException(
                    "WorkingPaper step4-1 transaction 無法釋放。");
            }

            if (await TryDisposeConnectionAsync(_connection))
            {
                Metrics.ConnectionDisposed = true;
            }
            else
            {
                completionFailure ??= new InvalidOperationException(
                    "WorkingPaper step4-1 connection 無法釋放。");
            }

            Metrics.CleanupCommandSucceeded = effectiveCleanup;
            Metrics.Disposed =
                Metrics.TransactionDisposed && Metrics.ConnectionDisposed;
            Metrics.TemporaryObjectsCleared =
                effectiveCleanup
                || (Metrics.DedicatedConnection && Metrics.ConnectionDisposed);
        }

        if (readerCompleted
            && (!Metrics.TransactionCommitted
                || !Metrics.TemporaryObjectsCleared
                || completionFailure is not null))
        {
            throw new InvalidOperationException(
                "WorkingPaper step4-1 prepared session 無法完成有界清理。",
                completionFailure);
        }
    }

    private async Task MaterializeHitVouchersAsync(CancellationToken cancellationToken)
    {
        await using var command = CreateCommand();
        GlPopulationScopeSql.Plan(_dialect, _context, "g").BindParametersTo(command);
        _scenarioScope.AddParameters(command);
        command.CommandText = CreateVoucherSql();
        command.CommandTimeout = 0;
        Metrics.HitVoucherMaterializationCommands++;
        RecordCommand("materializeHitVouchers", command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InitializeTemporaryTablesAsync(
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand();
        command.CommandText = InitializeTemporaryTablesSql();
        command.CommandTimeout = 0;
        Metrics.TemporaryTableInitializationCommands++;
        RecordCommand("initializeTemporaryTables", command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task MaterializeRowTagsAsync(CancellationToken cancellationToken)
    {
        await using var command = CreateCommand();
        GlPopulationScopeSql.Plan(_dialect, _context, "g").BindParametersTo(command);
        _scenarioScope.AddParameters(command);
        _hitVoucherScenarioScope.AddParameters(command);
        command.CommandText = CreateTagSql();
        command.CommandTimeout = 0;
        Metrics.RowTagMaterializationCommands++;
        RecordCommand("materializeRowTags", command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateCommand(bool enlistInTransaction = true)
    {
        var command = _connection.CreateCommand();
        if (enlistInTransaction)
        {
            command.Transaction = _transaction;
        }
        return command;
    }

    private async Task<bool> TryCleanupAsync(bool enlistInTransaction)
    {
        DbCommand? cleanup = null;
        try
        {
            cleanup = CreateCommand(enlistInTransaction);
            cleanup.CommandText = CleanupSql();
            cleanup.CommandTimeout = CleanupCommandTimeoutSeconds;
            Metrics.CleanupCommands++;
            RecordCommand("cleanup", cleanup);
            using var deadline = new CancellationTokenSource(CleanupDeadline);
            await cleanup.ExecuteNonQueryAsync(deadline.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (cleanup is not null)
            {
                try
                {
                    await cleanup.DisposeAsync();
                }
                catch (Exception)
                {
                    // 命令釋放失敗時不往外丟；之後交易結束與連線關閉仍會把資源收掉。
                }
            }
        }
    }

    private static async Task<bool> TryCommitAsync(DbTransaction transaction)
    {
        try
        {
            using var deadline = new CancellationTokenSource(CleanupDeadline);
            await transaction.CommitAsync(deadline.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<bool> TryRollbackAsync(DbTransaction transaction)
    {
        try
        {
            using var deadline = new CancellationTokenSource(CleanupDeadline);
            await transaction.RollbackAsync(deadline.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<bool> TryDisposeTransactionAsync(DbTransaction transaction)
    {
        try
        {
            await transaction.DisposeAsync();
            return true;
        }
        catch (Exception)
        {
            try
            {
                transaction.Dispose();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private static async Task<bool> TryDisposeConnectionAsync(DbConnection connection)
    {
        try
        {
            await connection.DisposeAsync();
            return true;
        }
        catch (Exception)
        {
            try
            {
                connection.Close();
                connection.Dispose();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private void RecordCommand(string operation, DbCommand command)
    {
        Metrics.RecordCommand(
            operation,
            command.CommandText,
            command.Parameters
                .Cast<DbParameter>()
                .Select(parameter => parameter.ParameterName)
                .ToArray());
    }

    private string InitializeTemporaryTablesSql()
    {
        var voucher = $"""
            DROP TABLE IF EXISTS {_voucherTable};
            CREATE TEMP TABLE {_voucherTable}
                (voucher_number TEXT COLLATE BINARY NOT NULL PRIMARY KEY);
            """;
        var tag = $"""
            DROP TABLE IF EXISTS {_tagTable};
            CREATE TEMP TABLE {_tagTable}
                (entry_id BIGINT NOT NULL PRIMARY KEY, tag_mask BIGINT NOT NULL);
            """;
        return voucher + Environment.NewLine + tag;
    }

    private string CreateVoucherSql() =>
        $"""
         INSERT INTO {_voucherTable} (voucher_number)
         SELECT DISTINCT g.voucher_number
         FROM {_schemaPrefix}filter_hit r
         JOIN {_schemaPrefix}filter_scenario fs ON fs.scenario_id = r.scenario_id
         JOIN {_schemaPrefix}gl g ON g.entry_id = r.entry_id
         WHERE {HitVoucherRows.HasVoucherNumber("g")}
           AND {GlPopulationScopeSql.Predicate(_context, "g")}
           {ScenarioPredicate("fs.position")};
         """;

    /// <summary>
    /// 每筆分錄命中的情境位置，加總成一個標記值。整張傳票的位置先取出不重複的命中傳票與位置，
    /// 再接回那張傳票的全部分錄；如果每一筆命中分錄各自展開整張傳票，一張傳票有 k 筆命中、m 筆分錄時
    /// 會產生 k×m 列。
    /// </summary>
    private string CreateTagSql()
    {
        var expandedRows = _hitVoucherScenarioScope.IsEmpty
            ? string.Empty
            : $"""
               UNION
               SELECT expanded.entry_id, hit_voucher.scenario_position
               FROM (
                   SELECT DISTINCT hit.voucher_number, fs.position AS scenario_position
                   FROM {_schemaPrefix}filter_hit r
                   JOIN {_schemaPrefix}filter_scenario fs ON fs.scenario_id = r.scenario_id
                   JOIN {_schemaPrefix}gl hit ON hit.entry_id = r.entry_id
                   WHERE {HitVoucherRows.HasVoucherNumber("hit")}
                     AND {GlPopulationScopeSql.Predicate(_context, "hit")}
                     {_hitVoucherScenarioScope.Predicate("fs.position")}
               ) hit_voucher
               JOIN {_schemaPrefix}gl expanded
                 ON expanded.voucher_number = hit_voucher.voucher_number
               WHERE {GlPopulationScopeSql.Predicate(_context, "expanded")}
               """;

        return $"""
               INSERT INTO {_tagTable} (entry_id, tag_mask)
               SELECT tag_source.entry_id,
                      {ScenarioTagMask.SumSql("tag_source.scenario_position")}
               FROM (
                   SELECT r.entry_id, fs.position AS scenario_position
                   FROM {_schemaPrefix}filter_hit r
                   JOIN {_schemaPrefix}filter_scenario fs ON fs.scenario_id = r.scenario_id
                   JOIN {_schemaPrefix}gl g ON g.entry_id = r.entry_id
                   WHERE {GlPopulationScopeSql.Predicate(_context, "g")}
                     {ScenarioPredicate("fs.position")}
                   {expandedRows}
               ) tag_source
               GROUP BY tag_source.entry_id;
               """;
    }

    private string OrderedRowsSql()
    {
        // SQLite 的文字比較依字元碼（BINARY 定序），排序不受語言設定影響。
        const string documentOrder = "COALESCE(g.voucher_number, '')";
        var lineOrder = _lineItemKind switch
        {
            LegacyFieldKind.Number =>
                "CASE WHEN g.line_item IS NULL THEN '' "
                + "ELSE COALESCE(g.line_item_numeric_sort_key, '') END",
            LegacyFieldKind.Text or LegacyFieldKind.Date or LegacyFieldKind.Time =>
                "COALESCE(g.line_item, '')",
            _ => throw new ArgumentOutOfRangeException(
                nameof(_lineItemKind),
                _lineItemKind,
                "Unsupported step4-1 line item field kind.")
        };
        const string lineNullRank = "CASE WHEN g.line_item IS NULL THEN 0 ELSE 1 END";
        return
            "SELECT g.voucher_number, g.line_item, g.gl_date, g.approval_date, "
            + "       g.created_by, g.approved_by, g.account_number, g.account_name, "
            + "       g.amount_scaled, g.description, g.source_module, g.is_manual, "
            + "       raw.row_json, g.line_item_numeric_sort_key, g.entry_id, "
            + "       COALESCE(t.tag_mask, 0), g.posting_status "
            + $"FROM {_schemaPrefix}gl g "
            + $"LEFT JOIN {_voucherTable} v ON v.voucher_number = g.voucher_number "
            + $"LEFT JOIN {_schemaPrefix}pbc_gl raw "
            + "  ON raw.batch_id = g.batch_id AND raw.row_number = g.source_row_number "
            + $"LEFT JOIN {_tagTable} t ON t.entry_id = g.entry_id "
            + $"WHERE {GlPopulationScopeSql.Predicate(_context, "g")} "
            + $"AND {HitVoucherRows.ListedInWorkingPaper("g", "v", "t")} "
            + $"ORDER BY {documentOrder}, {lineNullRank}, {lineOrder}, g.entry_id;";
    }

    private string CleanupSql() => $"""
        DROP TABLE IF EXISTS {_tagTable};
        DROP TABLE IF EXISTS {_voucherTable};
        """;

    private string ScenarioPredicate(string columnExpression) =>
        _scenarioScope.IsEmpty
            ? "AND 1 = 0"
            : _scenarioScope.Predicate(columnExpression);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(WorkingPaperHighRiskVoucherDetailPreparedSession));
        }
    }

    private static JetActionException MissingWorkingPaperRawSource() => new(
        JetErrorCodes.StaleResult,
        "底稿引用的原始 GL 列已不完整，請重新匯入、配對並執行對應步驟後再產出底稿。");

    private static JetActionException MissingWorkingPaperNumericLineItemSortKey() => new(
        JetErrorCodes.StaleResult,
        "底稿引用的 GL 傳票文件項次排序鍵已過期，請重新匯入、配對並執行對應步驟後再產出底稿。");
}
