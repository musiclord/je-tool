using System.Globalization;
using System.Text.RegularExpressions;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 案件資料庫的版本相容：首次發布的版本起，較早版本建立的案件開啟時逐版升到現行版本，資料保留；
/// 升版在一個交易裡完成，中途失敗時整個復原。比現行版本新的案件不開啟，訊息請使用者改用較新版本的 JET。
/// 結構一改就要升版並加升版步驟：首次發布的建表程式升版後，要和新建的資料庫完全相同。
/// </summary>
public sealed class ProjectSchemaUpgradeTests
{
    private const int Released = 21;

    [Fact]
    public void ReleasedSchema_KeepsTheFirstReleasedVersionAsTheOldestUpgradableOne()
    {
        // 首次發布的版本固定是第 21 版。提高它，等於不再開啟已發布版本建立的案件。
        Assert.Equal(Released, ProjectSchemaVersions.Released.OldestUpgradable);
        Assert.Equal(LocalSchemaReadiness.CurrentSchemaVersion, ProjectSchemaVersions.Released.Current);
        Assert.True(ProjectSchemaVersions.Released.Current >= ProjectSchemaVersions.Released.OldestUpgradable);
    }

    [Fact]
    public void Steps_MustCoverEveryVersionFromTheOldestToTheCurrent()
    {
        var first = ProjectSchemaUpgrade.Sql(21, "SELECT 1;");
        var third = ProjectSchemaUpgrade.Sql(23, "SELECT 1;");

        Assert.Throws<InvalidOperationException>(() => new ProjectSchemaVersions(21, 23, [first]));
        Assert.Throws<InvalidOperationException>(() => new ProjectSchemaVersions(21, 23, [first, third]));
        Assert.Throws<InvalidOperationException>(() => new ProjectSchemaVersions(21, 21, [first]));
        Assert.Throws<InvalidOperationException>(() => new ProjectSchemaVersions(22, 21, []));
    }

    [Fact]
    public async Task OlderReleasedVersion_IsUpgradedStepByStepAndKeepsItsData()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectId = await CreateReleasedProjectAsync(folder);
        var later = new SqliteProjectDatabase(folder, new ProjectSchemaVersions(Released, 23,
        [
            ProjectSchemaUpgrade.Sql(21, "CREATE TABLE upgrade_probe (id INTEGER PRIMARY KEY, note TEXT NOT NULL);"),
            new ProjectSchemaUpgrade(22, async (connection, transaction, cancellationToken) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO upgrade_probe (id, note) SELECT message_id, text FROM message_log;";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }),
        ]));

        await later.EnsureExistingAsync(projectId, CancellationToken.None);
        await later.EnsureExistingAsync(projectId, CancellationToken.None);

        Assert.Equal("23", await TextAsync(later, projectId, "SELECT value FROM schema_info WHERE key = 'schema_version';"));
        Assert.Equal("kept", await TextAsync(later, projectId, "SELECT text FROM message_log;"));
        Assert.Equal("kept", await TextAsync(later, projectId, "SELECT note FROM upgrade_probe;"));
        Assert.Equal(1L, await CountAsync(later, projectId, "SELECT COUNT(*) FROM upgrade_probe;"));
    }

    [Fact]
    public async Task FailedUpgrade_RollsBackEveryStepAndKeepsTheStoredVersion()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectId = await CreateReleasedProjectAsync(folder);
        var later = new SqliteProjectDatabase(folder, new ProjectSchemaVersions(Released, 23,
        [
            ProjectSchemaUpgrade.Sql(21, "CREATE TABLE upgrade_probe (id INTEGER PRIMARY KEY);"),
            ProjectSchemaUpgrade.Sql(22, "INSERT INTO table_that_does_not_exist (id) VALUES (1);"),
        ]));

