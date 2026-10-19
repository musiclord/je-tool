using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using JET.Application;
using JET.Bridge;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 執行中的長語句在取消後要很快停下，三種資料庫在畫面上都回「作業已取消」（使用者 2026-10-19 對 SQLite 取消選「這輪一起修 (Recommended)」）。
/// 2026-10-05 最後獨立複審實測：SQLite 帶取消的長語句仍跑完 54.7 秒。這裡用真正的長語句，200 毫秒後取消，
/// 要求 10 秒內停下；交易內被中斷的寫入不得留下資料，同一條連線之後照常可用。
/// 修正前第一次失敗見收據 20261019-053544183-355616838b7e4e6aaab0832e02473f2f：SQLite 三種執行方式都等到
/// 測試自己的 30 秒中斷才停，丟出 SqliteException；同日 SQL Server 半秒內停下，但丟出 SqlException，
/// 經 bridge 會變成非預期錯誤。
/// </summary>
public sealed class LongStatementCancellationProviderTests
{
    /// <summary>十億層遞迴，不取消要跑很久；SQLite 與 DuckDB 共用同一段 SQL。</summary>
    private const string LocalLongCount =
        "SELECT COUNT(*) FROM (WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 1000000000) SELECT x FROM c) AS t";

    /// <summary>SQL Server 用等待四十秒代替長計算；取消時伺服器中止整批。</summary>
    private const string SqlServerLongWait = "WAITFOR DELAY '00:00:40'";

    private const string SqlServerLongRead = SqlServerLongWait + "; SELECT CAST(1 AS BIGINT);";

    private static readonly TimeSpan CancelAfter = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan StopWithin = TimeSpan.FromSeconds(10);

    /// <summary>產品的取消沒有生效時，由測試自己在 30 秒後中斷，避免長語句一直跑下去。</summary>
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(30);

    private static readonly Action<DbConnection, DbCommand> InterruptSqliteDirectly =
        static (connection, _) => SQLitePCL.raw.sqlite3_interrupt(((SqliteConnection)connection).Handle);

