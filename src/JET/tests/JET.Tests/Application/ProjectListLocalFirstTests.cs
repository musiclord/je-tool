using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// Master spec「本機先顯示、線上手動同步」的最低 Application seam。
/// project.listLocal 只看目前 root 的 SQLite／DuckDB project.json，不得把 SQL Server cache 帶進啟動 snapshot。
/// </summary>
public sealed class ProjectListLocalFirstTests
{
    [Fact]
    public async Task ProjectListLocal_ReturnsOnlyLocalProvidersWithBoundedWireShape()
    {
        using var host = new HandlerTestHost(sqlServerConnectionString: string.Empty);
        await WriteProjectAsync(host.ProjectsRoot, Document("sqlite-case", "sqlite", 1));
        await WriteProjectAsync(host.ProjectsRoot, Document("duckdb-case", "duckdb", 2));
        await WriteProjectAsync(host.ProjectsRoot, Document("server-cache", "sqlServer", 3));

        var data = await host.DispatchAsync("project.listLocal");

        Assert.Equal(["projects"], data.EnumerateObject().Select(property => property.Name));
        var projects = data.GetProperty("projects").EnumerateArray().ToArray();
        Assert.Equal(2, projects.Length);
        Assert.Equal(["duckdb-case", "sqlite-case"],
            projects.Select(project => project.GetProperty("projectId").GetString()));
        Assert.All(projects, project =>
        {
            Assert.Contains(
                project.GetProperty("databaseProvider").GetString(),
                new[] { ProjectDocument.DefaultDatabaseProvider, ProjectDocument.DuckDbDatabaseProvider });
            Assert.Equal(
                new[]
                {
                    "projectId", "projectCode", "clientName", "periodStart", "periodEnd",
                    "createdUtc", "currentStep", "databaseProvider", "lastOpenedUtc"
                },
                project.EnumerateObject().Select(property => property.Name));
        });
    }

    private static ProjectDocument Document(string projectId, string provider, int createdDay) =>
        new(
            projectId,
            $"CODE-{createdDay}",
            $"Entity {createdDay}",
            "operator",
            "2025-01-01",
            "2025-12-31",
            null,
            ProjectDocument.DefaultMoneyScale,
            ProjectDocument.DefaultRoundingMode,
            new DateTimeOffset(2025, 1, createdDay, 0, 0, 0, TimeSpan.Zero),
            CurrentStep: 1,
            ProjectDocument.CurrentSchemaVersion,
            provider,
            // 目前版本建案一定寫入 INF 抽樣種子與版本；缺欄位的文件會被當成舊版案件拒絕。
            SampleSeed: 1_234_567,
            SampleSeedVersion: JetAuditProgram.CurrentInfSamplingAlgorithmVersion);

    private static async Task WriteProjectAsync(string root, ProjectDocument document)
    {
        var directory = Path.Combine(root, document.ProjectId);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "project.json"),
            JsonSerializer.Serialize(document, JetJsonStorage.IndentedOptions));

        // 裁定 2026-11-05-72：本機案件的資料庫檔不見時，案件清單把它列成無法開啟的錯誤列，所以正常的本機案件要有建好的資料庫。
        // 原本只寫 project.json。第一次失敗收據 20261105-155959508-7b95566f0d5a426aa5145c6a92f63fc1。
        var folder = new JetProjectFolder(root);
        ILocalProjectDatabase? database = document.DatabaseProvider switch
        {
            ProjectDocument.DefaultDatabaseProvider => new SqliteProjectDatabase(folder),
            ProjectDocument.DuckDbDatabaseProvider => new DuckDbProjectDatabase(folder),
            _ => null
        };
        if (database is not null)
        {
            await database.EnsureCreatedAsync(document.ProjectId, CancellationToken.None);
            SqliteConnection.ClearAllPools();
        }
    }
}
