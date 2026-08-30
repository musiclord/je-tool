using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 控制面第四輪 §5 <c>dev.db.reconcile</c> 三方對帳驗收（manifest Dev Actions）。全部 [SqlServerFact] 閘控（連 JET_Test）。
/// oracle：對帳規格——三種漂移各以獨立 fixture 造出後,回應對應清單須「含」該筆（共用 JET_Test 下用 Contains,
/// 不斷言完整集合;zombieFolders 為 host 本地資料夾,故該筆可精確驗）。
/// </summary>
public sealed class DevDbReconcileHandlerTests
{
    private const string SingleDb = "JET_Test";

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [SqlServerFact]
    public async Task Reconcile_OrphanSchema_ListedInOrphanSchemas()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        using var host = new HandlerTestHost(sqlServerConnectionString: conn);
        var orphanSchema = $"prj_orphan_{Guid.NewGuid():N}"[..24].ToLowerInvariant();

        try
        {
            // 造孤兒:建一個 prj_ schema 但不登記 registry。
            await ExecuteAsync(conn, $"IF SCHEMA_ID(@s) IS NULL EXEC('CREATE SCHEMA [{orphanSchema}]');", ("@s", orphanSchema));

            var data = await host.DispatchAsync("dev.db.reconcile");

            var orphans = data.GetProperty("orphanSchemas").EnumerateArray()
                .Select(e => e.GetString()).ToList();
            Assert.Contains(orphanSchema, orphans);
        }
        finally
        {
            await ExecuteAsync(conn, $"IF SCHEMA_ID(@s) IS NOT NULL EXEC('DROP SCHEMA [{orphanSchema}]');", ("@s", orphanSchema));
        }
    }

    [SqlServerFact]
    public async Task Reconcile_GhostRegistration_ListedInGhostRegistrations()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        using var host = new HandlerTestHost(sqlServerConnectionString: conn);
        var registry = new SqlServerProjectRegistry(new SqlServerConnectionOptions(conn, SingleDb));
        var ghostId = Unique("幽靈登記");

        try
        {
            // 造幽靈:登記 registry 但不建對應 schema。
            await registry.RegisterAsync(Doc(ghostId), Unique("user"), CancellationToken.None);

            var data = await host.DispatchAsync("dev.db.reconcile");

            var ghosts = data.GetProperty("ghostRegistrations").EnumerateArray()
                .Select(e => e.GetProperty("projectId").GetString()).ToList();
            Assert.Contains(ghostId, ghosts);
        }
        finally
        {
            await registry.UnregisterAsync(ghostId, CancellationToken.None);
        }
    }

    [SqlServerFact]
    public async Task Reconcile_ZombieFolder_ListedInZombieFolders()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        using var host = new HandlerTestHost(sqlServerConnectionString: conn);
        var zombieId = Unique("殭屍資料夾");

        // 造殭屍:本機有 sqlServer project.json,但無對應 schema、無 registry。
        await WriteLocalSqlServerProjectJsonAsync(host.ProjectsRoot, zombieId);

        var data = await host.DispatchAsync("dev.db.reconcile");

        var zombies = data.GetProperty("zombieFolders").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        // zombieFolders 為 host 本地資料夾集合,故可精確驗此筆存在。
        Assert.Contains(zombieId, zombies);
    }

    private static ProjectDocument Doc(string projectId) =>
        new(projectId, "REC", "對帳測試", "op", "2024-01-01", "2024-12-31", null,
            ProjectDocument.DefaultMoneyScale, ProjectDocument.DefaultRoundingMode,
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            CurrentStep: 1, ProjectDocument.CurrentSchemaVersion, ProjectDocument.SqlServerDatabaseProvider);

    private static async Task WriteLocalSqlServerProjectJsonAsync(string projectsRoot, string projectId)
    {
        var dir = Path.Combine(projectsRoot, projectId);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "project.json"), $$"""
            {
              "projectId": "{{projectId}}",
              "projectCode": "REC-1",
              "entityName": "殭屍案",
              "operatorId": "op",
              "periodStart": "2025-01-01",
              "periodEnd": "2025-12-31",
              "lastAccountingPeriodDate": null,
              "moneyScale": 10000,
              "roundingMode": "AwayFromZero",
              "createdUtc": "2026-06-01T00:00:00+00:00",
              "currentStep": 1,
              "schemaVersion": 1,
              "databaseProvider": "sqlServer"
            }
            """);
    }

    private static async Task ExecuteAsync(
        string baseConnectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(
            new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = SingleDb }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }
}
