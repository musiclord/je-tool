using System.Globalization;
using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 第 22 步盤點的第 7 項裡兩個沒有測試的邊界：比現行版本新的案件資料庫，以及最長的中文案件名稱。這是新增的測試。
/// </summary>
public sealed class CaseLimitJourneyTests
{
    /// <summary>
    /// 較新的 JET 建立的案件，用這一版開啟時要被拒絕：照裁定 2026-10-30-23 寫出無法開啟與接下來能做什麼，
    /// 資料庫檔保持原狀，案件照樣可以從清單刪除。
    /// </summary>
    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task NewerDatabaseVersion_IsRejected_TheFileStaysUnchanged_AndTheCaseCanBeDeleted(string provider)
    {
        using var workspace = new TempProjectRoot();
        var projects = Path.Combine(workspace.Path, "projects");
        Directory.CreateDirectory(projects);
        var name = $"newer-version-{provider}";
        using var host = new HandlerTestHost(projectsRootPath: projects);
        await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectName = name, periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider,
        }));
        await host.DispatchAsync("project.releaseLock");

        var databasePath = DatabasePath(projects, name, provider);
        var newer = (LocalSchemaReadiness.CurrentSchemaVersion + 1).ToString(CultureInfo.InvariantCulture);
        await using (var connection = DirectConnection(databasePath, provider))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE schema_info SET value = '{newer}' WHERE key = 'schema_version';";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var before = await File.ReadAllBytesAsync(databasePath);
        var rejected = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = name })));
        Assert.Equal(JetErrorCodes.InvalidProjectSchema, rejected.Code);
        Assert.Equal(ProjectFolderMessages.UnreadableDatabaseVersion, rejected.Message);
        var after = await File.ReadAllBytesAsync(databasePath);
        Assert.True(before.SequenceEqual(after), "被拒絕的資料庫檔不應被改動");

        await host.DispatchAsync("project.delete", JsonSerializer.Serialize(new { projectId = name }));
        Assert.False(Directory.Exists(Path.Combine(projects, name)));
    }

    /// <summary>
    /// 案件名稱最長 100 字。案件資料夾、資料庫、報告與底稿的路徑都在這個名稱的資料夾裡，
    /// 最長的中文名稱要能走完建案到底稿，之後從清單重新開啟、讀到兩份報告，再刪除案件。
    /// </summary>
    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task LongestChineseName_CompletesTheJourney_ReopensWithItsReports_AndCanBeDeleted(string provider)
    {
        using var workspace = new TempProjectRoot();
        var projects = Path.Combine(workspace.Path, "projects");
        Directory.CreateDirectory(projects);
        var name = string.Concat(Enumerable.Repeat("查核案件名稱長度測試", 10));
        Assert.Equal(ProjectNameRules.MaxLength, name.Length);

        var (gl, tb) = ScaleJourneyTests.WriteSources(workspace.Path, 200);
        var steps = await ScaleJourneyTests.RunJourneyAsync(projects, name, provider, gl, tb);
        Assert.Equal(10, steps.Count);
        Assert.Equal("ok", await ScaleJourneyTests.CheckIntegrityAsync(projects, name, provider));

        using var host = new HandlerTestHost(projectsRootPath: projects);
        var listed = (await host.DispatchAsync("project.listLocal")).GetProperty("projects").EnumerateArray()
            .Single(project => project.GetProperty("projectId").GetString() == name);
        Assert.False(listed.TryGetProperty("loadError", out _), "最長名稱的案件照常列在清單");

        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = name }));
        var reports = loaded.GetProperty("reports").EnumerateArray().ToArray();
        Assert.Equal(2, reports.Length);
        foreach (var report in reports)
        {
            Assert.True(File.Exists(report.GetProperty("fullPath").GetString()), "報告與底稿的檔案都在");
        }

        await host.DispatchAsync("project.releaseLock");
        await host.DispatchAsync("project.delete", JsonSerializer.Serialize(new { projectId = name }));
        Assert.False(Directory.Exists(Path.Combine(projects, name)));
    }

    private static string DatabasePath(string projects, string name, string provider)
    {
        var folder = new JetProjectFolder(projects);
        ILocalProjectDatabase database = provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        return database.GetDatabasePath(name);
    }

    /// <summary>不經 JET 的連線池，關閉時就真的關閉，記錄檔併回資料庫檔。</summary>
    private static System.Data.Common.DbConnection DirectConnection(string databasePath, string provider) =>
        provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDB.NET.Data.DuckDBConnection($"DataSource={databasePath}")
            : new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
}
