using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 同一個 host 內切換不同資料庫種類的案件時，建案、載入與刪除都必須用該案件自己的資料庫。
/// 這組測試在改成「案件建立或載入時選定 repository」之前先釘住現行行為（裁定 2026-10-02-07）；
/// 斷言只看檔案、錯誤碼與 session 是否仍指向原案件，不看內部物件。
/// 只保留 SQLite 之後沒有第二種資料庫可切換（裁定 2026-11-06-08）：原本作用中案件是 DuckDB 案件的情境改成另一個
/// SQLite 案件；同名改用另一種資料庫重建的兩個情境沒有可用的資料庫，隨之刪除。
/// </summary>
public sealed class ProjectRepositorySelectionTests
{
    private const string UnknownProvider = "synthetic-engine";

    [Fact]
    public async Task LoadOtherCaseFails_KeepsPreviousSessionAndReleasesTargetLock()
    {
        using var root = new TempProjectRoot();
        var sqliteCase = NewProjectId("sqlite-b");
        var activeCase = NewProjectId("sqlite-a");
        using (var creator = new HandlerTestHost(projectsRootPath: root.Path))
        {
            await creator.DispatchAsync("project.create", CreatePayload(sqliteCase, ProjectDocument.DefaultDatabaseProvider));
        }

        using var host = new HandlerTestHost(projectsRootPath: root.Path);
        await host.DispatchAsync("project.create", CreatePayload(activeCase, ProjectDocument.DefaultDatabaseProvider));

        // 唯讀的 project.json 讓載入在取得工作鎖之後、寫回最近開啟時間時失敗。
        var projectJson = Path.Combine(root.Path, sqliteCase, JetProjectFolder.ProjectJsonFileName);
        File.SetAttributes(projectJson, FileAttributes.ReadOnly);
        try
        {
            var loadError = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync("project.load", LoadPayload(sqliteCase)));
            Assert.Equal(JetErrorCodes.FileReadError, loadError.Code);
        }
        finally
        {
            File.SetAttributes(projectJson, FileAttributes.Normal);
        }

