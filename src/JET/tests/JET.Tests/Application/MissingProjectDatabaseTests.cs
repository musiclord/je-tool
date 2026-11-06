using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 裁定 2026-11-05-72：本機案件的資料庫不見或變成 0 位元組時，比照無法開啟的案件，不再自動建立空的資料庫；
/// 建立案件的初始化程序也要穩健。只有建案會建表，開案與讀寫遇到資料庫不見或沒有內容時照實回報，檔案保持原狀；
/// 建案中斷留下的案件，因為有建案中的標記，開案時把初始化做完。SQLite 與 DuckDB 都測。這是新增的測試。
/// </summary>
public sealed class MissingProjectDatabaseTests
{
    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task MissingDatabase_IsListedAsUnopenable_AndOpeningDoesNotCreateAFile(string provider)
    {
        using var root = new TempProjectRoot();
        var name = $"missing-{provider}";
        await CreateCaseAsync(root.Path, name, provider);
        var databasePath = DatabasePath(root.Path, name, provider);
        DeleteDatabaseFiles(databasePath);

        using var host = new HandlerTestHost(projectsRootPath: root.Path);
        var expected = ProjectFolderMessages.DatabaseMissing(name);
        AssertListedAsUnopenable(await host.DispatchAsync("project.listLocal"), name, expected);

        var rejected = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.load", $$"""{ "projectId": "{{name}}" }"""));
        Assert.Equal(JetErrorCodes.FileReadError, rejected.Code);
        Assert.Equal(expected, rejected.Message);
        Assert.False(File.Exists(databasePath), "開案不得替資料庫遺失的案件建出新檔");

        // 工作鎖已經釋放：另一個執行個體再開，得到的仍是同一個原因，而不是案件被鎖定。
        using var otherInstance = new HandlerTestHost(projectsRootPath: root.Path);
        var again = await Assert.ThrowsAsync<JetActionException>(
            () => otherInstance.DispatchAsync("project.load", $$"""{ "projectId": "{{name}}" }"""));
        Assert.Equal(JetErrorCodes.FileReadError, again.Code);
        Assert.False(File.Exists(databasePath));

        // 無法開啟的案件照常可以從案件清單刪除。
        await otherInstance.DispatchAsync("project.delete", $$"""{ "projectId": "{{name}}" }""");
        Assert.False(Directory.Exists(Path.Combine(root.Path, name)));
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task ZeroByteDatabase_IsListedAsUnopenable_AndOpeningLeavesTheFileEmpty(string provider)
    {
        using var root = new TempProjectRoot();
        var name = $"empty-{provider}";
        await CreateCaseAsync(root.Path, name, provider);
        var databasePath = DatabasePath(root.Path, name, provider);
        DeleteDatabaseFiles(databasePath);
        await File.WriteAllBytesAsync(databasePath, []);

        using var host = new HandlerTestHost(projectsRootPath: root.Path);
        var expected = ProjectFolderMessages.DatabaseEmpty(name);
        AssertListedAsUnopenable(await host.DispatchAsync("project.listLocal"), name, expected);

        var rejected = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.load", $$"""{ "projectId": "{{name}}" }"""));
        Assert.Equal(JetErrorCodes.FileReadError, rejected.Code);
        Assert.Equal(expected, rejected.Message);
        Assert.Equal(0, new FileInfo(databasePath).Length);
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task DatabaseWithoutTables_IsRejectedWithoutChangingTheFile(string provider)
    {
        using var root = new TempProjectRoot();
        var name = $"no-tables-{provider}";
        await CreateCaseAsync(root.Path, name, provider);
        var databasePath = DatabasePath(root.Path, name, provider);
        DeleteDatabaseFiles(databasePath);
        await WriteDatabaseWithoutTablesAsync(root.Path, name, provider);
        var before = await File.ReadAllBytesAsync(databasePath);

        using var host = new HandlerTestHost(projectsRootPath: root.Path);
        var rejected = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.load", $$"""{ "projectId": "{{name}}" }"""));
        Assert.Equal(JetErrorCodes.FileReadError, rejected.Code);
        Assert.Equal(ProjectFolderMessages.DatabaseEmpty(name), rejected.Message);

        SqliteConnection.ClearAllPools();
        Assert.Equal(before, await File.ReadAllBytesAsync(databasePath));
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider, false)]
    [InlineData(ProjectDocument.DefaultDatabaseProvider, true)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider, false)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider, true)]
    public async Task InterruptedCreate_IsListedNormally_AndOpeningFinishesTheInitialization(string provider, bool zeroByteFile)
    {
        using var root = new TempProjectRoot();
        var name = $"interrupted-{provider}-{(zeroByteFile ? "empty" : "missing")}";
        await CreateCaseAsync(root.Path, name, provider);
        var databasePath = DatabasePath(root.Path, name, provider);
        var markerPath = new JetProjectFolder(root.Path).GetCreationMarkerPath(name);

        // 重現建案中斷：設定檔與建案中的標記已經發布，資料庫還沒建好。
        DeleteDatabaseFiles(databasePath);
        if (zeroByteFile)
        {
            await File.WriteAllBytesAsync(databasePath, []);
        }
        await File.WriteAllBytesAsync(markerPath, []);

        using var host = new HandlerTestHost(projectsRootPath: root.Path);
        var listed = FindListed(await host.DispatchAsync("project.listLocal"), name);
        Assert.False(listed.TryGetProperty("loadError", out _), "建案中斷的案件照常列出，開案時把初始化做完");

        var loaded = await host.DispatchAsync("project.load", $$"""{ "projectId": "{{name}}" }""");
        Assert.Equal(name, loaded.GetProperty("project").GetProperty("projectId").GetString());
        Assert.True(new FileInfo(databasePath).Length > 0, "開案時把資料庫建好");
        Assert.False(File.Exists(markerPath), "初始化做完後刪掉建案中的標記");
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task CompletedCreate_RemovesTheMarker_AndTheNewCaseIsListedNormally(string provider)
    {
        using var root = new TempProjectRoot();
        var name = $"created-{provider}";
        using var host = new HandlerTestHost(projectsRootPath: root.Path);
        await host.DispatchAsync("project.create", CreatePayload(name, provider));

        // 建案後連線還開著時，資料表可能只在記錄檔裡；案件清單只看檔案大小，所以資料庫檔本身不得是 0 位元組。
        Assert.True(new FileInfo(DatabasePath(root.Path, name, provider)).Length > 0);
        Assert.False(File.Exists(new JetProjectFolder(root.Path).GetCreationMarkerPath(name)));
        var listed = FindListed(await host.DispatchAsync("project.listLocal"), name);
        Assert.False(listed.TryGetProperty("loadError", out _));
        Assert.Equal(provider, listed.GetProperty("databaseProvider").GetString());
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task ReadingAfterTheDatabaseWasDeleted_ReportsItMissingInsteadOfCreatingAnEmptyOne(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        var projectId = $"deleted-while-open-{provider}";
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await database.EnsureReadyAsync(projectId, CancellationToken.None);

        var databasePath = database.GetDatabasePath(projectId);
        DeleteDatabaseFiles(databasePath);

        var rejected = await Assert.ThrowsAsync<JetActionException>(
            () => database.EnsureReadyAsync(projectId, CancellationToken.None));
        Assert.Equal(JetErrorCodes.FileReadError, rejected.Code);
        Assert.Equal(ProjectFolderMessages.DatabaseMissing(projectId), rejected.Message);
        Assert.False(File.Exists(databasePath), "讀寫前的檢查不得替被刪掉的資料庫建出新檔");
    }

    private static async Task CreateCaseAsync(string projectsRoot, string name, string provider)
    {
        using var creator = new HandlerTestHost(projectsRootPath: projectsRoot);
        await creator.DispatchAsync("project.create", CreatePayload(name, provider));
    }

    private static string CreatePayload(string name, string provider) =>
        JsonSerializer.Serialize(new
        {
            projectName = name,
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            databaseProvider = provider,
        });

    private static string DatabasePath(string projectsRoot, string name, string provider) =>
        Path.Combine(
            projectsRoot,
            name,
            provider == ProjectDocument.DuckDbDatabaseProvider
                ? DuckDbProjectDatabase.DatabaseFileName
                : JetProjectFolder.DatabaseFileName);

    private static void DeleteDatabaseFiles(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm", ".wal" })
        {
            File.Delete(databasePath + suffix);
        }
    }

    private static async Task WriteDatabaseWithoutTablesAsync(string projectsRoot, string name, string provider)
    {
        var folder = new JetProjectFolder(projectsRoot);
        ILocalProjectDatabase database = provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        await using (var connection = database.CreateConnection(name))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = provider == ProjectDocument.DuckDbDatabaseProvider
                ? "CREATE TABLE scratch (x INTEGER); DROP TABLE scratch; CHECKPOINT;"
                : "CREATE TABLE scratch (x INTEGER); DROP TABLE scratch;";
            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
    }

    private static JsonElement FindListed(JsonElement list, string name) =>
        list.GetProperty("projects").EnumerateArray()
            .Single(project => project.GetProperty("projectId").GetString() == name);

    private static void AssertListedAsUnopenable(JsonElement list, string name, string expectedMessage)
    {
        var listed = FindListed(list, name);
        var loadError = listed.GetProperty("loadError");
        Assert.Equal(JetErrorCodes.FileReadError, loadError.GetProperty("code").GetString());
        Assert.Equal(expectedMessage, loadError.GetProperty("message").GetString());
    }
}
