using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// Master spec「本機先顯示、線上手動同步」的最低 Application seam。
/// project.listLocal 只列目前 root 的 SQLite 案件；以前建立的 DuckDB 與 SQL Server 案件不列出（裁定 2026-11-06-09）。
/// </summary>
public sealed class ProjectListLocalFirstTests
{
    [Fact]
    public async Task ProjectListLocal_ReturnsOnlyLocalProvidersWithBoundedWireShape()
    {
        using var host = new HandlerTestHost();
        await WriteProjectAsync(host.ProjectsRoot, Document("sqlite-case", "sqlite", 1));
        await WriteProjectAsync(host.ProjectsRoot, Document("duckdb-case", "duckdb", 2));
        await WriteProjectAsync(host.ProjectsRoot, Document("server-cache", "sqlServer", 3));

        var data = await host.DispatchAsync("project.listLocal");

        Assert.Equal(["projects"], data.EnumerateObject().Select(property => property.Name));
        var projects = data.GetProperty("projects").EnumerateArray().ToArray();
        // 裁定 2026-11-06-09：以前建立的 DuckDB 案件也不列出。原本預期 DuckDB 與 SQLite 兩個案件，改成只剩 SQLite 案件；
        // 第一次失敗：Focused 收據 20261106-084017183-285ee971d2ad44c3b0f22bb9c5f69f21。
        var listed = Assert.Single(projects);
        Assert.Equal("sqlite-case", listed.GetProperty("projectId").GetString());
        Assert.All(projects, project =>
        {
            Assert.Contains(
                project.GetProperty("databaseProvider").GetString(),
                new[] { ProjectDocument.DefaultDatabaseProvider });
            Assert.Equal(
                new[]
                {
                    "projectId", "projectCode", "clientName", "periodStart", "periodEnd",
                    "createdUtc", "currentStep", "databaseProvider", "lastOpenedUtc"
                },
                project.EnumerateObject().Select(property => property.Name));
        });
    }

    /// <summary>
    /// 裁定 2026-11-06-09：以前建立的 DuckDB 與 SQL Server 案件不列出，資料夾也不動。設定檔讀得通時看記錄的種類；
    /// 讀不通時看設定檔原文記錄的種類，連原文都讀不通就看資料夾裡是不是只有以前的 DuckDB 資料庫檔。
    /// 判斷不出來或確定是 SQLite 的案件，讀不通時照舊列成錯誤列，審計員才找得到它、能刪除。這是新增的測試。
    /// </summary>
    [Fact]
    public async Task ProjectListLocal_HidesRetiredProviderCases_AndStillListsUnreadableOtherCases()
    {
        using var host = new HandlerTestHost();
        var root = host.ProjectsRoot;
        await WriteProjectAsync(root, Document("sqlite-case", "sqlite", 1));
        await WriteProjectAsync(root, Document("duckdb-readable", "duckdb", 2));
        await WriteProjectAsync(root, Document("server-readable", "sqlServer", 3));
        File.WriteAllBytes(Path.Combine(root, "duckdb-readable", "jet.duckdb"), [1, 2, 3]);
        WriteRaw(root, "duckdb-recorded-broken", """{"databaseProvider":"duckdb"}""", "jet.duckdb");
        WriteRaw(root, "server-recorded-broken", """{"databaseProvider":"sqlServer"}""");
        WriteRaw(root, "duckdb-file-only", "{ not json", "jet.duckdb");
        WriteRaw(root, "sqlite-recorded-broken", """{"databaseProvider":"sqlite"}""");
        WriteRaw(root, "sqlite-file-broken", "{ not json", "jet.db");
        WriteRaw(root, "unknown-broken", "{ not json");
        var retired = new[]
        {
            "duckdb-readable", "server-readable", "duckdb-recorded-broken", "server-recorded-broken", "duckdb-file-only"
        };
        var before = retired.ToDictionary(id => id, id => FolderSnapshot(root, id), StringComparer.Ordinal);

        var data = await host.DispatchAsync("project.listLocal");

        var projects = data.GetProperty("projects").EnumerateArray().ToArray();
        Assert.Equal(["sqlite-case"], projects
            .Where(project => !project.TryGetProperty("loadError", out _))
            .Select(project => project.GetProperty("projectId").GetString()));
        Assert.Equal(["sqlite-file-broken", "sqlite-recorded-broken", "unknown-broken"], projects
            .Where(project => project.TryGetProperty("loadError", out _))
            .Select(project => project.GetProperty("projectId").GetString())
            .Order(StringComparer.Ordinal));
        Assert.All(retired, id => Assert.Equal(before[id], FolderSnapshot(root, id)));
    }

