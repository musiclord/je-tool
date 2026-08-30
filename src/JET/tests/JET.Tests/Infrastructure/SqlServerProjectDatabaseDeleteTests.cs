using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 控制面第四輪 §2 原子刪案驗收（<see cref="SqlServerProjectDatabase.DeleteAsync"/>）。
/// 全部 [SqlServerFact] 閘控（連 JET_Test）。單庫是 sqlServer 專案的唯一管家:單一交易內 drop schema →
/// 寫 audit → 刪 access/registry，四步全成或全回滾。
/// oracle：交易原子性——正常刪案後 schema／registry／access 三者皆消失；中間態注入例外 → 整筆 rollback、無半刪。
/// </summary>
public sealed class SqlServerProjectDatabaseDeleteTests
{
    private const string SingleDb = "JET_Test";

    private static ProjectDocument Doc(string projectId) =>
        new(projectId, "DEL", "刪案測試", "op", "2024-01-01", "2024-12-31", null,
            ProjectDocument.DefaultMoneyScale, ProjectDocument.DefaultRoundingMode,
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            CurrentStep: 1, ProjectDocument.CurrentSchemaVersion, ProjectDocument.SqlServerDatabaseProvider);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [SqlServerFact]
    public async Task Delete_HappyPath_RemovesSchemaRegistryAndAccess()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var options = new SqlServerConnectionOptions(conn, SingleDb);
        var database = new SqlServerProjectDatabase(options);
        var registry = new SqlServerProjectRegistry(options);
        var projectId = Unique("待原子刪");
        var principal = Unique("user");
        var schema = SqlServerProjectSchema.For(projectId);

        await database.EnsureCreatedAsync(projectId, CancellationToken.None);   // schema
        await registry.RegisterAsync(Doc(projectId), principal, CancellationToken.None); // registry + access

        // 前置成立：schema 在、registry 在、access 一列。
        Assert.Equal(1, await SchemaExistsAsync(conn, schema));
        Assert.True(await registry.ExistsAsync(projectId, CancellationToken.None));
        Assert.Equal(1, await AccessCountAsync(conn, projectId));

        await database.DeleteAsync(projectId, CancellationToken.None);

        // 三者皆消失（單一交易清乾淨）。
        Assert.Equal(0, await SchemaExistsAsync(conn, schema));
        Assert.False(await registry.ExistsAsync(projectId, CancellationToken.None));
        Assert.Equal(0, await AccessCountAsync(conn, projectId));
    }

    [SqlServerFact]
    public async Task Delete_FaultAfterDropSchema_RollsBackEverything()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var options = new SqlServerConnectionOptions(conn, SingleDb);
        var database = new SqlServerProjectDatabase(options);
        var registry = new SqlServerProjectRegistry(options);
        var projectId = Unique("刪到一半");
        var principal = Unique("user");
        var schema = SqlServerProjectSchema.For(projectId);

        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await registry.RegisterAsync(Doc(projectId), principal, CancellationToken.None);

        try
        {
            // 注入中間態例外：DROP SCHEMA 之後、刪 registry 之前拋 → 整筆交易應 rollback。
            database.DeleteFaultHookForTests = _ =>
                throw new InvalidOperationException("injected mid-transaction fault");

            await Assert.ThrowsAnyAsync<Exception>(
                () => database.DeleteAsync(projectId, CancellationToken.None));

            // rollback 後一切復原：schema 仍在（transactional DDL）、registry 仍在、access 仍一列——無半刪。
            Assert.Equal(1, await SchemaExistsAsync(conn, schema));
            Assert.True(await registry.ExistsAsync(projectId, CancellationToken.None));
            Assert.Equal(1, await AccessCountAsync(conn, projectId));
        }
        finally
        {
            // 清鉤子後正常刪案兜底清理。
            database.DeleteFaultHookForTests = null;
            await database.DeleteAsync(projectId, CancellationToken.None);
        }
    }

    private static string SingleDbConnectionString(string baseConnectionString) =>
        new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = SingleDb }.ConnectionString;

    private static async Task<long> SchemaExistsAsync(string baseConnectionString, string schema)
    {
        await using var connection = new SqlConnection(SingleDbConnectionString(baseConnectionString));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN SCHEMA_ID(@s) IS NULL THEN 0 ELSE 1 END;";
        command.Parameters.AddWithValue("@s", schema);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> AccessCountAsync(string baseConnectionString, string projectId)
    {
        await using var connection = new SqlConnection(SingleDbConnectionString(baseConnectionString));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.project_access WHERE project_id = @id;";
        command.Parameters.AddWithValue("@id", projectId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
