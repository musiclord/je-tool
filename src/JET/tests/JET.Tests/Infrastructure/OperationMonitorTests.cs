using System.Data.Common;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 操作監看模組：跑太久、被中斷或出錯的 SQL，長時間的操作、匯出進度與每次操作的摘要都寫進日誌；
/// 監看模組本身出錯時，查詢結果與例外不變。用真的 SQLite 案件資料庫與縮短的門檻執行。
/// </summary>
public sealed class OperationMonitorTests
{
    /// <summary>算 1 加到 n 的遞迴查詢；n 夠大時要在 SQLite 裡跑上一段時間，不讀任何資料表。</summary>
    private static string CountingSql(long n) =>
        $"WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < {n}) SELECT SUM(x) FROM c;";

    private static OperationMonitorOptions Fast(
        TimeSpan? stillRunningAfter = null,
        TimeSpan? exportProgressInterval = null) => new()
        {
            CheckInterval = TimeSpan.FromMilliseconds(20),
            SlowSqlThreshold = TimeSpan.FromMilliseconds(100),
            SlowSqlRepeat = TimeSpan.FromHours(1),
            StillRunningAfter = stillRunningAfter ?? TimeSpan.FromHours(1),
            StillRunningRepeat = TimeSpan.FromHours(1),
            ExportProgressInterval = exportProgressInterval ?? TimeSpan.FromHours(1),
            SummaryAfter = TimeSpan.FromHours(1),
        };

    [Fact]
    public async Task SlowQuery_LogsSqlSlowWhenFinished_WithSqlTextInDiagnosticLogOnly()
    {
        using var env = new Env(Fast());
        object? sum;
        using (env.Scope("corr-slow"))
        using (env.Monitor.BeginOperation("test.slow", CancellationToken.None))
        {
            await using var connection = env.Open();
            await using var command = connection.CreateCommand();
            command.CommandText = CountingSql(5_000_000);
            sum = await command.ExecuteScalarAsync();
        }

        Assert.Equal(12_500_002_500_000L, Convert.ToInt64(sum));
        // 建表的語句沒有這個 correlation，機器慢時它們也可能超過門檻，所以只看這次查詢的紀錄。
        // 還在跑的那一筆由背景計時器寫，查詢結束前不一定輪得到；那一筆在取消查詢的測試裡等到出現再檢查。
        var slow = env.Diagnostic.Snapshot().Where(e => e.EventName == "sql.slow" && e.CorrelationId == "corr-slow").ToList();
        var finished = Assert.Single(slow, e => Equals(e.Fields["still_running"], false));
        Assert.True(Convert.ToInt64(finished.Fields["sql_ms"]) >= 100);
        Assert.Equal("execute", finished.Fields["sql_step"]?.ToString());
        Assert.Contains("WITH RECURSIVE", finished.Fields["sql"]?.ToString(), StringComparison.Ordinal);

        // 支援日誌收得到同一件事，但只有時間與步驟，沒有 SQL。
        var support = env.Support.Snapshot().Where(e => e.EventName == "sql.slow" && e.CorrelationId == "corr-slow").ToList();
        Assert.NotEmpty(support);
        Assert.All(support, e =>
        {
            Assert.False(e.Fields.ContainsKey("sql"));
            Assert.False(e.Fields.ContainsKey("parameters"));
            Assert.True(e.Fields.ContainsKey("sql_ms"));
        });
    }

