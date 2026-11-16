using System.Collections;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using JET.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace JET.Infrastructure;

/// <summary>
/// 操作監看模組：從旁記錄每次操作的時間花在哪裡，不參與審計判斷，也不改變操作的流程與結果。
/// 它只在四個地方接上程式：dispatcher 開始與結束一次操作、SQLite 命令執行與讀取、對前端發布事件，
/// 以及原本就給診斷用的步驟紀錄 <see cref="SupportOperationStage"/> 進入步驟時；審計邏輯不呼叫它。記錄的內容：
/// <list type="bullet">
/// <item>操作跑太久時定時記一筆目前的步驟、SQL 用掉的時間，以及程式用掉的處理器時間與記憶體。</item>
/// <item>SQL 在 SQLite 裡跑太久（還在跑或已跑完）、被取消或出錯時各記一筆；慢查詢在診斷日誌另附執行計畫。</item>
/// <item>匯出報表的進度變化。</item>
/// <item>操作結束時的摘要：經過的步驟、SQL 筆數、讀取列數、要求取消到真正停下的時間，以及最花時間的語句。</item>
/// </list>
/// SQL 文字、參數值與執行計畫只寫進診斷日誌；支援日誌依它自己的允許清單只收時間、筆數與程式代號。
/// 監看模組自己出錯時吞掉，不往外丟。
/// </summary>
public sealed class OperationMonitor : IOperationMonitor, ISqlStatementObserver, IDisposable
{
    private const int MaxPlannedSqlTexts = 256;
    private const int TopSqlCount = 5;
    private const int SummarySqlLength = 300;

    private static readonly EventId StillRunningEvent = new(2600, SupportEventNames.ActionStillRunning);
    private static readonly EventId SummaryEvent = new(2601, SupportEventNames.ActionSummary);
    private static readonly EventId StageEnteredEvent = new(2602, SupportEventNames.ActionStageEntered);
    private static readonly EventId SqlSlowEvent = new(2610, SupportEventNames.SqlSlow);
    private static readonly EventId SqlCancelledEvent = new(2611, SupportEventNames.SqlCancelled);
    private static readonly EventId SqlFailedEvent = new(2612, SupportEventNames.SqlFailed);
    private static readonly EventId SqlPlanEvent = new(2613, SupportEventNames.SqlPlan);
    private static readonly EventId ExportProgressEvent = new(2620, SupportEventNames.ExportProgress);

    private readonly ILogger _logger;
    private readonly AsyncLocal<OperationWatch?> _current = new();
    private readonly ConcurrentDictionary<OperationWatch, byte> _operations = new();
    private readonly ConcurrentDictionary<SqlStatementWatch, byte> _statements = new();
    private readonly ConcurrentDictionary<string, byte> _plannedSql = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _pendingPlans = new();
    private readonly ExportProgressThrottle _unownedExportProgress = new();
    private readonly System.Threading.Timer _timer;
    private int _checking;
    private int _disposed;

    public OperationMonitor(ILogger logger, OperationMonitorOptions? options = null)
    {
        _logger = logger;
        Options = options ?? OperationMonitorOptions.Default;
        _timer = CreateTimer();
    }

    public OperationMonitorOptions Options { get; }

    public IDisposable BeginOperation(string action, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return NoOperation.Instance;
        }

