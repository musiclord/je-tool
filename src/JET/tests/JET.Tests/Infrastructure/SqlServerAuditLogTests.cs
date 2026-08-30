using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 控制面第四輪 §4 <c>dbo.audit_log</c> 治理留痕驗收。全部 [SqlServerFact] 閘控（連 JET_Test）。
/// oracle：同交易留痕規格——create/delete 各留一列且 login_name/host_name 非空（伺服器端 SUSER_SNAME()/HOST_NAME()）；
/// 建案失敗（撞 PK）整筆回滾 → <b>不</b>留 audit 列（證明 audit 併入敏感動作的同一交易）。
/// 隔離:每測試唯一 projectId，故 audit_log 依 project_id 過濾互不干擾。
/// </summary>
public sealed class SqlServerAuditLogTests
{
    private const string SingleDb = "JET_Test";

    private static ProjectDocument Doc(string projectId) =>
        new(projectId, "AUD", "留痕測試", "op", "2024-01-01", "2024-12-31", null,
            ProjectDocument.DefaultMoneyScale, ProjectDocument.DefaultRoundingMode,
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            CurrentStep: 1, ProjectDocument.CurrentSchemaVersion, ProjectDocument.SqlServerDatabaseProvider);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [SqlServerFact]
    public async Task Create_LeavesOneAuditRow_WithNonNullIdentity()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var options = new SqlServerConnectionOptions(conn, SingleDb);
        var registry = new SqlServerProjectRegistry(options);
        var projectId = Unique("建案留痕");

        try
        {
            await registry.RegisterAsync(Doc(projectId), Unique("user"), CancellationToken.None);

            Assert.Equal(1, await AuditCountAsync(conn, projectId, "project.create"));
            Assert.Equal(1, await AuditCountWithIdentityAsync(conn, projectId, "project.create"));
        }
        finally
        {
            await registry.UnregisterAsync(projectId, CancellationToken.None);
        }
    }

    [SqlServerFact]
    public async Task FailedCreate_LeavesNoAdditionalAuditRow_SameTransaction()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var options = new SqlServerConnectionOptions(conn, SingleDb);
        var registry = new SqlServerProjectRegistry(options);
        var projectId = Unique("重複建案");

        try
        {
            await registry.RegisterAsync(Doc(projectId), Unique("first"), CancellationToken.None);

            // 第二次同 project_id → registry PK 衝突 → 整筆交易回滾（含 audit）。
            await Assert.ThrowsAnyAsync<Exception>(
                () => registry.RegisterAsync(Doc(projectId), Unique("second"), CancellationToken.None));

            // 仍恰為一列 create 留痕（失敗那次的 audit 隨交易回滾、未新增）。
            Assert.Equal(1, await AuditCountAsync(conn, projectId, "project.create"));
        }
        finally
        {
            await registry.UnregisterAsync(projectId, CancellationToken.None);
        }
    }

    [SqlServerFact]
    public async Task Delete_LeavesOneAuditRow_WithNonNullIdentity()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var options = new SqlServerConnectionOptions(conn, SingleDb);
        var database = new SqlServerProjectDatabase(options);
        var registry = new SqlServerProjectRegistry(options);
        var projectId = Unique("刪案留痕");

        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await registry.RegisterAsync(Doc(projectId), Unique("user"), CancellationToken.None);

        await database.DeleteAsync(projectId, CancellationToken.None);

        // 刪案後 registry 列已清,但 audit 列留存（留痕不隨 project 消失）。
        Assert.Equal(1, await AuditCountAsync(conn, projectId, "project.delete"));
        Assert.Equal(1, await AuditCountWithIdentityAsync(conn, projectId, "project.delete"));
    }

    private static string SingleDbConnectionString(string baseConnectionString) =>
        new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = SingleDb }.ConnectionString;

    private static async Task<long> AuditCountAsync(string baseConnectionString, string projectId, string action)
    {
        await using var connection = new SqlConnection(SingleDbConnectionString(baseConnectionString));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.audit_log WHERE project_id = @id AND action = @action;";
        command.Parameters.AddWithValue("@id", projectId);
        command.Parameters.AddWithValue("@action", action);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> AuditCountWithIdentityAsync(
        string baseConnectionString, string projectId, string action)
    {
        await using var connection = new SqlConnection(SingleDbConnectionString(baseConnectionString));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // login_name/host_name 由 SUSER_SNAME()/HOST_NAME() 供值,必為非空。
        command.CommandText =
            """
            SELECT COUNT(*) FROM dbo.audit_log
            WHERE project_id = @id AND action = @action
              AND login_name IS NOT NULL AND LEN(login_name) > 0
              AND host_name  IS NOT NULL AND LEN(host_name)  > 0;
            """;
        command.Parameters.AddWithValue("@id", projectId);
        command.Parameters.AddWithValue("@action", action);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