    /// <summary>
    /// 以前的 DuckDB 與 SQL Server 案件不列在清單上（裁定 2026-11-06-09）。審計員用同一個名稱建立 SQLite 案件時，
    /// 訊息要說出名稱被以前的案件佔用與能怎麼做，不能只說「已存在」，也不能報那個看不到的案件的設定檔錯誤；
    /// 資料夾照樣不動。獨立複審 2026-11-06 找到的缺口，這是新增的測試。
    /// </summary>
    [Fact]
    public async Task ProjectCreate_WithTheNameOfAHiddenRetiredCase_ExplainsTheOldFolderAndLeavesItUntouched()
    {
        using var host = new HandlerTestHost();
        var root = host.ProjectsRoot;
        await WriteProjectAsync(root, Document("合成舊案甲", "duckdb", 1));
        File.WriteAllBytes(Path.Combine(root, "合成舊案甲", "jet.duckdb"), [1, 2, 3]);
        WriteRaw(root, "合成舊案乙", "{ not json", "jet.duckdb");
        WriteRaw(root, "合成舊案丙", """{"databaseProvider":"sqlServer"}""");
        var names = new[] { "合成舊案甲", "合成舊案乙", "合成舊案丙" };
        var before = names.ToDictionary(id => id, id => FolderSnapshot(root, id), StringComparer.Ordinal);

        foreach (var name in names)
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("project.create",
                JsonSerializer.Serialize(new { projectName = name, periodStart = "2025-01-01", periodEnd = "2025-12-31" })));
            Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
            Assert.Equal(JetErrorFields.ProjectName, error.Field);
            Assert.Equal(
                $"案件名稱「{name}」已被以前建立的 DuckDB 或 SQL Server 案件使用，這類案件不列在案件清單。" +
                "請改用其他名稱，或到 JET 的案件位置刪除這個案件資料夾後再建立。",
                error.Message);
        }

        Assert.All(names, id => Assert.Equal(before[id], FolderSnapshot(root, id)));
        var listed = (await host.DispatchAsync("project.listLocal")).GetProperty("projects").EnumerateArray()
            .Select(project => project.GetProperty("projectId").GetString());
        Assert.DoesNotContain(listed, id => id is not null && names.Contains(id));
    }

    private static void WriteRaw(string root, string projectId, string projectJson, params string[] databaseFiles)
    {
        var directory = Path.Combine(root, projectId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "project.json"), projectJson);
        foreach (var file in databaseFiles)
        {
            File.WriteAllBytes(Path.Combine(directory, file), [1, 2, 3]);
        }
    }

    /// <summary>資料夾內每個檔案的相對路徑與內容；用來確認清單沒有改動、刪除或新增任何檔案。</summary>
    private static string[] FolderSnapshot(string root, string projectId)
    {
        var directory = Path.Combine(root, projectId);
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(file => $"{Path.GetRelativePath(directory, file)}:{Convert.ToHexString(File.ReadAllBytes(file))}")
            .Order(StringComparer.Ordinal)
            .ToArray();
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
            _ => null
        };
        if (database is not null)
        {
            await database.EnsureCreatedAsync(document.ProjectId, CancellationToken.None);
            SqliteConnection.ClearAllPools();
        }
    }
}