        var failure = await Assert.ThrowsAsync<JetActionException>(
            () => later.EnsureExistingAsync(projectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.InvalidProjectSchema, failure.Code);
        Assert.Equal(ProjectFolderMessages.DatabaseUpgradeFailed, failure.Message);
        Assert.IsType<SqliteException>(failure.InnerException);
        var released = new SqliteProjectDatabase(folder);
        Assert.Equal("21", await TextAsync(released, projectId, "SELECT value FROM schema_info WHERE key = 'schema_version';"));
        Assert.Equal(0L, await CountAsync(released, projectId,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'upgrade_probe';"));
        Assert.Equal("kept", await TextAsync(released, projectId, "SELECT text FROM message_log;"));

        // 修好升版步驟後再開啟一次，從原本的版本完整升級，兩步都會執行。
        var repaired = new SqliteProjectDatabase(folder, new ProjectSchemaVersions(Released, 23,
        [
            ProjectSchemaUpgrade.Sql(21, "CREATE TABLE upgrade_probe (id INTEGER PRIMARY KEY);"),
            ProjectSchemaUpgrade.Sql(22, "INSERT INTO upgrade_probe (id) VALUES (1);"),
        ]));
        await repaired.EnsureExistingAsync(projectId, CancellationToken.None);
        Assert.Equal("23", await TextAsync(repaired, projectId, "SELECT value FROM schema_info WHERE key = 'schema_version';"));
        Assert.Equal(1L, await CountAsync(repaired, projectId, "SELECT COUNT(*) FROM upgrade_probe;"));
    }

    [Fact]
    public async Task CancelledUpgrade_IsReportedAsCancellationAndKeepsTheStoredVersion()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectId = await CreateReleasedProjectAsync(folder);
        using var cancellation = new CancellationTokenSource();
        var later = new SqliteProjectDatabase(folder, new ProjectSchemaVersions(Released, 22,
        [
            new ProjectSchemaUpgrade(21, (_, _, _) =>
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }),
        ]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => later.EnsureExistingAsync(projectId, cancellation.Token));

        Assert.Equal("21", await TextAsync(new SqliteProjectDatabase(folder), projectId,
            "SELECT value FROM schema_info WHERE key = 'schema_version';"));
    }

    [Fact]
    public async Task NewerVersion_IsRejectedWithItsOwnMessageWithoutChangingTheFile()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectId = await CreateReleasedProjectAsync(folder);
        var database = new SqliteProjectDatabase(folder);
        var newer = (LocalSchemaReadiness.CurrentSchemaVersion + 1).ToString(CultureInfo.InvariantCulture);
        await ExecuteAsync(database, projectId, $"UPDATE schema_info SET value = '{newer}' WHERE key = 'schema_version';");
        await database.CloseAsync(projectId, CancellationToken.None);
        SqliteConnection.ClearAllPools();
        var before = await File.ReadAllBytesAsync(database.GetDatabasePath(projectId));

        var rejected = await Assert.ThrowsAsync<JetActionException>(
            () => database.EnsureExistingAsync(projectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.InvalidProjectSchema, rejected.Code);
        Assert.Equal(
            "這個案件是較新版本的 JET 建立或開啟過的，目前版本無法開啟。請改用較新版本的 JET 開啟這個案件。",
            rejected.Message);
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, await File.ReadAllBytesAsync(database.GetDatabasePath(projectId)));
    }

    [Fact]
    public async Task VersionOlderThanTheFirstRelease_IsStillRejectedWhenLaterVersionsCanUpgrade()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectId = await CreateReleasedProjectAsync(folder);
        var released = new SqliteProjectDatabase(folder);
        await ExecuteAsync(released, projectId, "UPDATE schema_info SET value = '20' WHERE key = 'schema_version';");
        await released.CloseAsync(projectId, CancellationToken.None);
        var later = new SqliteProjectDatabase(folder, new ProjectSchemaVersions(Released, 22,
            [ProjectSchemaUpgrade.Sql(21, "CREATE TABLE upgrade_probe (id INTEGER PRIMARY KEY);")]));

        var rejected = await Assert.ThrowsAsync<JetActionException>(
            () => later.EnsureExistingAsync(projectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.InvalidProjectSchema, rejected.Code);
        Assert.Equal(ProjectFolderMessages.OldVersionProject(), rejected.Message);
    }

