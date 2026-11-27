using System.Globalization;
using System.Text.RegularExpressions;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// JET 開啟案件時不讀資料庫裡的版本紀錄，改為比對資料庫和建表程式：結構齊全時不寫入任何東西；
/// 缺少的索引與觸發條件，以及登記為定版後新增的表與欄位，在一個交易裡補上，失敗就整個復原；
/// 缺少其他表或欄位的，是首次發布以前的 JET 建立的，請使用者刪除後重建，檔案保持原狀。
/// 案件裡多出 JET 不認得的表或欄位時照常開啟，不改它們。
/// 新建的案件照首次發布的值寫入版本紀錄，首次發布的 JET 才開得了新版建立的案件。
/// </summary>
public sealed class ProjectSchemaCompatibilityTests
{
    private const string ProbeTable = "CREATE TABLE IF NOT EXISTS probe_table (id INTEGER PRIMARY KEY);";

    private const string ProbeTableWithNote =
        "CREATE TABLE IF NOT EXISTS probe_table (id INTEGER PRIMARY KEY, note TEXT NOT NULL DEFAULT 'x');";

    private const string AddNote = "ALTER TABLE probe_table ADD COLUMN note TEXT NOT NULL DEFAULT 'x';";

    private const string SchemaFromBeforeTheFirstRelease =
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

    // 首次發布的 JET 開啟案件時要讀到這個值，才認得是它能開的案件；JET 自己不讀它。
    [Fact]
    public async Task NewDatabase_RecordsTheSchemaVersionThatTheFirstReleaseReads()
    {
        using var root = new TempProjectRoot();
        var database = new SqliteProjectDatabase(new JetProjectFolder(root.Path));
        var projectId = await CreateAsync(database);

        Assert.Equal("21", await TextAsync(database, projectId,
            "SELECT value FROM schema_info WHERE key = 'schema_version';"));
    }

    [Fact]
    public async Task CaseWithTheCurrentStructure_OpensWithoutChangingTheFile()
    {
        using var root = new TempProjectRoot();
        var database = new SqliteProjectDatabase(new JetProjectFolder(root.Path));
        var projectId = await CreateAsync(database);
        await database.CloseAsync(projectId, CancellationToken.None);
        SqliteConnection.ClearAllPools();
        var path = database.GetDatabasePath(projectId);
        var before = await File.ReadAllBytesAsync(path);

        await database.EnsureExistingAsync(projectId, CancellationToken.None);
        await database.CloseAsync(projectId, CancellationToken.None);

        SqliteConnection.ClearAllPools();
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    // 已經建立的案件在 schema_info 留有 schema_version 這一列；不論存的是什麼，開啟時都不讀它，也不改它。
    [Theory]
    [InlineData("'5'")]
    [InlineData("'20'")]
    [InlineData("'99'")]
    [InlineData("'not-a-version'")]
    [InlineData("X'3230'")]
    public async Task StoredSchemaVersionValue_IsNotReadAndStaysAsItWas(string storedValue)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);
        var projectId = await CreateAsync(database);
        await ExecuteAsync(database, projectId,
            $"INSERT OR REPLACE INTO schema_info (key, value) VALUES ('schema_version', {storedValue});");
        await database.CloseAsync(projectId, CancellationToken.None);
        var stored = await TextAsync(database, projectId,
            "SELECT quote(value) FROM schema_info WHERE key = 'schema_version';");

        var reopened = new SqliteProjectDatabase(folder);
        await reopened.EnsureExistingAsync(projectId, CancellationToken.None);