        try
        {
            var operation = new OperationWatch(this, action, _current.Value);
            // 先登記取消，再讓計時器與 SQL 看得到這次操作；登記失敗時不留下沒人結束的操作。
            operation.WatchCancellation(cancellationToken);
            SupportOperationStage.Current?.Observe(operation.StageEntered);
            _current.Value = operation;
            _operations.TryAdd(operation, 0);
            return operation;
        }
        catch (Exception)
        {
            return NoOperation.Instance;
        }
    }

    public void ObservePublishedEvent(string eventName, object payload)
    {
        if (!string.Equals(eventName, SupportEventNames.ExportProgress, StringComparison.Ordinal)
            || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            var phase = Read(payload, "phase")?.ToString();
            var sheetName = Read(payload, "sheetName")?.ToString();
            var sheetsCompleted = Convert.ToInt64(Read(payload, "sheetsCompleted") ?? 0L);
            var throttle = _current.Value?.ExportProgress ?? _unownedExportProgress;
            if (!throttle.ShouldLog(phase, sheetName, sheetsCompleted, Stopwatch.GetTimestamp(), Options.ExportProgressInterval))
            {
                return;
            }

            var reportKind = Read(payload, "reportKind")?.ToString();
            var rowsWritten = Convert.ToInt64(Read(payload, "rowsWritten") ?? 0L);
            var elapsed = Convert.ToInt64(Read(payload, "elapsedMilliseconds") ?? 0L);
            Write(
                context: null,
                LogLevel.Information,
                ExportProgressEvent,
                $"export {reportKind} {phase} sheet={sheetName} sheets_completed={sheetsCompleted} rows={rowsWritten} in {elapsed} ms",
                new()
                {
                    ["report_kind"] = reportKind,
                    ["phase"] = phase,
                    ["sheet_name"] = sheetName,
                    ["sheets_completed"] = sheetsCompleted,
                    ["rows_written"] = rowsWritten,
                    ["elapsed_ms"] = elapsed,
                });
        }
        catch (Exception)
        {
            // 監看模組出錯不影響發布事件。
        }
    }

    public SqlStatementWatch? Begin(DbCommand command)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return null;
        }

        var statement = new SqlStatementWatch(this, _current.Value, command);
        _statements.TryAdd(statement, 0);
        statement.Operation?.StatementStarted();
        return statement;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            // 等正在跑的檢查與執行計畫結束，之後才釋放 logger，避免寫到已釋放的 logger。
            // 用 DisposeAsync 等計時器：等不到時直接放手，不留下會在之後被通知的 wait handle。
            _timer.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            Task.WaitAll([.. _pendingPlans.Keys], TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // 釋放時的等待只是盡量，失敗也照常關閉。
        }
    }

    internal void LogStageEntered(OperationWatch operation, string stage, long offsetMilliseconds)
    {
        try
        {
            Write(
                context: null,
                LogLevel.Debug,
                StageEnteredEvent,
                $"action {operation.Action} entered {stage} at {offsetMilliseconds} ms",
                new()
                {
                    ["action"] = operation.Action,
                    ["stage"] = stage,
                    ["elapsed_ms"] = offsetMilliseconds,
                });
        }
        catch (Exception)
        {
            // 監看模組出錯不影響操作本身。
        }
    }

    internal void OperationEnded(OperationWatch operation)
    {
        try
        {
            _operations.TryRemove(operation, out _);
            if (ReferenceEquals(_current.Value, operation))
            {
                _current.Value = operation.Parent;
            }

            WriteSummary(operation);
        }
        catch (Exception)
        {
            // 監看模組出錯不影響操作本身。
        }
    }

    internal void StatementFinished(SqlStatementWatch statement, Exception? exception, bool cancellationRequested)
    {
        try
        {
            _statements.TryRemove(statement, out _);
            var now = Stopwatch.GetTimestamp();
            var callStarted = statement.CallStartedTimestamp;
            // 從 exception filter 呼叫時，最後一次進入 SQLite 的時間還沒加進合計。
            var sqlTicks = statement.SqlTicks + (callStarted == 0 ? 0 : now - callStarted);
            var cancelled = exception is OperationCanceledException || (exception is not null && cancellationRequested);
            var slow = statement.SlowLogged != 0 || sqlTicks >= Ticks(Options.SlowSqlThreshold);
            statement.Operation?.StatementEnded(
                statement.Sql,
                sqlTicks,
                statement.RowsRead,
                slow,
                failed: exception is not null && !cancelled);

            var elapsed = Milliseconds(now - statement.StartTimestamp);
            var step = StepName(statement.Step);
            if (exception is not null)
            {
                // 在執行語句的執行緒上，命令還沒交還給呼叫端，可以安全讀參數。
                statement.CapturedParameters ??= ReadParameters(statement.Command);
            }

            if (cancelled)
            {
                Write(
                    context: null,
                    LogLevel.Information,
                    SqlCancelledEvent,
                    $"sql cancelled during {step} after {elapsed} ms",
                    StatementFields(statement, elapsed, sqlTicks, step));
            }
            else if (exception is not null)
            {
                var fields = StatementFields(statement, elapsed, sqlTicks, step);
                fields["error_type"] = exception.GetType().Name;
                fields["error_message"] = exception.Message;
                if (exception is SqliteException sqliteException)
                {
                    fields["sqlite_error_code"] = sqliteException.SqliteErrorCode;
                    fields["sqlite_extended_error_code"] = sqliteException.SqliteExtendedErrorCode;
                }

                Write(
                    context: null,
                    LogLevel.Warning,
                    SqlFailedEvent,
                    $"sql failed during {step} after {elapsed} ms: {exception.GetType().Name}",
                    fields);
            }
            else if (sqlTicks >= Ticks(Options.SlowSqlThreshold))
            {
                statement.CapturedParameters ??= ReadParameters(statement.Command);
                var fields = StatementFields(statement, elapsed, sqlTicks, step);
                fields["still_running"] = false;
                Write(
                    context: null,
                    LogLevel.Information,
                    SqlSlowEvent,
                    $"sql finished after {Milliseconds(sqlTicks)} ms in SQLite ({elapsed} ms in total)",
                    fields);
                RequestPlan(statement);
            }
        }
        catch (Exception)
        {
            // 監看模組出錯不影響語句本身。
        }
    }

    private System.Threading.Timer CreateTimer()
    {
        // 計時器不帶建立當下的日誌 scope；每一筆紀錄改用該操作或語句開始時的 scope。
        AsyncFlowControl? suppressed = ExecutionContext.IsFlowSuppressed() ? null : ExecutionContext.SuppressFlow();
        try
        {
            return new System.Threading.Timer(
                static state => ((OperationMonitor)state!).Check(),
                this,
                Options.CheckInterval,
                Options.CheckInterval);
        }
        finally
        {
            suppressed?.Dispose();
        }
    }

    private void Check()
    {
        if (Interlocked.Exchange(ref _checking, 1) != 0)
        {
            return;
        }

        try
        {
            var now = Stopwatch.GetTimestamp();
            foreach (var pair in _statements)
            {
                CheckStatement(pair.Key, now);
            }

            foreach (var pair in _operations)
            {
                CheckOperation(pair.Key, now);
            }
        }
        catch (Exception)
        {
            // 監看模組出錯不影響操作本身；下一次檢查照常進行。
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    private void CheckStatement(SqlStatementWatch statement, long now)
    {
        var callStarted = statement.CallStartedTimestamp;
        if (callStarted == 0 || now - callStarted < Ticks(Options.SlowSqlThreshold))
        {
            return;
        }

        var first = statement.SlowLogged == 0;
        if (!first && now - statement.LastSlowLogTimestamp < Ticks(Options.SlowSqlRepeat))
        {
            return;
        }

        statement.SlowLogged = 1;
        statement.LastSlowLogTimestamp = now;
        // 語句還在 SQLite 裡，執行它的執行緒停在這次呼叫上，參數不會在這時被改動。
        statement.CapturedParameters ??= ReadParameters(statement.Command);
        var callTicks = now - callStarted;
        var elapsed = Milliseconds(now - statement.StartTimestamp);
        var step = StepName(statement.Step);
        var fields = StatementFields(statement, elapsed, statement.SqlTicks + callTicks, step);
        fields["still_running"] = true;
        fields["sql_call_ms"] = Milliseconds(callTicks);
        Write(
            statement.Context,
            LogLevel.Information,
            SqlSlowEvent,
            $"sql still running in SQLite for {Milliseconds(callTicks)} ms during {step} ({elapsed} ms in total)",
            fields);
        if (first)
        {
            RequestPlan(statement);
        }
    }

    private void CheckOperation(OperationWatch operation, long now)
    {
        operation.ObserveCancellation();
        var elapsedTicks = now - operation.StartTimestamp;
        if (elapsedTicks < Ticks(Options.StillRunningAfter))
        {
            return;
        }

        if (operation.LastHeartbeatTimestamp != 0
            && now - operation.LastHeartbeatTimestamp < Ticks(Options.StillRunningRepeat))
        {
            return;
        }

        var cpu = Environment.CpuUsage.TotalTime;
        var intervalStart = operation.LastHeartbeatTimestamp == 0 ? operation.StartTimestamp : operation.LastHeartbeatTimestamp;
        var intervalCpu = cpu - operation.LastHeartbeatCpu;
        operation.LastHeartbeatTimestamp = now;
        operation.LastHeartbeatCpu = cpu;
        operation.Heartbeats++;

        var elapsed = Milliseconds(elapsedTicks);
        var fields = new LogFields
        {
            ["action"] = operation.Action,
            ["elapsed_ms"] = elapsed,
            ["current_stage"] = operation.CurrentStage,
            ["sql_statements"] = operation.Statements,
            ["sql_rows_read"] = operation.RowsRead,
            ["sql_ms"] = Milliseconds(operation.SqlTicks + ActiveCallTicks(operation, now, out var activeStep)),
            ["active_sql_step"] = activeStep,
            ["process_cpu_ms"] = (long)(cpu - operation.StartCpu).TotalMilliseconds,
            ["interval_ms"] = Milliseconds(now - intervalStart),
            ["interval_cpu_ms"] = (long)intervalCpu.TotalMilliseconds,
        };
        AddMemory(fields);
        Write(
            operation.Context,
            LogLevel.Information,
            StillRunningEvent,
            $"action {operation.Action} still running after {elapsed} ms at {operation.CurrentStage ?? "(no stage)"}",
            fields);
    }

    /// <summary>這次操作目前正在 SQLite 裡的語句已經待了多久；沒有時是 0。</summary>
    private long ActiveCallTicks(OperationWatch operation, long now, out string? step)
    {
        step = null;
        long longest = 0;
        foreach (var pair in _statements)
        {
            var statement = pair.Key;
            var callStarted = statement.CallStartedTimestamp;
            if (ReferenceEquals(statement.Operation, operation) && callStarted != 0 && now - callStarted > longest)
            {
                longest = now - callStarted;
                step = StepName(statement.Step);
            }
        }

        return longest;
    }

    private void WriteSummary(OperationWatch operation)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedTicks = now - operation.StartTimestamp;
        var cancelRequested = operation.CancelRequestedTimestamp;
        var notable = elapsedTicks >= Ticks(Options.SummaryAfter)
            || operation.SlowStatements > 0
            || operation.FailedStatements > 0
            || cancelRequested != 0
            || operation.Heartbeats > 0;
        if (!notable && operation.Statements == 0 && operation.StagesEntered == 0)
        {
            return;
        }

        var level = notable ? LogLevel.Information : LogLevel.Debug;
        if (!_logger.IsEnabled(level))
        {
            return;
        }

        var elapsed = Milliseconds(elapsedTicks);
        var fields = new LogFields
        {
            ["action"] = operation.Action,
            ["elapsed_ms"] = elapsed,
            ["stage_path"] = operation.StagePath(),
            ["sql_statements"] = operation.Statements,
            ["sql_slow_statements"] = operation.SlowStatements,
            ["sql_failed_statements"] = operation.FailedStatements,
            ["sql_rows_read"] = operation.RowsRead,
            ["sql_ms"] = Milliseconds(operation.SqlTicks),
            ["longest_sql_ms"] = Milliseconds(operation.LongestStatementTicks),
            ["process_cpu_ms"] = (long)(Environment.CpuUsage.TotalTime - operation.StartCpu).TotalMilliseconds,
        };
        if (cancelRequested != 0)
        {
            fields["cancel_requested_after_ms"] = Milliseconds(cancelRequested - operation.StartTimestamp);
            fields["cancel_latency_ms"] = Milliseconds(now - cancelRequested);
        }

        AddMemory(fields);
        if (operation.Statements > 0)
        {
            fields["top_sql"] = operation.TopSql(TopSqlCount, Milliseconds, sql => Shorten(sql, SummarySqlLength));
        }

        Write(
            operation.Context,
            level,
            SummaryEvent,
            $"action {operation.Action} took {elapsed} ms, {operation.Statements} sql statements, "
                + $"{Milliseconds(operation.SqlTicks)} ms in SQLite",
            fields);
    }

    /// <summary>
    /// 在診斷日誌附上慢查詢的執行計畫。只處理單一的 SELECT 或 WITH 查詢，同一段 SQL 只做一次；
    /// 另開一條唯讀、不進連線池的連線執行 EXPLAIN QUERY PLAN，不經過正在執行的連線，也不實際執行查詢。
    /// </summary>
    private void RequestPlan(SqlStatementWatch statement)
    {
        if (!_logger.IsEnabled(LogLevel.Debug)
            || Volatile.Read(ref _disposed) != 0
            || !IsSingleQuery(statement.Sql)
            || _plannedSql.Count >= MaxPlannedSqlTexts
            || !_plannedSql.TryAdd(statement.Sql, 0))
        {
            return;
        }

        var connectionString = statement.Command.Connection?.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        var parameters = statement.CapturedParameters ?? [];
        var task = Task.Run(() =>
        {
            try
            {
                var fields = new LogFields { ["sql"] = statement.Sql };
                try
                {
                    fields["sql_plan"] = Explain(dataSource, statement.Sql, parameters);
                }
                catch (Exception exception)
                {
                    fields["plan_error"] = $"{exception.GetType().Name}: {exception.Message}";
                }

                Write(statement.Context, LogLevel.Debug, SqlPlanEvent, "sql query plan", fields);
            }
            catch (Exception)
            {
                // 執行計畫只是附加資訊，做不出來就不記。
            }
        });
        _pendingPlans.TryAdd(task, 0);
        task.ContinueWith(
            static (finished, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(finished, out _),
            _pendingPlans,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string Explain(string dataSource, string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 2,
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var depth = new Dictionary<long, int>();
        var text = new StringBuilder();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var parent = reader.GetInt64(1);
            var level = depth.TryGetValue(parent, out var parentDepth) ? parentDepth + 1 : 0;
            depth[id] = level;
            if (text.Length > 0)
            {
                text.Append('\n');
            }

            text.Append(' ', level * 2).Append(reader.GetString(3));
        }

        return text.ToString();
    }

    private static bool IsSingleQuery(string sql)
    {
        var trimmed = sql.Trim().TrimEnd(';').TrimEnd();
        return trimmed.Length > 0
            && !trimmed.Contains(';', StringComparison.Ordinal)
            && (trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<KeyValuePair<string, object?>> ReadParameters(DbCommand command)
    {
        var parameters = new List<KeyValuePair<string, object?>>(command.Parameters.Count);
        foreach (DbParameter parameter in command.Parameters)
        {
            parameters.Add(new(parameter.ParameterName, parameter.Value is DBNull ? null : parameter.Value));
        }

        return parameters;
    }

    private static LogFields StatementFields(
        SqlStatementWatch statement,
        long elapsedMilliseconds,
        long sqlTicks,
        string step)
    {
        var fields = new LogFields
        {
            ["elapsed_ms"] = elapsedMilliseconds,
            ["sql_ms"] = Milliseconds(sqlTicks),
            ["sql_step"] = step,
            ["rows_read"] = statement.RowsRead,
            ["sql"] = statement.Sql,
        };
        if (statement.CapturedParameters is { Count: > 0 } parameters)
        {
            fields["parameters"] = string.Join(
                "; ",
                parameters.Select(pair => $"{pair.Key}={(pair.Value is null ? "NULL" : pair.Value.ToString())}"));
        }

        return fields;
    }

    private static void AddMemory(LogFields fields)
    {
        fields["working_set_mb"] = Environment.WorkingSet / (1024 * 1024);
        fields["managed_heap_mb"] = GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024);
    }

    private void Write(ExecutionContext? context, LogLevel level, EventId eventId, string message, LogFields fields)
    {
        if (!_logger.IsEnabled(level))
        {
            return;
        }

        var state = new LogState(message, fields);
        if (context is null)
        {
            _logger.Log(level, eventId, state, null, static (value, _) => value.Message);
            return;
        }

        // 在別的執行緒寫紀錄時，換回該操作或語句開始時的日誌 scope，紀錄才帶得到 correlation_id。
        ExecutionContext.Run(
            context,
            static value =>
            {
                var (logger, logLevel, id, logState) = ((ILogger, LogLevel, EventId, LogState))value!;
                logger.Log(logLevel, id, logState, null, static (item, _) => item.Message);
            },
            (_logger, level, eventId, state));
    }

    private static object? Read(object payload, string name) =>
        payload.GetType().GetProperty(name)?.GetValue(payload);

    private static string StepName(SqlStep step) => step == SqlStep.Read ? "read" : "execute";

    private static long Ticks(TimeSpan duration) => (long)(duration.TotalSeconds * Stopwatch.Frequency);

    private static long Milliseconds(long ticks) => ticks * 1000 / Stopwatch.Frequency;

    private static string Shorten(string sql, int length)
    {
        var single = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return single.Length <= length ? single : single[..length] + "…";
    }

    private sealed class LogFields : Dictionary<string, object?>
    {
        public LogFields()
            : base(StringComparer.Ordinal)
        {
        }
    }

    /// <summary>給 logger 的欄位清單；診斷日誌與支援日誌都從這裡逐一讀出欄位。</summary>
    private sealed class LogState(string message, LogFields fields) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly KeyValuePair<string, object?>[] _items = [.. fields];

        public string Message => message;

        public int Count => _items.Length;

        public KeyValuePair<string, object?> this[int index] => _items[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)_items).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => message;
    }

    private sealed class NoOperation : IDisposable
    {
        public static readonly NoOperation Instance = new();

        public void Dispose()
        {
        }
    }
}
