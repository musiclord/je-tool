using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 本機案件資料庫只接受現行版本：案件資料庫只留現行結構，版本不符時請審計員刪除後重建。
/// 比現行版本舊的資料庫開啟時回報 invalid_project_schema，訊息寫明刪除後重建，而且在任何寫入之前就擋下，檔案內容保持原狀。
/// 版本值讀不出整數的資料庫同樣擋下。新資料庫直接建成現行版本。
/// </summary>
public sealed class LegacySchemaRejectionTests
{
    // 較早版本的 JET 建立的案件，訊息要寫明下一步是刪除後重建。
    private const string LegacySchemaMessage =
        "這個案件是較早版本的 JET 建立的，目前版本無法開啟。請在案件清單刪除這個案件，再用目前版本重新建立並匯入資料。";

    private const string V5SchemaAndData =
        """
        CREATE TABLE schema_info (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        INSERT INTO schema_info (key, value) VALUES ('schema_version', '5');

        CREATE TABLE gl (
            entry_id              INTEGER PRIMARY KEY,
            batch_id              TEXT NOT NULL,
            voucher_number       TEXT NULL,
            line_item             TEXT NULL,
            amount_scaled         INTEGER NOT NULL
        );

        INSERT INTO gl (entry_id, batch_id, voucher_number, line_item, amount_scaled)
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

    [Theory]
    [InlineData("14")]
    [InlineData("15")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    public async Task PreviousSchemaVersion_IsRejectedAsLegacyProjectWithoutChangingTheFile(string olderVersion)
    {
        // 這裡的資料庫結構和現行相同，只把版本號改成較舊的第 14 到第 18 版；
        // 沒有升版程式，每一版都當成較早版本的案件擋下，不升版。
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var lower = connection.CreateCommand();
            lower.CommandText = $"UPDATE schema_info SET value = '{olderVersion}' WHERE key = 'schema_version';"
                + ("\nPRAGMA wal_checkpoint(TRUNCATE);");
            await lower.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        var reopened = (ILocalProjectDatabase)new SqliteProjectDatabase(folder);
        var path = reopened.GetDatabasePath(projectId);
        var before = await File.ReadAllBytesAsync(path);

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => reopened.EnsureCreatedAsync(projectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.InvalidProjectSchema, ex.Code);
        Assert.Equal(LegacySchemaMessage, ex.Message);

        SqliteConnection.ClearAllPools();
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task UnreadableSchemaVersion_IsRejectedWithoutChangingTheFile()
    {
        // 版本值讀不出整數代表檔案損壞；不得猜測從哪一版升級，也不得略過升版後照常開啟。
        const string unreadableMessage =
            "這個案件資料庫的版本資訊無法辨識，目前版本無法開啟。請從備份還原案件資料夾，或用目前版本重新建立案件，再重新匯入資料。";
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
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
                """ + (string.Empty);
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

    // 版本值存成不是文字的內容（例如二進位）時，同樣讀不出整數；回的是版本資訊無法辨識那一句，不是非預期錯誤。
    [Theory]
    [InlineData("X'3230'")]
    [InlineData("X''")]
    public async Task SchemaVersionStoredAsBinary_IsRejectedAsUnreadableWithoutChangingTheFile(string storedValue)
    {
        const string unreadableMessage =
            "這個案件資料庫的版本資訊無法辨識，目前版本無法開啟。請從備份還原案件資料夾，或用目前版本重新建立案件，再重新匯入資料。";
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                "CREATE TABLE schema_info (key TEXT PRIMARY KEY, value TEXT NOT NULL);"
                + $"INSERT INTO schema_info (key, value) VALUES ('schema_version', {storedValue});";
            await seed.ExecuteNonQueryAsync();

            await using var stored = connection.CreateCommand();
            stored.CommandText = "SELECT typeof(value) FROM schema_info WHERE key = 'schema_version';";
            Assert.Equal("blob", await stored.ExecuteScalarAsync());
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

    [Fact]
    public async Task NewDatabase_IsCreatedAndUpgradedToCurrentVersion()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM schema_info WHERE key = 'schema_version';";
        // 新資料庫直接建成現行結構，不經過升版；現行版本是第 20 版，版本比它小的案件都擋下。
        Assert.Equal("20", await command.ExecuteScalarAsync() as string);
        Assert.Equal("20", LocalSchemaReadiness.CurrentSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
