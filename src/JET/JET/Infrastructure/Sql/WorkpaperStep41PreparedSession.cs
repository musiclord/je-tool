using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// Step4-1 prepared export 的 provider-neutral SQL lifecycle。Provider adapter 只提供
/// connection、dialect 與 schema qualifier；所有值仍以參數綁定，temp object 名為內部常數。
/// </summary>
internal sealed class WorkpaperStep41PreparedSession : IWorkpaperStep41PreparedSession
{
    private const string LocalVoucherTable = "jet_step41_hit_voucher";
    private const string LocalTagTable = "jet_step41_row_tag";
    private const string SqlServerVoucherTable = "#jet_step41_hit_voucher";
    private const string SqlServerTagTable = "#jet_step41_row_tag";
    private const int RequiredSchemaVersion = 6;
    private const int CleanupCommandTimeoutSeconds = 5;
    private static readonly TimeSpan CleanupDeadline = TimeSpan.FromSeconds(10);

    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;
    private readonly ISqlDialect _dialect;
    private readonly GlPopulationContext _context;
    private readonly TagMatrixScenarioSqlScope _scenarioScope;
    private readonly TagMatrixScenarioSqlScope _hitVoucherScenarioScope;
    private readonly LegacyFieldKind _lineItemKind;
    private readonly string _schemaPrefix;
    private readonly string _voucherTable;
    private readonly string _tagTable;
    private readonly bool _sqlServer;
    private int _readStarted;
    private int _readCompleted;
    private int _disposed;

    private WorkpaperStep41PreparedSession(
        DbConnection connection,
        DbTransaction transaction,
        ISqlDialect dialect,
        GlPopulationContext context,
        TagMatrixScenarioSqlScope scenarioScope,
        TagMatrixScenarioSqlScope hitVoucherScenarioScope,
        LegacyFieldKind lineItemKind,
        string schemaPrefix,
        bool sqlServer,
        WorkpaperStep41PreparedSessionMetrics metrics)
    {
        _connection = connection;
        _transaction = transaction;
        _dialect = dialect;
        _context = context;
        _scenarioScope = scenarioScope;
        _hitVoucherScenarioScope = hitVoucherScenarioScope;
        _lineItemKind = lineItemKind;
        _schemaPrefix = schemaPrefix;
        _sqlServer = sqlServer;
        _voucherTable = sqlServer ? SqlServerVoucherTable : LocalVoucherTable;
        _tagTable = sqlServer ? SqlServerTagTable : LocalTagTable;
        Metrics = metrics;
    }

    public WorkpaperStep41PreparedSessionMetrics Metrics { get; }

    internal static async Task<IWorkpaperStep41PreparedSession> CreateAsync(
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
        WorkpaperStep41PreparedSessionMetrics? metrics = null;
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

            var scenarioScope = TagMatrixScenarioSqlScope.Create(scenarioPositions);
            var hitVoucherScenarioScope =
                TagMatrixScenarioSqlScope.CreateHitVoucher(hitVoucherPositions);

            var sqlServer = dialect is SqlServerDialect;
            metrics = new WorkpaperStep41PreparedSessionMetrics(dialect.ProviderName)
            {
                DedicatedConnection = dedicatedConnection,
                ConsistencyMode = sqlServer
                    ? "serializable"
                    : "transaction-snapshot"
            };
            metrics.Connections++;
            await connection.OpenAsync(cancellationToken);
            transaction = sqlServer
                ? await connection.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken)
                : await connection.BeginTransactionAsync(cancellationToken);
            metrics.Transactions++;
            metrics.TransactionIsolationLevel = transaction.IsolationLevel.ToString();

            var session = new WorkpaperStep41PreparedSession(
                connection,
                transaction,
                dialect,
                context,
                scenarioScope,
                hitVoucherScenarioScope,
                lineItemKind,
                schemaPrefix,
                sqlServer,
                metrics);
            await session.VerifySchemaReadinessAsync(cancellationToken);
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