        var overview = await host.DispatchAsync("dev.db.overview");
        Assert.Equal(ProjectDocument.DefaultDatabaseProvider, overview.GetProperty("databaseProvider").GetString());
        AssertDatabaseUnder(overview, root.Path, activeCase);
        AssertLockFree(root.Path, sqliteCase);
    }

    [Fact]
    public async Task LoadCaseWithUnknownProvider_RejectsBeforeLockingAndKeepsActiveCase()
    {
        using var root = new TempProjectRoot();
        var unknownCase = NewProjectId("unknown");
        var activeCase = NewProjectId("sqlite-a");
        using (var creator = new HandlerTestHost(projectsRootPath: root.Path))
        {
            await creator.DispatchAsync("project.create", CreatePayload(unknownCase, ProjectDocument.DefaultDatabaseProvider));
        }

        RewriteProvider(root.Path, unknownCase, UnknownProvider);
        using var host = new HandlerTestHost(projectsRootPath: root.Path);
        await host.DispatchAsync("project.create", CreatePayload(activeCase, ProjectDocument.DefaultDatabaseProvider));

        // 另一個執行個體持有工作鎖時，仍先回報資料庫種類不支援，不回報案件被鎖定。
        using (var external = new LocalFileLockService(new JetProjectFolder(root.Path)))
        {
            Assert.IsType<LockOutcome.Acquired>(
                await external.AcquireAsync(unknownCase, "external-holder", CancellationToken.None));
            var whileHeld = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync("project.load", LoadPayload(unknownCase)));
            Assert.Equal(JetErrorCodes.UnsupportedProvider, whileHeld.Code);
        }

        var rejected = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.load", LoadPayload(unknownCase)));

        Assert.Equal(JetErrorCodes.UnsupportedProvider, rejected.Code);
        AssertLockFree(root.Path, unknownCase);
        var overview = await host.DispatchAsync("dev.db.overview");
        AssertDatabaseUnder(overview, root.Path, activeCase);
    }

    [Fact]
    public async Task DeleteCaseWithUnknownProvider_RejectsBeforeDeletingAnything()
    {
        using var root = new TempProjectRoot();
        var unknownCase = NewProjectId("unknown");
        using (var creator = new HandlerTestHost(projectsRootPath: root.Path))
        {
            await creator.DispatchAsync("project.create", CreatePayload(unknownCase, ProjectDocument.DefaultDatabaseProvider));
        }

        RewriteProvider(root.Path, unknownCase, UnknownProvider);
        var projectJson = Path.Combine(root.Path, unknownCase, JetProjectFolder.ProjectJsonFileName);
        var databasePath = Path.Combine(root.Path, unknownCase, JetProjectFolder.DatabaseFileName);
        using var host = new HandlerTestHost(projectsRootPath: root.Path);

        using (var external = new LocalFileLockService(new JetProjectFolder(root.Path)))
        {
            Assert.IsType<LockOutcome.Acquired>(
                await external.AcquireAsync(unknownCase, "external-holder", CancellationToken.None));
            var whileHeld = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync("project.delete", LoadPayload(unknownCase)));
            Assert.Equal(JetErrorCodes.UnsupportedProvider, whileHeld.Code);
        }

        var rejected = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.delete", LoadPayload(unknownCase)));

        Assert.Equal(JetErrorCodes.UnsupportedProvider, rejected.Code);
        Assert.True(File.Exists(projectJson));
        Assert.True(File.Exists(databasePath));
        AssertLockFree(root.Path, unknownCase);
    }

    private static void AssertLockFree(string rootPath, string projectId)
    {
        using var probe = new LocalFileLockService(new JetProjectFolder(rootPath));
        var outcome = probe.AcquireAsync(projectId, "lock-probe", CancellationToken.None).GetAwaiter().GetResult();
        var acquired = Assert.IsType<LockOutcome.Acquired>(outcome);
        Assert.True(acquired.NewlyAcquired);
    }

    private static void AssertDatabaseUnder(JsonElement overview, string rootPath, string projectId)
    {
        var databasePath = Path.GetFullPath(overview.GetProperty("databasePath").GetString()!);
        var projectDirectory = Path.GetFullPath(Path.Combine(rootPath, projectId)) + Path.DirectorySeparatorChar;
        Assert.StartsWith(projectDirectory, databasePath, StringComparison.OrdinalIgnoreCase);
    }

    private static long TableRowCount(JsonElement overview, string tableName)
    {
        foreach (var table in overview.GetProperty("tables").EnumerateArray())
        {
            if (table.GetProperty("name").GetString() == tableName)
            {
                return table.GetProperty("rowCount").GetInt64();
            }
        }

        throw new Xunit.Sdk.XunitException($"dev.db.overview 沒有回報資料表 {tableName}");
    }

    private static void RewriteProvider(string rootPath, string projectId, string provider)
    {
        var path = Path.Combine(rootPath, projectId, JetProjectFolder.ProjectJsonFileName);
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var propertyName = node
            .Select(property => property.Key)
            .Single(key => string.Equals(key, "databaseProvider", StringComparison.OrdinalIgnoreCase));
        node[propertyName] = provider;
        File.WriteAllText(path, node.ToJsonString());
    }

    private static string FileHash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string DatabaseFileName(string provider) =>
        JetProjectFolder.DatabaseFileName;

    private static string NewProjectId(string scenario) =>
        $"repo-select-{scenario}-{Guid.NewGuid():N}";

    private static string CreatePayload(string projectId, string provider) =>
        JsonSerializer.Serialize(new
        {
            projectName = projectId,
            projectCode = "REPO-SELECT",
            clientName = "資料庫選定測試",
            operatorId = "auditor",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            databaseProvider = provider,
        });

    private static string LoadPayload(string projectId) =>
        JsonSerializer.Serialize(new { projectId });

    private static string WriteGlWorkbook() =>
        TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "傳票號碼"; ws.Cell(1, 2).Value = "傳票日期"; ws.Cell(1, 3).Value = "科目代號"; ws.Cell(1, 4).Value = "金額";
            ws.Cell(2, 1).Value = "JV-1"; ws.Cell(2, 2).Value = "2025-03-01"; ws.Cell(2, 3).Value = "1101"; ws.Cell(2, 4).Value = "100";
            ws.Cell(3, 1).Value = "JV-2"; ws.Cell(3, 2).Value = "2025-03-02"; ws.Cell(3, 3).Value = "4101"; ws.Cell(3, 4).Value = "100";
        });
}
