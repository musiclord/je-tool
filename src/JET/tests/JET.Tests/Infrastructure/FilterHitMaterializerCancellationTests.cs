using System.Data.Common;
using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 本地 filter 命中 replace-all 的 commit 邊界：清除舊集合後收到取消，SQLite／DuckDB 都必須 rollback，
/// 保留完整舊集合。取消由真 SQL 完成事件觸發，避免用時間競速製造 flaky 測試。
/// </summary>
public sealed class FilterHitMaterializerCancellationTests
{
    [Fact]
    public async Task MaterializeAsync_CancelledAfterClear_RollsBackOriginalHitSet()
    {
        // State Transition：舊命中 → transaction 內 DELETE → 取消 → rollback 回舊命中（值與身分都不變）。
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await ExecuteAsync(
            database.CreateConnection(projectId),
            """
            INSERT INTO filter_hit (scenario_id, entry_id) VALUES (9, 101);
            UPDATE result_stale_state SET filter_stale = 1 WHERE singleton = 1;
            """);

        using var cancellation = new CancellationTokenSource();
        var materializer = new LocalFilterHitMaterializer(
            database,
            new CancelAfterClearLogger(cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => materializer.MaterializeAsync(
            projectId,
            [],
            new FilterConditionContext(
                ProjectDocument.DefaultMoneyScale,
                PrepStartDate: null,
                PeriodStart: "2025-01-01",
                PeriodEnd: "2025-12-31",
                PopulationScope: GlPopulationScope.AuditPeriod),
            cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1L, await ScalarAsync(
            database.CreateConnection(projectId),
            "SELECT COUNT(*) FROM filter_hit WHERE scenario_id = 9 AND entry_id = 101;"));
        Assert.Equal(1L, await ScalarAsync(
            database.CreateConnection(projectId),
            "SELECT filter_stale FROM result_stale_state WHERE singleton = 1;"));
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task MaterializeSelected_CancelledAfterScopedClear_PreservesSelectedAndUnselectedHits()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await ExecuteAsync(database.CreateConnection(projectId), """
            INSERT INTO filter_hit (scenario_id, entry_id) VALUES (1,101),(2,202);
            UPDATE result_stale_state SET filter_stale=1 WHERE singleton=1;
            """);
        using var definition = JsonDocument.Parse("""
            {"name":"所選情境","groups":[{"conditions":[{"type":"drCrOnly","drCr":"debit"}]}]}
            """);
        var spec = FilterScenarioPayloadParser.Parse(definition.RootElement, ProjectDocument.DefaultMoneyScale);
        using var cancellation = new CancellationTokenSource();
        var materializer = new LocalFilterHitMaterializer(database,
            new CancelAfterClearLogger(cancellation, "DELETE FROM filter_hit WHERE"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => materializer.MaterializeAsync(projectId,
            [new MaterializableScenario(1, spec)],
            new FilterConditionContext(ProjectDocument.DefaultMoneyScale, null, "2025-01-01", "2025-12-31",
                PopulationScope: GlPopulationScope.AuditPeriod), cancellation.Token, replaceAll: false));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, await ScalarAsync(database.CreateConnection(projectId),
            "SELECT COUNT(*) FROM filter_hit WHERE scenario_id=1 AND entry_id=101;"));
        Assert.Equal(1, await ScalarAsync(database.CreateConnection(projectId),
            "SELECT COUNT(*) FROM filter_hit WHERE scenario_id=2 AND entry_id=202;"));
        Assert.Equal(1, await ScalarAsync(database.CreateConnection(projectId),
            "SELECT filter_stale FROM result_stale_state WHERE singleton=1;"));
    }

    private static async Task<long> ScalarAsync(DbConnection connection, string sql)
    {
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
    }

    private sealed class CancelAfterClearLogger(CancellationTokenSource cancellation,
        string clearSqlFragment = "DELETE FROM filter_hit;")
        : ILogger<LocalFilterHitMaterializer>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 2000
                && formatter(state, exception).Contains(
                    clearSqlFragment,
                    StringComparison.Ordinal))
            {
                cancellation.Cancel();
            }
        }
    }
}
