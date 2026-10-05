using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 舊版 JET 建立的本機資料庫（schema 第 1 到第 5 版）不再升版：開啟時回報 invalid_project_schema，
/// 而且在任何寫入之前就擋下，檔案內容保持原狀。版本值讀不出整數的資料庫同樣擋下。
/// 新資料庫照舊從第 6 版建起再升到現行版。
/// </summary>
public sealed class LegacySchemaRejectionTests
{
    private const string LegacySchemaMessage =
        "這個案件的資料庫是舊版 JET 建立的格式，目前版本無法開啟。請用目前版本重新建立案件，再重新匯入資料。";

    private const string V5SchemaAndData =
        """
        CREATE TABLE schema_info (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        INSERT INTO schema_info (key, value) VALUES ('schema_version', '5');

        CREATE TABLE target_gl_entry (
            entry_id              INTEGER PRIMARY KEY,
            batch_id              TEXT NOT NULL,
            document_number       TEXT NULL,
            line_item             TEXT NULL,
            amount_scaled         INTEGER NOT NULL
        );

        INSERT INTO target_gl_entry (entry_id, batch_id, document_number, line_item, amount_scaled)
        VALUES (1, 'batch-v5', 'DOC-V5', '7', 1000000);
        """;

    [Fact]
    public async Task Sqlite_SchemaVersion5Database_IsRejectedWithoutChangingTheFile()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = V5SchemaAndData;
            await seed.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        var path = database.GetDatabasePath(projectId);
        var before = await File.ReadAllBytesAsync(path);

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => database.EnsureCreatedAsync(projectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.InvalidProjectSchema, ex.Code);
        Assert.Equal(LegacySchemaMessage, ex.Message);

        SqliteConnection.ClearAllPools();
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + "-wal"));
    }

    [Fact]
    public async Task DuckDb_SchemaVersion5Database_IsRejectedWithoutChangingTheFile()
    {
        using var root = new TempProjectRoot();
        var database = new DuckDbProjectDatabase(new JetProjectFolder(root.Path));
        var projectId = Guid.NewGuid().ToString("N");

        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = V5SchemaAndData + "\nCHECKPOINT;";
            await seed.ExecuteNonQueryAsync();
        }

        var path = database.GetDatabasePath(projectId);
        var before = await File.ReadAllBytesAsync(path);

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => database.EnsureCreatedAsync(projectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.InvalidProjectSchema, ex.Code);
        Assert.Equal(LegacySchemaMessage, ex.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task UnreadableSchemaVersion_IsRejectedWithoutChangingTheFile(string provider)
    {
        // 版本值讀不出整數代表檔案損壞；不得猜測從哪一版升級，也不得略過升版後照常開啟。
        const string unreadableMessage =
            "這個案件資料庫的版本資訊無法辨識，目前版本無法開啟。請從備份還原案件資料夾，或用目前版本重新建立案件，再重新匯入資料。";
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == "sqlite"
            ? new SqliteProjectDatabase(folder)
            : new DuckDbProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                CREATE TABLE schema_info (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO schema_info (key, value) VALUES ('schema_version', 'not-a-version');
                """ + (provider == "duckdb" ? "\nCHECKPOINT;" : string.Empty);
            await seed.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        var path = database.GetDatabasePath(projectId);
        var before = await File.ReadAllBytesAsync(path);

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => database.EnsureCreatedAsync(projectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.InvalidProjectSchema, ex.Code);
        Assert.Equal(unreadableMessage, ex.Message);

        SqliteConnection.ClearAllPools();
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task NewDatabase_IsCreatedAndUpgradedToCurrentVersion(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == "sqlite"
            ? new SqliteProjectDatabase(folder)
            : new DuckDbProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM schema_info WHERE key = 'schema_version';";
        Assert.Equal("11", await command.ExecuteScalarAsync() as string);
    }
}
