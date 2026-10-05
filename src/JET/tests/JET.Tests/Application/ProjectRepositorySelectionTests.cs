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
/// 這組測試在改成「案件建立或載入時選定 repository」之前先釘住現行行為（使用者 2026-10-02 選 A）；
/// 斷言只看檔案、錯誤碼與 session 是否仍指向原案件，不看內部物件。
/// </summary>
public sealed class ProjectRepositorySelectionTests
{
    private const string UnknownProvider = "synthetic-engine";

    [Fact]
    public async Task LoadDuckDbCase_ThenCreateSqliteCaseAndImportGl_WritesOnlyTheNewSqliteDatabase()
    {
        using var root = new TempProjectRoot();
        using var host = new HandlerTestHost(projectsRootPath: root.Path);
        var duckDbCase = NewProjectId("duckdb-a");
        var sqliteCase = NewProjectId("sqlite-b");

        await host.DispatchAsync("project.create", CreatePayload(duckDbCase, ProjectDocument.DuckDbDatabaseProvider));
        await host.DispatchAsync("project.load", LoadPayload(duckDbCase));
        var duckDbPath = Path.Combine(root.Path, duckDbCase, DuckDbProjectDatabase.DatabaseFileName);
        var duckDbHashBefore = FileHash(duckDbPath);

        await host.DispatchAsync("project.create", CreatePayload(sqliteCase, ProjectDocument.DefaultDatabaseProvider));
        var glFile = WriteGlWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glFile }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(glFile);
        }

        Assert.True(File.Exists(Path.Combine(root.Path, sqliteCase, JetProjectFolder.DatabaseFileName)));
        Assert.False(File.Exists(Path.Combine(root.Path, sqliteCase, DuckDbProjectDatabase.DatabaseFileName)));
        Assert.False(File.Exists(Path.Combine(root.Path, duckDbCase, JetProjectFolder.DatabaseFileName)));
        Assert.Equal(duckDbHashBefore, FileHash(duckDbPath));

        var overview = await host.DispatchAsync("dev.db.overview");
        Assert.Equal(ProjectDocument.DefaultDatabaseProvider, overview.GetProperty("databaseProvider").GetString());
        AssertDatabaseUnder(overview, root.Path, sqliteCase);
        Assert.Equal(2L, TableRowCount(overview, "staging_gl_raw_row"));
    }

    [Fact]
    public async Task DeleteInactiveDuckDbCase_KeepsActiveSqliteCase_ThenDeletingActiveCaseClearsSession()
    {
        using var root = new TempProjectRoot();
        using var host = new HandlerTestHost(projectsRootPath: root.Path);
        var duckDbCase = NewProjectId("duckdb-c");
        var sqliteCase = NewProjectId("sqlite-a");

        await host.DispatchAsync("project.create", CreatePayload(duckDbCase, ProjectDocument.DuckDbDatabaseProvider));
        await host.DispatchAsync("project.create", CreatePayload(sqliteCase, ProjectDocument.DefaultDatabaseProvider));

        var deleted = await host.DispatchAsync("project.delete", LoadPayload(duckDbCase));

        Assert.True(deleted.GetProperty("ok").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(root.Path, duckDbCase)));
        var overview = await host.DispatchAsync("dev.db.overview");
        Assert.Equal(ProjectDocument.DefaultDatabaseProvider, overview.GetProperty("databaseProvider").GetString());
        AssertDatabaseUnder(overview, root.Path, sqliteCase);

        await host.DispatchAsync("project.delete", LoadPayload(sqliteCase));

        var noSession = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("dev.db.overview"));
        Assert.Equal(JetErrorCodes.NoActiveProject, noSession.Code);
    }

    [Fact]
    public async Task LoadOtherCaseFails_KeepsPreviousSessionAndReleasesTargetLock()
    {
        using var root = new TempProjectRoot();
        var sqliteCase = NewProjectId("sqlite-b");
        var duckDbCase = NewProjectId("duckdb-a");
        using (var creator = new HandlerTestHost(projectsRootPath: root.Path))
        {
            await creator.DispatchAsync("project.create", CreatePayload(sqliteCase, ProjectDocument.DefaultDatabaseProvider));
        }

        using var host = new HandlerTestHost(projectsRootPath: root.Path);
        await host.DispatchAsync("project.create", CreatePayload(duckDbCase, ProjectDocument.DuckDbDatabaseProvider));

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
        Assert.Equal(ProjectDocument.DuckDbDatabaseProvider, overview.GetProperty("databaseProvider").GetString());
        AssertDatabaseUnder(overview, root.Path, duckDbCase);
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

    [Theory]
    [InlineData("sqlite", "duckdb")]
    [InlineData("duckdb", "sqlite")]
    public async Task CreateFailsAtWorkLock_RetrySameNameWithOtherProvider_BuildsTheRetriedDatabase(
        string firstProvider,
        string retryProvider)
    {
        using var root = new TempProjectRoot();
        using var host = new HandlerTestHost(projectsRootPath: root.Path);
        var projectId = NewProjectId("create-retry");
        var projectDirectory = Path.Combine(root.Path, projectId);

        // 模擬另一個執行個體已持有同名案件的工作鎖：先建空資料夾取鎖，再移走資料夾讓建案能開始。
        // 鎖檔放在案件根目錄，不在案件資料夾內，所以移走資料夾後鎖仍有效。
        var external = new LocalFileLockService(new JetProjectFolder(root.Path));
        try
        {
            Directory.CreateDirectory(projectDirectory);
            Assert.IsType<LockOutcome.Acquired>(
                await external.AcquireAsync(projectId, "external-holder", CancellationToken.None));
            Directory.Delete(projectDirectory);

            var held = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync("project.create", CreatePayload(projectId, firstProvider)));
            Assert.Equal(JetErrorCodes.ProjectLocked, held.Code);
            Assert.False(Directory.Exists(projectDirectory));
        }
        finally
        {
            external.Dispose();
        }

        await host.DispatchAsync("project.create", CreatePayload(projectId, retryProvider));

        Assert.True(File.Exists(Path.Combine(projectDirectory, DatabaseFileName(retryProvider))));
        Assert.False(File.Exists(Path.Combine(projectDirectory, DatabaseFileName(firstProvider))));
        var overview = await host.DispatchAsync("dev.db.overview");
        Assert.Equal(retryProvider, overview.GetProperty("databaseProvider").GetString());
        Assert.Equal(
            Path.GetFullPath(Path.Combine(projectDirectory, DatabaseFileName(retryProvider))),
            Path.GetFullPath(overview.GetProperty("databasePath").GetString()!),
            ignoreCase: true);
    }

    [Theory]
    [InlineData("sqlite", "duckdb")]
    [InlineData("duckdb", "sqlite")]
    public async Task DeleteThenRecreateSameNameWithOtherProvider_LaterImportUsesTheNewDatabase(
        string firstProvider,
        string recreatedProvider)
    {
        using var root = new TempProjectRoot();
        using var host = new HandlerTestHost(projectsRootPath: root.Path);
        var projectId = NewProjectId("recreate");
        var projectDirectory = Path.Combine(root.Path, projectId);
        var glFile = WriteGlWorkbook();
        try
        {
            // 先匯入一次，讓還沒遷移的動作以案件編號記住第一種資料庫。
            await host.DispatchAsync("project.create", CreatePayload(projectId, firstProvider));
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glFile }));
            await host.DispatchAsync("project.delete", LoadPayload(projectId));

            await host.DispatchAsync("project.create", CreatePayload(projectId, recreatedProvider));
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glFile }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(glFile);
        }

        Assert.True(File.Exists(Path.Combine(projectDirectory, DatabaseFileName(recreatedProvider))));
        Assert.False(File.Exists(Path.Combine(projectDirectory, DatabaseFileName(firstProvider))));
        var overview = await host.DispatchAsync("dev.db.overview");
        Assert.Equal(recreatedProvider, overview.GetProperty("databaseProvider").GetString());
        Assert.Equal(2L, TableRowCount(overview, "staging_gl_raw_row"));
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
        provider == ProjectDocument.DuckDbDatabaseProvider
            ? DuckDbProjectDatabase.DatabaseFileName
            : JetProjectFolder.DatabaseFileName;

    private static string NewProjectId(string scenario) =>
        $"repo-select-{scenario}-{Guid.NewGuid():N}";

    private static string CreatePayload(string projectId, string provider) =>
        JsonSerializer.Serialize(new
        {
            caseName = projectId,
            projectCode = "REPO-SELECT",
            entityName = "資料庫選定測試",
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