    [Fact]
    public async Task FirstReleasedDatabase_OpensWithTheSameStructureAndInitialDataAsANewOne()
    {
        // Fixtures/project-schema-v21.sql 是首次發布時的建表程式，之後不再修改。
        // 用它建立的資料庫經過全部升版步驟後，資料表、欄位、索引、觸發器與建表時寫入的初始資料，都要和新建的資料庫完全相同。
        // 改了建表程式卻沒有加升版步驟，或升版步驟漏改了什麼，這個測試都會失敗。
        // 比對時不計 SQL 的註解、空白與名稱的引號：ALTER TABLE 新增的欄位接在欄位清單最後、表格限制之前，
        // 新建的資料表也把這個欄位寫在同一個位置，兩邊就會相同。
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);
        var fresh = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(fresh));
        await database.EnsureCreatedAsync(fresh, CancellationToken.None);
        await database.CloseAsync(fresh, CancellationToken.None);
        var released = await CreateReleasedProjectAsync(folder, withMessage: false);

        await database.EnsureExistingAsync(released, CancellationToken.None);

        var expected = await StructureAndDataAsync(database, fresh);
        var actual = await StructureAndDataAsync(database, released);
        Assert.True(expected == actual,
            "首次發布版本建立的案件資料庫升版後，結構或初始資料和新建的資料庫不同。"
            + "改了建表程式時，要把現行版本加一，並在 ProjectSchemaVersions.Released 加一步升版，讓已經建立的案件得到同樣的結果。\n"
            + $"新建：\n{expected}\n升版後：\n{actual}");
    }

    [Fact]
    public void FormatsStoredInsideTheCase_KeepTheirFirstReleasedVersions()
    {
        // 欄位配對的格式版本與抽樣演算法的版本存在案件裡，讀到和目前不同的值時拒絕開啟。
        // 報告裡隱藏的配對紀錄也記著格式版本，版本不同時，之前匯出的報告就無法帶入欄位配對。
        // 改這些值之前，要先決定已發布版本建立的案件與報告怎麼處理。
        Assert.Equal(2, MappingMetadataFormat.CurrentVersion);
        Assert.Equal(2, InfSamplingPrf.CurrentAlgorithmVersion);
        Assert.Equal(1, ReportWorkbookMetadataFormat.CurrentVersion);
    }

    [Fact]
    public async Task UpgradeBlockedByAnotherWriter_IsReportedAsAFailedUpgrade()
    {
        // 另一個程式正在寫入同一個資料庫時，升版取不到交易；回報的訊息要請使用者關閉其他程式後再開啟一次。
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectId = await CreateReleasedProjectAsync(folder);
        var database = new SqliteProjectDatabase(folder);
        var later = new ProjectSchemaVersions(Released, 22,
            [ProjectSchemaUpgrade.Sql(21, "CREATE TABLE upgrade_probe (id INTEGER PRIMARY KEY);")]);
        var path = database.GetDatabasePath(projectId);
        await using var holder = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await holder.OpenAsync();
        await using var held = holder.BeginTransaction(deferred: false);
        await using var upgrading = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 1, Pooling = false }.ToString());
        await upgrading.OpenAsync();

        var failure = await Assert.ThrowsAsync<JetActionException>(
            () => later.UpgradeAsync(upgrading, CancellationToken.None));

        Assert.Equal(JetErrorCodes.InvalidProjectSchema, failure.Code);
        Assert.Equal(ProjectFolderMessages.DatabaseUpgradeFailed, failure.Message);
        Assert.IsType<SqliteException>(failure.InnerException);
    }

    [Fact]
    public async Task UpgradeOfAReadOnlyDatabase_IsReportedWithTheReadOnlyDatabaseError()
    {
        // 資料庫檔無法寫入這類認得的資料庫錯誤，照一般的資料庫錯誤回報，使用者才知道要處理的是檔案本身。
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectId = await CreateReleasedProjectAsync(folder);
        var database = new SqliteProjectDatabase(folder);
        var later = new ProjectSchemaVersions(Released, 22,
            [ProjectSchemaUpgrade.Sql(21, "CREATE TABLE upgrade_probe (id INTEGER PRIMARY KEY);")]);
        await using var readOnly = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.GetDatabasePath(projectId),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await readOnly.OpenAsync();

        var failure = await Assert.ThrowsAsync<JetActionException>(
            () => later.UpgradeAsync(readOnly, CancellationToken.None));

        Assert.Equal(JetErrorCodes.DatabaseReadOnly, failure.Code);
    }

    [Fact]
    public async Task UpgradeOfADatabaseAlreadyAtTheCurrentVersion_RunsNoStep()
    {
        // 另一條連線先升好時，取得交易後重讀的版本已經是現行版本，不再執行任何一步。
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectId = await CreateReleasedProjectAsync(folder);
        var database = new SqliteProjectDatabase(folder);
        await ExecuteAsync(database, projectId, "UPDATE schema_info SET value = '22' WHERE key = 'schema_version';");
        var later = new ProjectSchemaVersions(Released, 22,
            [ProjectSchemaUpgrade.Sql(21, "INSERT INTO table_that_does_not_exist (id) VALUES (1);")]);
        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await later.UpgradeAsync(connection, CancellationToken.None);
        }

        Assert.Equal("22", await TextAsync(database, projectId, "SELECT value FROM schema_info WHERE key = 'schema_version';"));
    }

    private static async Task<string> CreateReleasedProjectAsync(JetProjectFolder folder, bool withMessage = true)
    {
        // 用首次發布的建表程式建立，現行版本日後提高時，這裡仍是真正的第 21 版資料庫。
        var database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        var builder = new SqliteConnectionStringBuilder { DataSource = database.GetDatabasePath(projectId), Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = await File.ReadAllTextAsync(TestRepositoryPaths.Fixture("project-schema-v21.sql"));
        if (withMessage)
        {
            command.CommandText +=
                "\nINSERT INTO message_log (occurred_utc, level, text) VALUES ('2026-01-01T00:00:00Z', 'info', 'kept');";
        }

        await command.ExecuteNonQueryAsync();
        return projectId;
    }

    private static async Task<string> StructureAndDataAsync(SqliteProjectDatabase database, string projectId)
    {
        var lines = new List<string>();
        var tables = new List<string>();
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT type, name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var sql = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                lines.Add($"{reader.GetString(0)} {reader.GetString(1)}: {NormalizeSql(sql)}");
                if (reader.GetString(0) == "table")
                {
                    tables.Add(reader.GetString(1));
                }
            }
        }

        foreach (var table in tables)
        {
            var rows = new List<string>();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table}\";";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var values = new string[reader.FieldCount];
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    values[index] = reader.IsDBNull(index)
                        ? "NULL"
                        : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty;
                }

                rows.Add($"{table}: {string.Join(" | ", values)}");
            }

            rows.Sort(StringComparer.Ordinal);
            lines.AddRange(rows);
        }

        return string.Join('\n', lines);
    }

    // 去掉 SQL 註解、名稱的引號，以及逗號與括號前後的空白，其餘連續空白併成一個。
    private static string NormalizeSql(string sql)
    {
        var withoutComments = Regex.Replace(sql, "--[^\n]*", string.Empty);
        var unquoted = Regex.Replace(withoutComments, "\"([A-Za-z_][A-Za-z0-9_]*)\"", "$1");
        var collapsed = Regex.Replace(unquoted, "\\s+", " ");
        return Regex.Replace(collapsed, "\\s*([,()])\\s*", "$1").Trim();
    }

    private static async Task ExecuteAsync(SqliteProjectDatabase database, string projectId, string sql)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> TextAsync(SqliteProjectDatabase database, string projectId, string sql)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<long> CountAsync(SqliteProjectDatabase database, string projectId, string sql)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