    private static readonly Action<DbConnection, DbCommand> CancelCommand = static (_, command) => command.Cancel();

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider, "scalar")]
    [InlineData(ProjectDocument.DefaultDatabaseProvider, "reader")]
    [InlineData(ProjectDocument.DefaultDatabaseProvider, "write")]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider, "scalar")]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider, "reader")]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider, "write")]
    public async Task LongStatement_StopsSoonAfterCancel_AndConnectionStaysUsable_LocalProviders(
        string provider, string mode)
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = CreateLocalProject(root, provider);

        await AssertCancelsAsync(
            () => database.CreateConnection(projectId),
            mode,
            longRead: LocalLongCount,
            probeTable: "cancel_probe",
            longWrite: probe => $"INSERT INTO {probe} (n) VALUES (0); INSERT INTO {probe} (n) {LocalLongCount}",
            InterruptFor(provider),
            expectCancellationException: true);
    }

    /// <summary>SQL Server 的驅動丟出自己的例外，轉成取消在 dispatcher 出口做，見下方經 dispatcher 的測試。</summary>
    [SqlServerTheory]
    [InlineData("scalar")]
    [InlineData("reader")]
    [InlineData("write")]
    public async Task LongStatement_StopsSoonAfterCancel_AndConnectionStaysUsable_SqlServer(string mode)
    {
        await using var project = Assert.IsType<TempSqlServerProject>(
            await TempSqlServerProject.TryCreateAsync());

        await AssertCancelsAsync(
            () => project.Database.CreateConnection(project.ProjectId),
            mode,
            longRead: SqlServerLongRead,
            probeTable: SqlServerProjectSchema.QualifierFor(project.ProjectId) + "cancel_probe",
            longWrite: probe => $"INSERT INTO {probe} (n) VALUES (0); {SqlServerLongWait}; INSERT INTO {probe} (n) VALUES (2);",
            CancelCommand,
            expectCancellationException: false);
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task CanceledLongStatement_ReachesScreenAsOperationCancelled_LocalProviders(string provider)
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = CreateLocalProject(root, provider);

        await AssertDispatcherReportsCancelledAsync(
            () => database.CreateConnection(projectId), LocalLongCount, InterruptFor(provider));
    }

    [SqlServerFact]
    public async Task CanceledLongStatement_ReachesScreenAsOperationCancelled_SqlServer()
    {
        await using var project = Assert.IsType<TempSqlServerProject>(
            await TempSqlServerProject.TryCreateAsync());

        await AssertDispatcherReportsCancelledAsync(
            () => project.Database.CreateConnection(project.ProjectId), SqlServerLongRead, CancelCommand);
    }

    private static (ILocalProjectDatabase Database, string ProjectId) CreateLocalProject(
        TempProjectRoot root, string provider)
    {
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        return (database, projectId);
    }

    private static Action<DbConnection, DbCommand> InterruptFor(string provider) =>
        provider == ProjectDocument.DuckDbDatabaseProvider ? CancelCommand : InterruptSqliteDirectly;

    private static async Task AssertCancelsAsync(
        Func<DbConnection> createConnection,
        string mode,
        string longRead,
        string probeTable,
        Func<string, string> longWrite,
        Action<DbConnection, DbCommand> interruptDirectly,
        bool expectCancellationException)
    {
        await using var connection = createConnection();
        await connection.OpenAsync(CancellationToken.None);

        if (mode != "write")
        {
            await using var command = connection.CreateCommand();
            command.CommandText = longRead;
            var exception = await RunUntilCanceledAsync(connection, command, interruptDirectly, async token =>
            {
                if (mode == "scalar")
                {
                    await command.ExecuteScalarAsync(token);
                    return;
                }

                await using var reader = await command.ExecuteReaderAsync(token);
                await reader.ReadAsync(token);
            });
            AssertCancellationException(exception, expectCancellationException);
        }
        else
        {
            await ExecuteAsync(connection, $"CREATE TABLE {probeTable} (n BIGINT)");
            await using (var transaction = await connection.BeginTransactionAsync(CancellationToken.None))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = longWrite(probeTable);
                var exception = await RunUntilCanceledAsync(connection, command, interruptDirectly,
                    token => command.ExecuteNonQueryAsync(token));
                AssertCancellationException(exception, expectCancellationException);
            }

            // 交易內已寫入的第一列也要隨取消回復。
            Assert.Equal(0L, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {probeTable}"));
            await ExecuteAsync(connection, $"INSERT INTO {probeTable} (n) VALUES (1)");
            Assert.Equal(1L, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {probeTable}"));
        }

        // 取消不會留在連線上影響之後的語句。
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT CAST(1 AS BIGINT)"));
    }

    private static async Task AssertDispatcherReportsCancelledAsync(
        Func<DbConnection> createConnection,
        string longRead,
        Action<DbConnection, DbCommand> interruptDirectly)
    {
        // 與 AppCompositionRoot 相同的引擎錯誤映射。
        var dispatcher = new ActionDispatcher(
            [new LongStatementHandler(createConnection, longRead, interruptDirectly)],
            NullLogger<ActionDispatcher>.Instance,
            new ProjectSession(),
            exception => LocalEngineErrors.TryTranslate(exception) ?? SqlServerEngineErrors.TryTranslate(exception));
        using var payload = JsonDocument.Parse("{}");
        using var cancellation = new CancellationTokenSource(CancelAfter);

        var stopwatch = Stopwatch.StartNew();
        var exception = await Record.ExceptionAsync(() =>
            dispatcher.DispatchAsync(LongStatementHandler.ActionName, payload.RootElement, cancellation.Token));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < StopWithin,
            $"取消後 {stopwatch.Elapsed.TotalSeconds:0.0} 秒才停下；例外：{exception?.GetType().Name}");
        Assert.NotNull(exception);
        Assert.Equal(JetErrorCodes.OperationCancelled, JetWebMessageBridge.ToErrorDto(exception).Code);
    }

    private static async Task<Exception?> RunUntilCanceledAsync(
        DbConnection connection,
        DbCommand command,
        Action<DbConnection, DbCommand> interruptDirectly,
        Func<CancellationToken, Task> execute)
    {
        using var cancellation = new CancellationTokenSource(CancelAfter);
        using var safetyNet = new CancellationTokenSource(SafetyNet);
        var safetyRegistration = safetyNet.Token.Register(() => interruptDirectly(connection, command));
        var stopwatch = Stopwatch.StartNew();
        Exception? exception;
        try
        {
            exception = await Record.ExceptionAsync(() => execute(cancellation.Token));
        }
        finally
        {
            stopwatch.Stop();
            // 等安全網的回呼結束，之後的語句不會再被它中斷。
            safetyRegistration.Dispose();
        }

        Assert.True(stopwatch.Elapsed < StopWithin,
            $"取消後 {stopwatch.Elapsed.TotalSeconds:0.0} 秒才停下；例外：{exception?.GetType().Name}");
        return exception;
    }

    private static void AssertCancellationException(Exception? exception, bool expectCancellationException)
    {
        Assert.NotNull(exception);
        if (expectCancellationException)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(exception);
        }
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<long> ScalarAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    /// <summary>用 action 的取消權杖跑一條長語句；產品取消失效時同樣由安全網在 30 秒後中斷。</summary>
    private sealed class LongStatementHandler(
        Func<DbConnection> createConnection,
        string sql,
        Action<DbConnection, DbCommand> interruptDirectly) : IApplicationActionHandler
    {
        public const string ActionName = "__test.longStatement";

        public string Action => ActionName;

        public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
        {
            await using var connection = createConnection();
            await connection.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var safetyNet = new CancellationTokenSource(SafetyNet);
            using var safetyRegistration = safetyNet.Token.Register(() => interruptDirectly(connection, command));
            return await command.ExecuteScalarAsync(cancellationToken);
        }
    }
}
