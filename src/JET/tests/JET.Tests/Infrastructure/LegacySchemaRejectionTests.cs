using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 本機案件資料庫只接受現行版本（裁定 2026-10-30-23：案件資料庫只留現行結構，版本不符時請審計員刪除後重建）。
/// 比現行版本舊的資料庫開啟時回報 invalid_project_schema，訊息寫明刪除後重建，而且在任何寫入之前就擋下，檔案內容保持原狀。
/// 版本值讀不出整數的資料庫同樣擋下。新資料庫直接建成現行版本。
/// </summary>
public sealed class LegacySchemaRejectionTests
{
    // 依裁定 2026-10-30-23，舊版案件的訊息要寫明下一步是刪除後重建，訊息由「請用目前版本重新建立案件」改成這一句。
    // 第一次失敗收據 20261030-065928748-be3c29975faa4b92b43ff12519708767。
    private const string LegacySchemaMessage =
        "這個案件是舊版 JET 建立的，目前版本無法開啟。請在案件清單刪除這個案件，再用目前版本重新建立並匯入資料。";

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
    [InlineData("sqlite", "14")]
    [InlineData("duckdb", "14")]
    [InlineData("sqlite", "15")]
    [InlineData("duckdb", "15")]
    [InlineData("sqlite", "16")]
    [InlineData("duckdb", "16")]
    [InlineData("sqlite", "17")]
    [InlineData("duckdb", "17")]
    [InlineData("sqlite", "18")]
    [InlineData("duckdb", "18")]
    public async Task PreviousSchemaVersion_IsRejectedAsLegacyProjectWithoutChangingTheFile(string provider, string olderVersion)
    {
        // 拿掉升版程式前的最後一版是第 14 版：結構和現行相同，只差版本號，也要當成舊版案件擋下，不再升版。
        // 統一改名前建立的案件是第 15 版，同樣當成舊版案件擋下。
        // 條件組結果改成依條件組編號儲存之前建立的案件是第 16 版，同樣當成舊版案件擋下。
        // 驗證摘要的鍵名改成 voucherNumberAcrossGlDates 之前建立的案件是第 17 版，同樣當成舊版案件擋下（裁定 2026-11-01-01）。第一次失敗收據 20261101-062343383-804cee29d9d9428f9e5f502ea4e7ae94。
        // 資料缺漏輔助檢視的驗證摘要鍵名改用 blank 字根之前建立的案件是第 18 版，同樣當成舊版案件擋下。第一次失敗收據 20261103-025603757-17ca9fea8bde42369861458b84ebd4dc。
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == "sqlite"
            ? new SqliteProjectDatabase(folder)
            : new DuckDbProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var lower = connection.CreateCommand();
            lower.CommandText = $"UPDATE schema_info SET value = '{olderVersion}' WHERE key = 'schema_version';"
                + (provider == "duckdb" ? "\nCHECKPOINT;" : "\nPRAGMA wal_checkpoint(TRUNCATE);");
            await lower.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        var reopened = provider == "sqlite"
            ? (ILocalProjectDatabase)new SqliteProjectDatabase(folder)
            : new DuckDbProjectDatabase(folder);
        var path = reopened.GetDatabasePath(projectId);
        var before = await File.ReadAllBytesAsync(path);

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => reopened.EnsureCreatedAsync(projectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.InvalidProjectSchema, ex.Code);
        Assert.Equal(LegacySchemaMessage, ex.Message);

        SqliteConnection.ClearAllPools();
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
        // 裁定 2026-10-13-15 把訊息分三級，訊息紀錄的等級限制在第 12 版加上 error。第一次失敗收據 20261013-095708679-ad35db917ff24a45800e552e9bc7753c。
        // 裁定 2026-10-21-04 把日期不符分成三種原因，本機 schema 再升到第 13 版。第一次失敗收據 20261021-080930829-9ee6cdc9021744a0bf40903ea77f833a。
        // 裁定 2026-10-22-15 讓每個來源可指定欄名在第幾列，import_batch_source 加 header_row 欄，本機 schema 升到第 14 版。第一次失敗收據 20261023-194708449-a199df52d31e49c882414cd4f37b05ba。
        // 裁定 2026-10-30-23：升版程式拿掉，新資料庫直接建成現行結構，版本推進到第 15 版，讓第 14 版以前的案件都當成舊版。
        // 第一次失敗收據 20261030-065928748-be3c29975faa4b92b43ff12519708767。
        // 統一改名改了表名、欄名與 CHECK 值，版本推進到第 16 版，讓改名前建立的第 15 版案件也當成舊版。
        // 第一次失敗收據 20261030-135241888-3c45f8ad7a044419beee5d439bf6db8a。
        // 條件組結果改成依條件組編號儲存，filter_scenario 加 scenario_id、新增 filter_hit_condition，版本推進到第 17 版，
        // 讓第 16 版以前建立的案件也當成舊版。第一次失敗收據 20261031-015438711-e019d04ff2b64ad08bc34c916334bd83。
        // 驗證摘要裡同一傳票號碼出現在不同總帳日期的鍵名改用 voucher（裁定 2026-11-01-01），存好的摘要和舊案件不同，
        // 版本推進到第 18 版，讓第 17 版以前建立的案件也當成舊版。第一次失敗收據 20261101-062343383-804cee29d9d9428f9e5f502ea4e7ae94。
        // 資料缺漏輔助檢視的驗證摘要鍵名改用 blank 字根，存好的摘要和舊案件不同，版本推進到第 19 版，讓第 18 版以前建立的案件也當成舊版。第一次失敗收據 20261103-025603757-17ca9fea8bde42369861458b84ebd4dc。
        // 欄位資訊多記每一欄的來歷與對應的 JET 欄位（裁定 2026-11-03-11），field_info 加 field_origin 與 jet_field，版本推進到第 20 版，
        // 讓第 19 版以前建立的案件也當成舊版。第一次失敗：Focused 收據 20261103-070053462-34d240fe519d4190a85d1a4368c1a80a。
        Assert.Equal("20", await command.ExecuteScalarAsync() as string);
        Assert.Equal("20", LocalSchemaReadiness.CurrentSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
