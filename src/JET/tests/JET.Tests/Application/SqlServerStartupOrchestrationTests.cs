using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// SQL startup orchestration 的純單元特徵鎖。決策表 oracle：
/// probe 後取消＝零 log／零 ensure；probe 成功＝info 後 ensure；probe 失敗＝warning 但仍 ensure；
/// ensure 取消＝靜默結束；ensure 完成後取消＝不寫 ready；ensure 關閉＝零 ensure；
/// ensure 非取消例外＝warning 且不向外拋。
/// </summary>
public sealed class SqlServerStartupOrchestrationTests
{
    [Fact]
    public async Task StartupWork_ProbeReturnsOrdinaryResultAfterCancellation_DoesNotLogOrEnsure()
    {
        using var logs = new RingBufferLoggerProvider(capacity: 10);
        using var cancellation = new CancellationTokenSource();
        var ensureCalls = 0;
        var work = CreateWork(
            logs,
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(new HealthResult(true, "healthy"));
            },
            _ =>
            {
                ensureCalls++;
                return Task.CompletedTask;
            });

        await work(cancellation.Token);

        Assert.Empty(logs.Snapshot());
        Assert.Equal(0, ensureCalls);
    }

    [Fact]
    public async Task StartupWork_ProbeSucceeds_LogsHealthAndEnsuresDatabaseOnce()
    {
        using var logs = new RingBufferLoggerProvider(capacity: 10);
        var ensureCalls = 0;
        var work = CreateWork(
            logs,
            (_, _) => Task.FromResult(new HealthResult(true, "healthy")),
            _ =>
            {
                ensureCalls++;
                return Task.CompletedTask;
            });

        await work(CancellationToken.None);

        var entries = logs.Snapshot();
        Assert.Equal(1, ensureCalls);
        Assert.Collection(
            entries,
            health =>
            {
                Assert.Equal("Information", health.Level);
                Assert.Equal("healthy", health.Message);
            },
            ready =>
            {
                Assert.Equal("Information", ready.Level);
                Assert.Equal("SQL Server 單一資料庫 JET 已就緒（不存在則已建立）。", ready.Message);
            });
    }

    [Fact]
    public async Task StartupWork_ProbeFails_LogsWarningAndContinuesToEnsure()
    {
        using var logs = new RingBufferLoggerProvider(capacity: 10);
        var ensureCalls = 0;
        var work = CreateWork(
            logs,
            (_, _) => Task.FromResult(new HealthResult(false, "unhealthy")),
            _ =>
            {
                ensureCalls++;
                return Task.CompletedTask;
            });

        await work(CancellationToken.None);

        var entries = logs.Snapshot();
        Assert.Equal(1, ensureCalls);
        Assert.Collection(
            entries,
            health =>
            {
                Assert.Equal("Warning", health.Level);
                Assert.Equal("unhealthy", health.Message);
            },
            ready =>
            {
                Assert.Equal("Information", ready.Level);
                Assert.Equal("SQL Server 單一資料庫 JET 已就緒（不存在則已建立）。", ready.Message);
            });
    }

    [Fact]
    public async Task StartupWork_EnsureIsCancelled_SwallowsCancellationWithoutFailureLog()
    {
        using var logs = new RingBufferLoggerProvider(capacity: 10);
        using var cancellation = new CancellationTokenSource();
        var work = CreateWork(
            logs,
            (_, _) => Task.FromResult(new HealthResult(true, "healthy")),
            _ =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });

        await work(cancellation.Token);

        var entry = Assert.Single(logs.Snapshot());
        Assert.Equal("Information", entry.Level);
        Assert.Equal("healthy", entry.Message);
    }

    [Fact]
    public async Task StartupWork_EnsureThrows_LogsWarningWithoutThrowing()
    {
        using var logs = new RingBufferLoggerProvider(capacity: 10);
        var work = CreateWork(
            logs,
            (_, _) => Task.FromResult(new HealthResult(true, "healthy")),
            _ => Task.FromException(new InvalidOperationException("ensure failed")));

        var exception = await Record.ExceptionAsync(() => work(CancellationToken.None));

        Assert.Null(exception);
        Assert.Collection(
            logs.Snapshot(),
            health => Assert.Equal("Information", health.Level),
            failure =>
            {
                Assert.Equal("Warning", failure.Level);
                Assert.Equal(
                    "啟動時確保 SQL Server 單一資料庫失敗（非致命，稍後建案時再試）。",
                    failure.Message);
            });
    }

    [Fact]
    public async Task StartupWork_EnsureCompletesAfterCancellation_DoesNotLogReadyOrWarning()
    {
        using var logs = new RingBufferLoggerProvider(capacity: 10);
        using var cancellation = new CancellationTokenSource();
        var ensureCalls = 0;
        var work = CreateWork(
            logs,
            (_, _) => Task.FromResult(new HealthResult(true, "healthy")),
            _ =>
            {
                ensureCalls++;
                cancellation.Cancel();
                return Task.CompletedTask;
            });

        await work(cancellation.Token);

        Assert.Equal(1, ensureCalls);
        var entry = Assert.Single(logs.Snapshot());
        Assert.Equal("Information", entry.Level);
        Assert.Equal("healthy", entry.Message);
    }

    [Fact]
    public async Task StartupWork_EnsureIsDisabled_LogsHealthWithoutEnsuringDatabase()
    {
        const string expectedConnectionString = "Server=example.invalid;Database=master;Integrated Security=True;";
        using var logs = new RingBufferLoggerProvider(capacity: 10);
        string? actualConnectionString = null;
        var ensureCalls = 0;
        var work = CreateWork(
            logs,
            (connectionString, _) =>
            {
                actualConnectionString = connectionString;
                return Task.FromResult(new HealthResult(true, "healthy"));
            },
            _ =>
            {
                ensureCalls++;
                return Task.CompletedTask;
            },
            ensureDatabaseOnStartup: false);

        await work(CancellationToken.None);

        Assert.Equal(expectedConnectionString, actualConnectionString);
        Assert.Equal(0, ensureCalls);
        var entry = Assert.Single(logs.Snapshot());
        Assert.Equal("Information", entry.Level);
        Assert.Equal("healthy", entry.Message);
    }

    private static Func<CancellationToken, Task> CreateWork(
        RingBufferLoggerProvider logs,
        Func<string, CancellationToken, Task<HealthResult>> probeAsync,
        Func<CancellationToken, Task> ensureDatabaseReadyAsync,
        bool ensureDatabaseOnStartup = true) =>
        SqlServerStartupOrchestration.Create(
            probeConnectionString: "Server=example.invalid;Database=master;Integrated Security=True;",
            singleDatabaseName: "JET",
            ensureDatabaseOnStartup: ensureDatabaseOnStartup,
            logger: logs.CreateLogger("SqlServerStartupOrchestrationTests"),
            probeAsync: probeAsync,
            ensureDatabaseReadyAsync: ensureDatabaseReadyAsync);
}