        Assert.Equal(stored, await TextAsync(reopened, projectId,
            "SELECT quote(value) FROM schema_info WHERE key = 'schema_version';"));
    }

    [Fact]
    public async Task MissingIndexesAndTriggers_AreCreatedAgainOnOpen()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);
        var projectId = await CreateAsync(database);
        await ExecuteAsync(database, projectId,
            "DROP INDEX ix_gl_voucher; DROP TRIGGER trg_operation_log_no_update;");
        await database.CloseAsync(projectId, CancellationToken.None);

        var reopened = new SqliteProjectDatabase(folder);
        await reopened.EnsureExistingAsync(projectId, CancellationToken.None);

        Assert.Equal(1L, await CountAsync(reopened, projectId,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_gl_voucher';"));
        Assert.Equal(1L, await CountAsync(reopened, projectId,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name = 'trg_operation_log_no_update';"));
    }

    [Fact]
    public async Task LaterAddedTableAndColumn_AreCreatedOnOpenAndTheDataIsKept()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var earlier = new SqliteProjectDatabase(folder, WithSql(ProbeTable));
        var projectId = await CreateAsync(earlier);
        await ExecuteAsync(earlier, projectId,
            "INSERT INTO probe_table (id) VALUES (1);"
            + "INSERT INTO message_log (occurred_utc, level, text) VALUES ('2026-01-01T00:00:00Z', 'info', 'kept');");
        await earlier.CloseAsync(projectId, CancellationToken.None);
        var later = new SqliteProjectDatabase(folder, new ProjectSchema(
            SqliteProjectDatabase.CurrentSchema.CreateSql + "\n" + ProbeTableWithNote
            + "\nCREATE TABLE IF NOT EXISTS addition_probe (id INTEGER PRIMARY KEY);"
            + "\nCREATE INDEX IF NOT EXISTS ix_probe_table_note ON probe_table (note);",
            [
                ProjectSchemaAddition.NewColumn("probe_table", "note", AddNote),
                ProjectSchemaAddition.NewTable("addition_probe"),
            ]));

        await later.EnsureExistingAsync(projectId, CancellationToken.None);
        await later.EnsureExistingAsync(projectId, CancellationToken.None);

        Assert.Equal(1L, await CountAsync(later, projectId,
            "SELECT COUNT(*) FROM pragma_table_info('probe_table') WHERE name = 'note';"));
        Assert.Equal("x", await TextAsync(later, projectId, "SELECT note FROM probe_table WHERE id = 1;"));
        Assert.Equal(1L, await CountAsync(later, projectId,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'addition_probe';"));
        Assert.Equal(1L, await CountAsync(later, projectId,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_probe_table_note';"));
        Assert.Equal("kept", await TextAsync(later, projectId, "SELECT text FROM message_log;"));
    }

    [Fact]
    public async Task MissingTableNotListedAsAnAddition_IsRejectedAsAnEarlierCaseWithoutChangingTheFile()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);
        var projectId = await CreateAsync(database);
        await ExecuteAsync(database, projectId, "DROP TABLE calendar_day;");

        await AssertRejectedAsEarlierCaseAsync(new SqliteProjectDatabase(folder), projectId);
    }

    [Fact]
    public async Task MissingColumnNotListedAsAnAddition_IsRejectedAsAnEarlierCaseWithoutChangingTheFile()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var earlier = new SqliteProjectDatabase(folder, WithSql(ProbeTable));
        var projectId = await CreateAsync(earlier);

        await AssertRejectedAsEarlierCaseAsync(
            new SqliteProjectDatabase(folder, WithSql(ProbeTableWithNote)), projectId);
    }

    [Fact]
    public async Task CaseFromBeforeTheFirstRelease_IsRejectedWithoutChangingTheFile()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await ExecuteAsync(database, projectId, SchemaFromBeforeTheFirstRelease);
        SqliteConnection.ClearAllPools();
        var path = database.GetDatabasePath(projectId);
        var before = await File.ReadAllBytesAsync(path);

        var rejected = await Assert.ThrowsAsync<JetActionException>(
            () => database.EnsureCreatedAsync(projectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.InvalidProjectSchema, rejected.Code);
        Assert.Equal(ProjectFolderMessages.OldVersionProject(), rejected.Message);
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + "-wal"));
    }

    [Fact]
    public async Task UnknownTablesAndColumns_AreLeftAsTheyAre()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);
        var projectId = await CreateAsync(database);
        await ExecuteAsync(database, projectId,
            "CREATE TABLE unknown_probe (id INTEGER PRIMARY KEY); ALTER TABLE message_log ADD COLUMN unknown_note TEXT;");
        await database.CloseAsync(projectId, CancellationToken.None);

        var reopened = new SqliteProjectDatabase(folder);
        await reopened.EnsureExistingAsync(projectId, CancellationToken.None);

        Assert.Equal(1L, await CountAsync(reopened, projectId,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'unknown_probe';"));
        Assert.Equal(1L, await CountAsync(reopened, projectId,
            "SELECT COUNT(*) FROM pragma_table_info('message_log') WHERE name = 'unknown_note';"));
    }

    [Fact]
    public async Task FirstReleaseDatabase_OpensWithTheSameStructureAndInitialDataAsANewOne()
    {
        // Fixtures/project-schema-first-release.sql 是首次發布的建表程式，之後不再修改。
        // 用它建立的資料庫開啟後，資料表、欄位、索引、觸發條件與建表時寫入的初始資料，包括 schema_version 這一列，
        // 都要和新建的資料庫相同。兩邊相同，首次發布的 JET 也開得了新版建立的案件。
        // 改了建表程式卻沒有把新的表或欄位登記為定版後新增，這個測試會失敗。
        // 比對時不計 SQL 的註解、空白與名稱的引號：ALTER TABLE 新增的欄位接在欄位清單最後、表格限制之前，
        // 新建的資料表也把這個欄位寫在同一個位置，兩邊就會相同。
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);
        var fresh = await CreateAsync(database);
        await database.CloseAsync(fresh, CancellationToken.None);
        var released = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(released));
        await ExecuteAsync(database, released,
            await File.ReadAllTextAsync(TestRepositoryPaths.Fixture("project-schema-first-release.sql")));
        var storedVersion = await TextAsync(database, released,
            "SELECT value FROM schema_info WHERE key = 'schema_version';");
        await database.CloseAsync(released, CancellationToken.None);

        await database.EnsureExistingAsync(released, CancellationToken.None);

        var expected = await StructureAndDataAsync(database, fresh);
        var actual = await StructureAndDataAsync(database, released);
        Assert.True(expected == actual,
            "首次發布的 JET 建立的案件資料庫開啟後，結構或初始資料和新建的資料庫不同。"
            + "在建表程式加了表或欄位時，要把它登記為定版後新增，欄位另附 ALTER TABLE，讓已經建立的案件得到同樣的結果。\n"
            + $"新建：\n{expected}\n開啟後：\n{actual}");
        Assert.Equal(storedVersion, await TextAsync(database, released,
            "SELECT value FROM schema_info WHERE key = 'schema_version';"));
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
    public async Task FailedCompletion_RollsBackEveryChangeAndCanBeRetried()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var earlier = new SqliteProjectDatabase(folder, WithSql(ProbeTable));
        var projectId = await CreateAsync(earlier);
        await ExecuteAsync(earlier, projectId, "INSERT INTO probe_table (id) VALUES (1);");
        await earlier.CloseAsync(projectId, CancellationToken.None);
        const string twoColumns =
            "CREATE TABLE IF NOT EXISTS probe_table (id INTEGER PRIMARY KEY, note TEXT NOT NULL DEFAULT 'x', "
            + "label TEXT NOT NULL DEFAULT 'y');";
        var broken = new SqliteProjectDatabase(folder, new ProjectSchema(
            SqliteProjectDatabase.CurrentSchema.CreateSql + "\n" + twoColumns,
            [
                ProjectSchemaAddition.NewColumn("probe_table", "note", AddNote),
                // 少了預設值，SQLite 不允許用 ALTER TABLE 加上 NOT NULL 欄位，這一句會失敗。
                ProjectSchemaAddition.NewColumn("probe_table", "label", "ALTER TABLE probe_table ADD COLUMN label TEXT NOT NULL;"),
            ]));

        var failure = await Assert.ThrowsAsync<JetActionException>(
            () => broken.EnsureExistingAsync(projectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.InvalidProjectSchema, failure.Code);
        Assert.Equal(ProjectFolderMessages.DatabaseUpgradeFailed, failure.Message);
        Assert.IsType<SqliteException>(failure.InnerException);
        Assert.Equal(0L, await CountAsync(earlier, projectId,
            "SELECT COUNT(*) FROM pragma_table_info('probe_table') WHERE name = 'note';"));

        // 改好新增欄位的語句後再開啟一次，兩個欄位都補上。
        var repaired = new SqliteProjectDatabase(folder, new ProjectSchema(
            SqliteProjectDatabase.CurrentSchema.CreateSql + "\n" + twoColumns,
            [
                ProjectSchemaAddition.NewColumn("probe_table", "note", AddNote),
                ProjectSchemaAddition.NewColumn("probe_table", "label", "ALTER TABLE probe_table ADD COLUMN label TEXT NOT NULL DEFAULT 'y';"),
            ]));
        await repaired.EnsureExistingAsync(projectId, CancellationToken.None);
        Assert.Equal("x|y", await TextAsync(repaired, projectId, "SELECT note || '|' || label FROM probe_table WHERE id = 1;"));
    }

    [Fact]
    public async Task CompletionBlockedByAnotherWriter_IsReportedAsAFailedUpgrade()
    {
        // 另一個程式正在寫入同一個資料庫時，補齊結構取不到交易；回報的訊息要請使用者關閉其他程式後再開啟一次。
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);
        var projectId = await CreateAsync(database);
        await database.CloseAsync(projectId, CancellationToken.None);
        var later = WithAddedTable();
        var path = database.GetDatabasePath(projectId);
        await using var holder = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await holder.OpenAsync();
        await using var held = holder.BeginTransaction(deferred: false);
        await using var completing = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 1, Pooling = false }.ToString());
        await completing.OpenAsync();

        var failure = await Assert.ThrowsAsync<JetActionException>(
            () => later.CompleteAsync(completing, CancellationToken.None));

        Assert.Equal(JetErrorCodes.InvalidProjectSchema, failure.Code);
        Assert.Equal(ProjectFolderMessages.DatabaseUpgradeFailed, failure.Message);
        Assert.IsType<SqliteException>(failure.InnerException);
    }

    [Fact]
    public async Task CompletionOfAReadOnlyDatabase_IsReportedWithTheReadOnlyDatabaseError()
    {
        // 資料庫檔無法寫入這類認得的資料庫錯誤，照一般的資料庫錯誤回報，使用者才知道要處理的是檔案本身。
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);
        var projectId = await CreateAsync(database);
        await database.CloseAsync(projectId, CancellationToken.None);
        await using var readOnly = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.GetDatabasePath(projectId),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await readOnly.OpenAsync();

        var failure = await Assert.ThrowsAsync<JetActionException>(
            () => WithAddedTable().CompleteAsync(readOnly, CancellationToken.None));

        Assert.Equal(JetErrorCodes.DatabaseReadOnly, failure.Code);
    }

    [Fact]
    public async Task CompletionOfACompleteDatabase_RunsNothing()
    {
        // 另一條連線先補好時，取得交易後重新比對，結構已經齊全，不再執行任何新增欄位的語句。
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder, WithSql(ProbeTableWithNote));
        var projectId = await CreateAsync(database);
        var schema = new ProjectSchema(
            SqliteProjectDatabase.CurrentSchema.CreateSql + "\n" + ProbeTableWithNote,
            [ProjectSchemaAddition.NewColumn("probe_table", "note", "INSERT INTO table_that_does_not_exist (id) VALUES (1);")]);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();

        await schema.CompleteAsync(connection, CancellationToken.None);

        Assert.Equal(1L, await CountAsync(database, projectId,
            "SELECT COUNT(*) FROM pragma_table_info('probe_table') WHERE name = 'note';"));
    }

    private static ProjectSchema WithSql(string sql) =>
        new(SqliteProjectDatabase.CurrentSchema.CreateSql + "\n" + sql, []);

    private static ProjectSchema WithAddedTable() =>
        new(SqliteProjectDatabase.CurrentSchema.CreateSql + "\nCREATE TABLE IF NOT EXISTS addition_probe (id INTEGER PRIMARY KEY);",
            [ProjectSchemaAddition.NewTable("addition_probe")]);

    private static async Task AssertRejectedAsEarlierCaseAsync(SqliteProjectDatabase database, string projectId)
    {
        await database.CloseAsync(projectId, CancellationToken.None);
        SqliteConnection.ClearAllPools();
        var path = database.GetDatabasePath(projectId);
        var before = await File.ReadAllBytesAsync(path);

        var rejected = await Assert.ThrowsAsync<JetActionException>(
            () => database.EnsureExistingAsync(projectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.InvalidProjectSchema, rejected.Code);
        Assert.Equal(ProjectFolderMessages.OldVersionProject(), rejected.Message);
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    private static async Task<string> CreateAsync(SqliteProjectDatabase database)
    {
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(database.GetDatabasePath(projectId))!);
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
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