    [Fact]
    public async Task CancelledQuery_LogsSqlSlowWhileRunning_ThenSqlCancelledWithElapsedTime_AndTheCancellationStillPropagates()
    {
        using var env = new Env(Fast());
        using var cancellation = new CancellationTokenSource();
        Task<object?> running;
        using (env.Scope("corr-cancel"))
        using (env.Monitor.BeginOperation("test.cancel", cancellation.Token))
        {
            var connection = env.Open();
            var command = connection.CreateCommand();
            command.CommandText = CountingSql(2_000_000_000);
            running = Task.Run(() => command.ExecuteScalarAsync(cancellation.Token));
            // 等監看模組記下語句已在 SQLite 裡超過門檻才取消，經過時間的下限才固定，不靠等待的長短。
            await env.WaitForAsync("sql.slow", "corr-cancel");
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            await command.DisposeAsync();
            await connection.DisposeAsync();
        }

        // 查詢沒有跑完，sql.slow 只會是計時器在查詢進行中寫的紀錄。
        var slowWhileRunning = Assert.Single(env.Diagnostic.Snapshot(), e => e.EventName == "sql.slow" && e.CorrelationId == "corr-cancel");
        Assert.True(Equals(slowWhileRunning.Fields["still_running"], true));
        Assert.True(Convert.ToInt64(slowWhileRunning.Fields["sql_call_ms"]) >= 100);
        Assert.Contains("WITH RECURSIVE", slowWhileRunning.Fields["sql"]?.ToString(), StringComparison.Ordinal);
        var supportRunning = Assert.Single(env.Support.Snapshot(), e => e.EventName == "sql.slow" && e.CorrelationId == "corr-cancel");
        Assert.True(Equals(supportRunning.Fields["still_running"], true));
        Assert.False(supportRunning.Fields.ContainsKey("sql"));
        Assert.False(supportRunning.Fields.ContainsKey("parameters"));

        var cancelled = Assert.Single(env.Diagnostic.Snapshot(), e => e.EventName == "sql.cancelled");
        Assert.True(Convert.ToInt64(cancelled.Fields["elapsed_ms"]) >= 100);
        Assert.Equal("execute", cancelled.Fields["sql_step"]?.ToString());
        Assert.Contains("WITH RECURSIVE", cancelled.Fields["sql"]?.ToString(), StringComparison.Ordinal);
        Assert.Equal("corr-cancel", cancelled.CorrelationId);
        var summary = Assert.Single(env.Diagnostic.Snapshot(), e => e.EventName == "action.summary");
        Assert.Equal("Information", summary.Level);
        Assert.True(Convert.ToInt64(summary.Fields["cancel_requested_after_ms"]) >= 100);
        Assert.True(Convert.ToInt64(summary.Fields["cancel_latency_ms"]) >= 0);
    }

    [Fact]
    public async Task FailedQuery_LogsSqlFailedWithSqliteErrorCode_AndTheSameExceptionReachesTheCaller()
    {
        using var env = new Env(Fast());
        SqliteException error;
        using (env.Scope("corr-failed"))
        using (env.Monitor.BeginOperation("test.failed", CancellationToken.None))
        {
            await using var connection = env.Open();
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM table_that_does_not_exist;";
            error = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteReaderAsync());
        }