    public async IAsyncEnumerable<WorkpaperStep41SourceRow> ReadRowsAsync(
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
            var documentNumber = reader.IsDBNull(0) ? null : reader.GetString(0);
            var lineItem = reader.IsDBNull(1) ? null : reader.GetString(1);
            var postDate = reader.IsDBNull(2) ? null : reader.GetString(2);
            var approvalDate = reader.IsDBNull(3) ? null : reader.GetString(3);
            var createdBy = reader.IsDBNull(4) ? null : reader.GetString(4);
            var approvedBy = reader.IsDBNull(5) ? null : reader.GetString(5);
            var accountCode = reader.IsDBNull(6) ? null : reader.GetString(6);
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

            if (rawJson is null)
            {
                throw MissingWorkpaperRawSource();
            }

            if (_lineItemKind == LegacyFieldKind.Number
                && lineItem is not null
                && numericSortKey is null)
            {
                throw MissingWorkpaperNumericLineItemSortKey();
            }

            var matchedPositions = DecodePositions(tagMask);
            Metrics.RowsRead = checked(Metrics.RowsRead + 1);
            yield return new WorkpaperStep41SourceRow(
                entryId,
                documentNumber,
                lineItem,
                postDate,
                approvalDate,
                createdBy,
                approvedBy,
                accountCode,
                accountName,
                amountScaled,
                description,
                sourceModule,
                isManual,
                rawJson,
                matchedPositions);
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

    private async Task VerifySchemaReadinessAsync(CancellationToken cancellationToken)
    {
        await using var command = CreateCommand();
        command.CommandText = _sqlServer
            ? $"SELECT [value] FROM {_schemaPrefix}schema_info WHERE [key] = 'schema_version';"
            : "SELECT value FROM schema_info WHERE key = 'schema_version';";
        command.CommandTimeout = 0;
        Metrics.SchemaReadinessCommands++;
        RecordCommand("schemaReadiness", command);
        var raw = await command.ExecuteScalarAsync(cancellationToken);
        var versionText = Convert.ToString(raw, CultureInfo.InvariantCulture);
        if (!int.TryParse(
                versionText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var version)
            || version < RequiredSchemaVersion)
        {
            throw new InvalidDataException(
                $"WorkingPaper step4-1 需要資料庫 schema v{RequiredSchemaVersion}，"
                + $"目前為 '{versionText ?? "null"}'。");
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
                    // Transaction completion and physical connection disposal remain the backstop.
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
        var voucher = _sqlServer
            ? $"""
               IF OBJECT_ID('tempdb..{_voucherTable}') IS NOT NULL DROP TABLE {_voucherTable};
               CREATE TABLE {_voucherTable}
                   (document_number NVARCHAR(450) COLLATE Latin1_General_BIN2 NOT NULL PRIMARY KEY);
               """
            : _dialect is SqliteDialect
                ? $"""
                   DROP TABLE IF EXISTS {_voucherTable};
                   CREATE TEMP TABLE {_voucherTable}
                       (document_number TEXT COLLATE BINARY NOT NULL PRIMARY KEY);
                   """
                : $"""
                   DROP TABLE IF EXISTS {_voucherTable};
                   CREATE TEMP TABLE {_voucherTable}
                       (document_number TEXT NOT NULL PRIMARY KEY);
                   """;
        var tag = _sqlServer
            ? $"""
               IF OBJECT_ID('tempdb..{_tagTable}') IS NOT NULL DROP TABLE {_tagTable};
               CREATE TABLE {_tagTable}
                   (entry_id BIGINT NOT NULL PRIMARY KEY, tag_mask BIGINT NOT NULL);
               """
            : $"""
               DROP TABLE IF EXISTS {_tagTable};
               CREATE TEMP TABLE {_tagTable}
                   (entry_id BIGINT NOT NULL PRIMARY KEY, tag_mask BIGINT NOT NULL);
               """;
        return voucher + Environment.NewLine + tag;
    }

    private string CreateVoucherSql() =>
        $"""
         INSERT INTO {_voucherTable} (document_number)
         SELECT DISTINCT g.document_number
         FROM {_schemaPrefix}result_filter_run r
         JOIN {_schemaPrefix}target_gl_entry g ON g.entry_id = r.entry_id
         WHERE g.document_number IS NOT NULL
           AND {GlPopulationScopeSql.Predicate(_context, "g")}
           {ScenarioPredicate("r.scenario_position")};
         """;

    private string CreateTagSql()
    {
        var expandedRows = _hitVoucherScenarioScope.IsEmpty
            ? string.Empty
            : $"""
               UNION
               SELECT expanded.entry_id, r.scenario_position
               FROM {_schemaPrefix}result_filter_run r
               JOIN {_schemaPrefix}target_gl_entry hit ON hit.entry_id = r.entry_id
               JOIN {_schemaPrefix}target_gl_entry expanded
                 ON expanded.document_number = hit.document_number
               WHERE hit.document_number IS NOT NULL
                 AND {GlPopulationScopeSql.Predicate(_context, "hit")}
                 AND {GlPopulationScopeSql.Predicate(_context, "expanded")}
                 {_hitVoucherScenarioScope.Predicate("r.scenario_position")}
               """;

        return $"""
               INSERT INTO {_tagTable} (entry_id, tag_mask)
               SELECT tag_source.entry_id,
                      SUM(CASE tag_source.scenario_position
                          WHEN 1 THEN 1
                          WHEN 2 THEN 2
                          WHEN 3 THEN 4
                          WHEN 4 THEN 8
                          WHEN 5 THEN 16
                          WHEN 6 THEN 32
                          WHEN 7 THEN 64
                          WHEN 8 THEN 128
                          WHEN 9 THEN 256
                          WHEN 10 THEN 512
                          ELSE 0 END)
               FROM (
                   SELECT r.entry_id, r.scenario_position
                   FROM {_schemaPrefix}result_filter_run r
                   JOIN {_schemaPrefix}target_gl_entry g ON g.entry_id = r.entry_id
                   WHERE {GlPopulationScopeSql.Predicate(_context, "g")}
                     {ScenarioPredicate("r.scenario_position")}
                   {expandedRows}
               ) tag_source
               GROUP BY tag_source.entry_id;
               """;
    }

    private string OrderedRowsSql()
    {
        var documentOrder = OrdinalText("g.document_number");
        var lineOrder = _lineItemKind switch
        {
            LegacyFieldKind.Number =>
                OrdinalText(
                    "CASE WHEN g.line_item IS NULL THEN '' "
                    + "ELSE COALESCE(g.line_item_numeric_sort_key, '') END"),
            LegacyFieldKind.Text or LegacyFieldKind.Date or LegacyFieldKind.Time =>
                OrdinalText("COALESCE(g.line_item, '')"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(_lineItemKind),
                _lineItemKind,
                "Unsupported step4-1 line item field kind.")
        };
        const string lineNullRank = "CASE WHEN g.line_item IS NULL THEN 0 ELSE 1 END";
        return
            "SELECT g.document_number, g.line_item, g.post_date, g.approval_date, "
            + "       g.created_by, g.approved_by, g.account_code, g.account_name, "
            + "       g.amount_scaled, g.document_description, g.source_module, g.is_manual, "
            + "       raw.row_json, g.line_item_numeric_sort_key, g.entry_id, "
            + "       COALESCE(t.tag_mask, 0) "
            + $"FROM {_schemaPrefix}target_gl_entry g "
            + $"JOIN {_voucherTable} v ON v.document_number = g.document_number "
            + $"LEFT JOIN {_schemaPrefix}staging_gl_raw_row raw "
            + "  ON raw.batch_id = g.batch_id AND raw.row_number = g.source_row_number "
            + $"LEFT JOIN {_tagTable} t ON t.entry_id = g.entry_id "
            + $"WHERE {GlPopulationScopeSql.Predicate(_context, "g")} "
            + $"ORDER BY {documentOrder}, {lineNullRank}, {lineOrder}, g.entry_id;";
    }

    private string CleanupSql() => _sqlServer
        ? $"""
           IF OBJECT_ID('tempdb..{_tagTable}') IS NOT NULL DROP TABLE {_tagTable};
           IF OBJECT_ID('tempdb..{_voucherTable}') IS NOT NULL DROP TABLE {_voucherTable};
           """
        : $"""
           DROP TABLE IF EXISTS {_tagTable};
           DROP TABLE IF EXISTS {_voucherTable};
           """;

    private string ScenarioPredicate(string columnExpression) =>
        _scenarioScope.IsEmpty
            ? "AND 1 = 0"
            : _scenarioScope.Predicate(columnExpression);

    private string OrdinalText(string expression) =>
        _sqlServer
            ? $"({expression}) COLLATE Latin1_General_BIN2"
            : expression;

    private static IReadOnlyList<int> DecodePositions(long tagMask)
    {
        if (tagMask == 0)
        {
            return [];
        }

        var positions = new List<int>(10);
        for (var position = 1; position <= 10; position++)
        {
            if ((tagMask & (1L << (position - 1))) != 0)
            {
                positions.Add(position);
            }
        }
        return positions;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(WorkpaperStep41PreparedSession));
        }
    }

    private static JetActionException MissingWorkpaperRawSource() => new(
        JetErrorCodes.StaleResult,
        "底稿引用的原始 GL 列已不完整，請重新匯入、配對並執行對應步驟後再產出底稿。");

    private static JetActionException MissingWorkpaperNumericLineItemSortKey() => new(
        JetErrorCodes.StaleResult,
        "底稿引用的 GL 傳票文件項次排序鍵已過期，請重新匯入、配對並執行對應步驟後再產出底稿。");
}
