using System.Data.Common;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 篩選情境 replace-all 的混版防護。規格 oracle：情境定義換版時，舊的
/// result_filter_run 必須在同一交易清除；若新情境寫入失敗，兩張表都必須回復舊版。
/// </summary>
public sealed class FilterScenarioStoreTests
{
    private static readonly DateTimeOffset FixedUtc =
        new(2026, 7, 10, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public Task ReplaceAll_Sqlite_ClearsPreviousFilterHits() =>
        ReplaceAllClearsPreviousFilterHitsAsync(
            static folder => new SqliteProjectDatabase(folder));

    [Fact]
    public Task ReplaceAll_DuckDb_ClearsPreviousFilterHits() =>
        ReplaceAllClearsPreviousFilterHitsAsync(
            static folder => new DuckDbProjectDatabase(folder));

    [Fact]
    public Task ReplaceAll_Sqlite_InsertFailureRollsBackDefinitionsAndHits() =>
        InsertFailureRollsBackDefinitionsAndHitsAsync(
            static folder => new SqliteProjectDatabase(folder));

    [Fact]
    public Task ReplaceAll_DuckDb_InsertFailureRollsBackDefinitionsAndHits() =>
        InsertFailureRollsBackDefinitionsAndHitsAsync(
            static folder => new DuckDbProjectDatabase(folder));

    [SqlServerFact]
    public async Task ReplaceAll_SqlServer_ClearsPreviousFilterHits()
    {
        var sql = Assert.IsType<TempSqlServerProject>(await TempSqlServerProject.TryCreateAsync());
        await using var cleanup = sql;
        var store = new SqlServerFilterScenarioStore(sql.Database);
        await store.ReplaceAllAsync(
            sql.ProjectId, [Scenario(1, "舊情境")], CancellationToken.None);
        await SeedHitAsync(
            sql.Database.CreateConnection(sql.ProjectId),
            $"{SqlServerProjectSchema.QualifierFor(sql.ProjectId)}result_filter_run");

        await store.ReplaceAllAsync(
            sql.ProjectId, [Scenario(1, "新情境")], CancellationToken.None);

        Assert.Equal(0, await ScalarAsync(
            sql.Database.CreateConnection(sql.ProjectId),
            $"SELECT COUNT(*) FROM {SqlServerProjectSchema.QualifierFor(sql.ProjectId)}result_filter_run;"));
        var current = Assert.Single(await store.ListAsync(sql.ProjectId, CancellationToken.None));
        Assert.Equal("新情境", current.Name);
    }

    [SqlServerFact]
    public async Task ReplaceAll_SqlServer_InsertFailureRollsBackDefinitionsAndHits()
    {
        var sql = Assert.IsType<TempSqlServerProject>(await TempSqlServerProject.TryCreateAsync());
        await using var cleanup = sql;
        var store = new SqlServerFilterScenarioStore(sql.Database);
        await store.ReplaceAllAsync(
            sql.ProjectId, [Scenario(1, "舊情境")], CancellationToken.None);
        await SeedHitAsync(
            sql.Database.CreateConnection(sql.ProjectId),
            $"{SqlServerProjectSchema.QualifierFor(sql.ProjectId)}result_filter_run");

        await Assert.ThrowsAsync<SqlException>(() => store.ReplaceAllAsync(
            sql.ProjectId,
            [Scenario(1, "新情境一"), Scenario(1, "新情境二")],
            CancellationToken.None));

        var current = Assert.Single(await store.ListAsync(sql.ProjectId, CancellationToken.None));
        Assert.Equal("舊情境", current.Name);
        Assert.Equal(1, await ScalarAsync(
            sql.Database.CreateConnection(sql.ProjectId),
            $"SELECT COUNT(*) FROM {SqlServerProjectSchema.QualifierFor(sql.ProjectId)}result_filter_run;"));
    }

    private static async Task ReplaceAllClearsPreviousFilterHitsAsync(
        Func<JetProjectFolder, ILocalProjectDatabase> databaseFactory)
    {
        // 狀態轉換：舊定義＋舊命中 → replace-all → 新定義＋空命中。
        using var root = new TempProjectRoot();
        var projectId = Guid.NewGuid().ToString("N");
        var database = databaseFactory(new JetProjectFolder(root.Path));
        var store = new LocalFilterScenarioStore(database);
        await store.ReplaceAllAsync(
            projectId, [Scenario(1, "舊情境")], CancellationToken.None);
        await SeedHitAsync(database.CreateConnection(projectId), "result_filter_run");

        await store.ReplaceAllAsync(
            projectId, [Scenario(1, "新情境")], CancellationToken.None);

        Assert.Equal(0, await ScalarAsync(
            database.CreateConnection(projectId),
            "SELECT COUNT(*) FROM result_filter_run;"));
        var current = Assert.Single(await store.ListAsync(projectId, CancellationToken.None));
        Assert.Equal("新情境", current.Name);
    }

    private static async Task InsertFailureRollsBackDefinitionsAndHitsAsync(
        Func<JetProjectFolder, ILocalProjectDatabase> databaseFactory)
    {
        // 原子性 oracle：重複 position 使第二筆 INSERT 失敗，兩張表都必須回復交易前狀態。
        using var root = new TempProjectRoot();
        var projectId = Guid.NewGuid().ToString("N");
        var database = databaseFactory(new JetProjectFolder(root.Path));
        var store = new LocalFilterScenarioStore(database);
        await store.ReplaceAllAsync(
            projectId, [Scenario(1, "舊情境")], CancellationToken.None);
        await SeedHitAsync(database.CreateConnection(projectId), "result_filter_run");

        await Assert.ThrowsAnyAsync<Exception>(() => store.ReplaceAllAsync(
            projectId,
            [Scenario(1, "新情境一"), Scenario(1, "新情境二")],
            CancellationToken.None));

        var current = Assert.Single(await store.ListAsync(projectId, CancellationToken.None));
        Assert.Equal("舊情境", current.Name);
        Assert.Equal(1, await ScalarAsync(
            database.CreateConnection(projectId),
            "SELECT COUNT(*) FROM result_filter_run;"));
    }

    private static SavedFilterScenario Scenario(int position, string name) =>
        new(position, name, "測試動機", "{}", FixedUtc);

    private static async Task SeedHitAsync(DbConnection connection, string table)
    {
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"INSERT INTO {table} (scenario_position, entry_id) VALUES (1, 101);";
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<long> ScalarAsync(DbConnection connection, string sql)
    {
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var result = await command.ExecuteScalarAsync();
            return result is null or DBNull ? 0 : Convert.ToInt64(result);
        }
    }
}