        Assert.Equal(1, error.SqliteErrorCode);
        var failed = Assert.Single(env.Diagnostic.Snapshot(), e => e.EventName == "sql.failed");
        Assert.Equal(1, Convert.ToInt32(failed.Fields["sqlite_error_code"]));
        Assert.Contains("table_that_does_not_exist", failed.Fields["sql"]?.ToString(), StringComparison.Ordinal);
        Assert.Equal("corr-failed", failed.CorrelationId);
    }

    [Fact]
    public async Task Summary_CountsStatementsRowsAndStages_AndLogsEachStageEntered()
    {
        // 這裡測的是短操作的摘要，慢查詢門檻拉長，機器忙時兩條語句也不會被算成慢查詢。
        using var env = new Env(Fast() with { SlowSqlThreshold = TimeSpan.FromHours(1) });
        using (env.Scope("corr-summary"))
        {
            SupportOperationStage.Begin();
            using (env.Monitor.BeginOperation("test.summary", CancellationToken.None))
            {
                await using var connection = env.Open();
                SupportOperationStage.Enter("first_stage");
                await using (var scalar = connection.CreateCommand())
                {
                    scalar.CommandText = "SELECT 1;";
                    await scalar.ExecuteScalarAsync();
                }

                SupportOperationStage.Enter("second_stage");
                await using DbCommand rows = connection.CreateCommand();
                rows.CommandText = "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 5) SELECT x FROM c;";
                await using var reader = await rows.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                }
            }
        }

        var entries = env.Diagnostic.Snapshot();
        var summary = Assert.Single(entries, e => e.EventName == "action.summary");
        Assert.Equal("Debug", summary.Level);
        Assert.Equal("test.summary", summary.Fields["action"]?.ToString());
        Assert.Equal(2L, Convert.ToInt64(summary.Fields["sql_statements"]));
        Assert.Equal(5L, Convert.ToInt64(summary.Fields["sql_rows_read"]));
        var stagePath = summary.Fields["stage_path"]?.ToString();
        Assert.Contains("first_stage", stagePath, StringComparison.Ordinal);
        Assert.Contains("second_stage", stagePath, StringComparison.Ordinal);
        Assert.Equal(
            ["first_stage", "second_stage"],
            entries.Where(e => e.EventName == "action.stage_entered").Select(e => e.Fields["stage"]?.ToString()).ToArray());
        Assert.Equal("corr-summary", summary.CorrelationId);
    }

    /// <summary>
    /// 步驟很多或名稱很長時，支援日誌的步驟路徑仍保留第一個與最後一個步驟；省略的步驟在中間標明個數，不在半途截斷。
    /// </summary>
    [Theory]
    [InlineData(12)]
    [InlineData(80)]
    public async Task Summary_LongStagePath_KeepsTheFirstAndLastStagesInTheSupportLog(int stageCount)
    {
        using var env = new Env(Fast() with { SlowSqlThreshold = TimeSpan.FromHours(1), SummaryAfter = TimeSpan.Zero });
        using (env.Scope("corr-stages"))
        {
            SupportOperationStage.Begin();
            using (env.Monitor.BeginOperation("test.stages", CancellationToken.None))
            {
                for (var i = 1; i <= stageCount; i++)
                {
                    SupportOperationStage.Enter($"report_manifest_write_{i:D2}");
                }

                await Task.Yield();
            }
        }

        var support = Assert.Single(env.Support.Snapshot(), e => e.EventName == "action.summary");
        var path = support.Fields["stage_path"]?.ToString()!;
        Assert.StartsWith("report_manifest_write_01@", path, StringComparison.Ordinal);
        Assert.Matches($@" > report_manifest_write_{stageCount:D2}@\d+ms$", path);
        Assert.True(path.Length <= 1024, $"stage_path 長度 {path.Length}");
        var diagnostic = Assert.Single(env.Diagnostic.Snapshot(), e => e.EventName == "action.summary");
        Assert.Equal(path, diagnostic.Fields["stage_path"]?.ToString());
        if (stageCount > 32)
        {
            Assert.Matches(@" > \(\+\d+ stages omitted\) > ", path);
        }
        else
        {
            Assert.DoesNotContain("omitted", path, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task LongOperation_LogsStillRunningWithCurrentStageAndProcessFigures()
    {
        using var env = new Env(Fast(stillRunningAfter: TimeSpan.FromMilliseconds(100)));
        using (env.Scope("corr-still"))
        {
            SupportOperationStage.Begin();
            using (env.Monitor.BeginOperation("test.still", CancellationToken.None))
            {
                SupportOperationStage.Enter("report_write");
                // 操作一直進行到定時紀錄出現為止，不靠固定的等待時間。
                await env.WaitForAsync("action.still_running", "corr-still");
            }
        }

        var still = Assert.Single(env.Diagnostic.Snapshot(), e => e.EventName == "action.still_running");
        Assert.Equal("Information", still.Level);
        Assert.Equal("report_write", still.Fields["current_stage"]?.ToString());
        Assert.True(Convert.ToInt64(still.Fields["elapsed_ms"]) >= 100);
        Assert.True(still.Fields.ContainsKey("process_cpu_ms"));
        Assert.True(still.Fields.ContainsKey("working_set_mb"));
        Assert.Equal("corr-still", still.CorrelationId);
        var summary = Assert.Single(env.Diagnostic.Snapshot(), e => e.EventName == "action.summary");
        Assert.Equal("Information", summary.Level);

        var support = Assert.Single(env.Support.Snapshot(), e => e.EventName == "action.still_running");
        Assert.Equal("report_write", support.Fields["current_stage"]?.ToString());
        Assert.Contains(env.Support.Snapshot(), e => e.EventName == "action.summary");
    }

    [Fact]
    public async Task SlowQuery_LogsItsQueryPlanInDiagnosticLog()
    {
        using var env = new Env(Fast());
        using (env.Scope("corr-plan"))
        using (env.Monitor.BeginOperation("test.plan", CancellationToken.None))
        {
            await using var connection = env.Open();
            await using var command = connection.CreateCommand();
            command.CommandText = CountingSql(5_000_000);
            await command.ExecuteScalarAsync();
        }

        var plan = await env.WaitForAsync("sql.plan", "corr-plan");
        Assert.False(string.IsNullOrWhiteSpace(plan.Fields["sql_plan"]?.ToString()));
        Assert.False(plan.Fields.ContainsKey("plan_error"));
        Assert.Contains("WITH RECURSIVE", plan.Fields["sql"]?.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(env.Support.Snapshot(), e => e.EventName == "sql.plan");
    }

    [Fact]
    public async Task ExportProgress_LogsPhaseAndSheetChangesAndThrottlesTheRest()
    {
        using var env = new Env(Fast());
        using (env.Scope("corr-export"))
        using (env.Monitor.BeginOperation("test.export", CancellationToken.None))
        {
            env.Monitor.ObservePublishedEvent("export.progress", Progress("writingSheet", "#Criteria Select 1", 0, 10));
            env.Monitor.ObservePublishedEvent("export.progress", Progress("writingSheet", "#Criteria Select 1", 0, 20));
            env.Monitor.ObservePublishedEvent("export.progress", Progress("writingSheet", "#Criteria Select 1", 1, 30));
            env.Monitor.ObservePublishedEvent("export.progress", Progress("finalizingWorkbook", null, 1, 30));
            env.Monitor.ObservePublishedEvent("import.progress", new { phase = "staging" });
            await Task.Yield();
        }

        var progress = env.Diagnostic.Snapshot().Where(e => e.EventName == "export.progress").ToList();
        Assert.Equal([10L, 30L, 30L], progress.Select(e => Convert.ToInt64(e.Fields["rows_written"])).ToArray());
        Assert.Equal(["writingSheet", "writingSheet", "finalizingWorkbook"], progress.Select(e => e.Fields["phase"]?.ToString()).ToArray());
        Assert.Equal("criteriaSelectionReport", progress[0].Fields["report_kind"]?.ToString());
        Assert.Equal("#Criteria Select 1", progress[0].Fields["sheet_name"]?.ToString());
        Assert.Equal(1L, Convert.ToInt64(progress[1].Fields["sheets_completed"]));
        Assert.All(progress, e => Assert.Equal("corr-export", e.CorrelationId));
        var support = env.Support.Snapshot().Where(e => e.EventName == "export.progress").ToList();
        Assert.Equal(3, support.Count);
        Assert.Equal(1L, Convert.ToInt64(support[1].Fields["sheets_completed"]));
        Assert.All(support, e => Assert.False(e.Fields.ContainsKey("sheet_name")));
    }

    /// <summary>
    /// 支援日誌會交給開發者，不帶工作表名稱；進度靠階段、完成幾張表與寫了幾列判斷。
    /// 底稿的說明頁名稱含有產品名稱，帶進支援日誌會和匯出檔的內容檢查衝突。
    /// </summary>
    [Theory]
    [InlineData("JE WorkingPaper說明")]
    [InlineData("R1\u0001")]
    public async Task ExportProgress_SheetNameStaysInTheDiagnosticLogOnly(string sheetName)
    {
        using var env = new Env(Fast());
        using (env.Scope("corr-sheet"))
        using (env.Monitor.BeginOperation("test.export", CancellationToken.None))
        {
            env.Monitor.ObservePublishedEvent("export.progress", Progress("writingSheet", sheetName, 0, 1));
            await Task.Yield();
        }

        var diagnostic = Assert.Single(env.Diagnostic.Snapshot(), e => e.EventName == "export.progress");
        Assert.Equal(sheetName, diagnostic.Fields["sheet_name"]?.ToString());
        var support = Assert.Single(env.Support.Snapshot(), e => e.EventName == "export.progress");
        Assert.False(support.Fields.ContainsKey("sheet_name"));
        Assert.Equal(1L, Convert.ToInt64(support.Fields["rows_written"]));
        Assert.DoesNotContain(support.Fields.Values, value => value?.ToString()?.Contains(sheetName, StringComparison.Ordinal) == true);
        Assert.DoesNotContain(sheetName, support.Message, StringComparison.Ordinal);
    }

    // 下面幾個情境和 SqliteCancellationBoundaryTests 相同，改用接上監看模組的資料庫：取消、錯誤與多段結果照舊，
    // 監看模組記下停在哪一步與讀了幾列。用 SQL 裡執行的函式按下取消，時間點固定，不靠等待。
    private const string TwoRowsCallingOnTheSecond =
        "WITH RECURSIVE r(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM r WHERE x < 2) "
        + "SELECT CASE WHEN x = 1 THEN 0 ELSE {0} END FROM r;";

    [Fact]
    public async Task Monitored_CancelArrivingAsTheFirstRowIsReady_ReportsCancellation_AndLogsTheExecuteStep()
    {
        using var env = new Env(Fast());
        using (env.Scope("corr-first-row"))
        using (env.Monitor.BeginOperation("test.boundary", CancellationToken.None))
        {
            await using var connection = (SqliteConnection)env.Open();
            using var cancellation = new CancellationTokenSource();
            connection.CreateFunction("cancel_now", () => { cancellation.Cancel(); return 1L; });
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT cancel_now();";
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => ((DbCommand)command).ExecuteReaderAsync(cancellation.Token));
            }

            Assert.Equal(1L, await ScalarAsync(connection, "SELECT CAST(1 AS BIGINT);"));
        }

        var cancelled = Assert.Single(env.Diagnostic.Snapshot(), e => e.EventName == "sql.cancelled");
        Assert.Equal("execute", cancelled.Fields["sql_step"]?.ToString());
        Assert.Equal("SELECT cancel_now();", cancelled.Fields["sql"]?.ToString());
        Assert.DoesNotContain(env.Diagnostic.Snapshot(), e => e.EventName == "sql.failed");
    }

    [Fact]
    public async Task Monitored_CancelArrivingAsALaterRowIsReady_ReportsCancellation_AndLogsTheReadStep()
    {
        using var env = new Env(Fast());
        using (env.Scope("corr-later-row"))
        using (env.Monitor.BeginOperation("test.boundary", CancellationToken.None))
        {
            await using var connection = (SqliteConnection)env.Open();
            using var cancellation = new CancellationTokenSource();
            connection.CreateFunction("cancel_now", () => { cancellation.Cancel(); return 2L; });
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = string.Format(TwoRowsCallingOnTheSecond, "cancel_now()");
                await using var reader = await ((DbCommand)command).ExecuteReaderAsync(cancellation.Token);
                Assert.True(await reader.ReadAsync(cancellation.Token));
                Assert.Equal(0L, reader.GetInt64(0));

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(cancellation.Token));
            }

            Assert.Equal(1L, await ScalarAsync(connection, "SELECT CAST(1 AS BIGINT);"));
        }

        var cancelled = Assert.Single(env.Diagnostic.Snapshot(), e => e.EventName == "sql.cancelled");
        Assert.Equal("read", cancelled.Fields["sql_step"]?.ToString());
        Assert.Equal(1L, Convert.ToInt64(cancelled.Fields["rows_read"]));
        // 第二列出來時已經要求取消，這一列不交給呼叫端，也不算讀到；之後的純量查詢不逐列讀取。
        var summary = Assert.Single(env.Diagnostic.Snapshot(), e => e.EventName == "action.summary");
        Assert.Equal(2L, Convert.ToInt64(summary.Fields["sql_statements"]));
        Assert.Equal(1L, Convert.ToInt64(summary.Fields["sql_rows_read"]));
    }

    [Fact]
    public async Task Monitored_ErrorWhileReadingALaterRow_ReachesTheCaller_AndLogsSqlFailed()
    {
        using var env = new Env(Fast());
        using (env.Scope("corr-read-error"))
        using (env.Monitor.BeginOperation("test.boundary", CancellationToken.None))
        {
            await using var connection = env.Open();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = string.Format(TwoRowsCallingOnTheSecond, "abs(-9223372036854775806 - x)");
                await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
                Assert.True(await reader.ReadAsync(CancellationToken.None));

                var error = await Assert.ThrowsAsync<SqliteException>(() => reader.ReadAsync(CancellationToken.None));
                Assert.Equal(1, error.SqliteErrorCode);
            }

            Assert.Equal(1L, await ScalarAsync(connection, "SELECT CAST(1 AS BIGINT);"));
        }

        var failed = Assert.Single(env.Diagnostic.Snapshot(), e => e.EventName == "sql.failed");
        Assert.Equal("read", failed.Fields["sql_step"]?.ToString());
        Assert.Equal(1, Convert.ToInt32(failed.Fields["sqlite_error_code"]));
        Assert.Equal(1L, Convert.ToInt64(failed.Fields["rows_read"]));
        Assert.DoesNotContain(env.Diagnostic.Snapshot(), e => e.EventName == "sql.cancelled");
    }

    [Fact]
    public async Task Monitored_SeveralResultsAndSynchronousReads_CountOneStatementAndEveryRow()
    {
        using var env = new Env(Fast());
        using (env.Scope("corr-results"))
        using (env.Monitor.BeginOperation("test.boundary", CancellationToken.None))
        {
            await using var connection = env.Open();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT CAST(1 AS BIGINT); SELECT CAST(2 AS BIGINT) UNION ALL SELECT CAST(3 AS BIGINT);";
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.True(await reader.NextResultAsync(CancellationToken.None));
            Assert.True(reader.Read());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.True(reader.Read());
            Assert.False(reader.Read());
            Assert.False(reader.NextResult());
        }

        var summary = Assert.Single(env.Diagnostic.Snapshot(), e => e.EventName == "action.summary");
        Assert.Equal(1L, Convert.ToInt64(summary.Fields["sql_statements"]));
        Assert.Equal(3L, Convert.ToInt64(summary.Fields["sql_rows_read"]));
        Assert.DoesNotContain(env.Diagnostic.Snapshot(), e => e.EventName is "sql.failed" or "sql.cancelled");
    }

    [Fact]
    public async Task Monitored_ScalarWithAnAlreadyCancelledToken_DoesNotRun_AndIsNotCounted()
    {
        using var env = new Env(Fast());
        var calls = 0;
        using (env.Scope("corr-precancelled"))
        using (env.Monitor.BeginOperation("test.boundary", CancellationToken.None))
        {
            await using var connection = (SqliteConnection)env.Open();
            connection.CreateFunction("count_call", () => ++calls);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count_call();";
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ((DbCommand)command).ExecuteScalarAsync(cancellation.Token));
        }

        Assert.Equal(0, calls);
        Assert.DoesNotContain(env.Diagnostic.Snapshot(), e => e.EventName is "sql.cancelled" or "sql.failed" or "action.summary");
    }

    [Fact]
    public async Task Monitored_ReaderObtainedDirectly_CompletesTheStatementWhenItIsHandedOut()
    {
        using var env = new Env(Fast());
        using (env.Scope("corr-direct"))
        using (env.Monitor.BeginOperation("test.boundary", CancellationToken.None))
        {
            await using var connection = (SqliteConnection)env.Open();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT CAST(7 AS BIGINT);";
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal(7L, reader.GetInt64(0));
        }

        var summary = Assert.Single(env.Diagnostic.Snapshot(), e => e.EventName == "action.summary");
        Assert.Equal(1L, Convert.ToInt64(summary.Fields["sql_statements"]));
    }

    private static async Task<object?> ScalarAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(CancellationToken.None);
    }

    [Fact]
    public async Task MonitorFailure_DoesNotChangeQueryResultsOrExceptions()
    {
        using var root = new TempProjectRoot();
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(new ThrowingLoggerProvider());
        });
        using var monitor = new OperationMonitor(factory.CreateLogger<OperationMonitor>(), Fast(stillRunningAfter: TimeSpan.FromMilliseconds(50)));
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder, monitor);
        const string projectId = "monitor-failure";
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        SupportOperationStage.Begin();
        using (monitor.BeginOperation("test.throwing-logger", CancellationToken.None))
        {
            SupportOperationStage.Enter("stage_with_throwing_logger");
            monitor.ObservePublishedEvent("export.progress", Progress("writingSheet", "#Criteria Select 1", 0, 1));
            await using var connection = database.CreateConnection(projectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = CountingSql(3_000_000);
            Assert.Equal(4_500_001_500_000L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            command.CommandText = "SELECT * FROM table_that_does_not_exist;";
            var error = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteScalarAsync());
            Assert.Equal(1, error.SqliteErrorCode);
        }
    }

    private static object Progress(string phase, string? sheetName, int sheetsCompleted, long rowsWritten) => new
    {
        reportKind = "criteriaSelectionReport",
        phase,
        sheetName,
        sheetsCompleted,
        rowsWritten,
        elapsedMilliseconds = 5L,
    };

    private sealed class Env : IDisposable
    {
        private readonly TempProjectRoot _root = new();
        private readonly ILoggerFactory _factory;
        private readonly ILogger _scopeLogger;

        public Env(OperationMonitorOptions options)
        {
            _factory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddProvider(Diagnostic);
                builder.AddProvider(Support);
            });
            _scopeLogger = _factory.CreateLogger("test");
            Monitor = new OperationMonitor(_factory.CreateLogger<OperationMonitor>(), options);
            var folder = new JetProjectFolder(_root.Path);
            Database = new SqliteProjectDatabase(folder, Monitor);
            Directory.CreateDirectory(folder.GetProjectDirectory(ProjectId));
            Database.EnsureCreatedAsync(ProjectId, CancellationToken.None).GetAwaiter().GetResult();
        }

        public RingBufferLoggerProvider Diagnostic { get; } = new(10_000);

        public SupportRingBufferLoggerProvider Support { get; } = new(2_000);

        public OperationMonitor Monitor { get; }

        public SqliteProjectDatabase Database { get; }

        public string ProjectId { get; } = "monitor-" + Guid.NewGuid().ToString("N")[..8];

        public IDisposable Scope(string correlationId) =>
            _scopeLogger.BeginScope(new Dictionary<string, object?> { ["correlation_id"] = correlationId })!;

        public DbConnection Open()
        {
            var connection = Database.CreateConnection(ProjectId);
            connection.Open();
            return connection;
        }

        /// <summary>等到診斷日誌出現這次操作的某種紀錄；計時器與執行計畫在背景執行緒寫，最多等 30 秒。</summary>
        public async Task<DiagnosticLogEntry> WaitForAsync(string eventName, string correlationId)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (Diagnostic.Snapshot().FirstOrDefault(e => e.EventName == eventName && e.CorrelationId == correlationId) is { } entry)
                {
                    return entry;
                }

                await Task.Delay(20);
            }

            throw new TimeoutException($"No {eventName} entry for {correlationId} within 30 seconds.");
        }

        public void Dispose()
        {
            Monitor.Dispose();
            _factory.Dispose();
            _root.Dispose();
        }
    }

    private sealed class ThrowingLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ThrowingLogger();

        public void Dispose()
        {
        }

        private sealed class ThrowingLogger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
                throw new InvalidOperationException("synthetic logger failure");

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                throw new InvalidOperationException("synthetic logger failure");
        }
    }
}
