using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 資料庫 schema 遷移（第 1 版 → 第 2 版見 guide §3.1.4；第 2 版 → 第 3 版為
/// 2026-06-11 規則命名更名 + 科目配對）。fixture 以手刻的舊版 DDL 建庫
/// （凍結舊形狀作 golden master），再由 EnsureCreatedAsync 觸發遷移；
/// 斷言鎖回填值、翻譯結果與冪等性。
/// </summary>
public sealed class SchemaMigrationTests
{
    /// <summary>第 1 版 schema 的凍結快照（只含遷移涉及的表；其餘由 EnsureCreated 補建）。</summary>
    private const string V1SchemaAndData =
        """
        CREATE TABLE schema_info (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        INSERT INTO schema_info (key, value) VALUES ('schema_version', '1');

        CREATE TABLE import_batch (
            batch_id         TEXT PRIMARY KEY,
            dataset_kind     TEXT NOT NULL CHECK (dataset_kind IN ('gl','tb')),
            source_file_path TEXT NOT NULL,
            source_file_name TEXT NOT NULL,
            imported_utc     TEXT NOT NULL,
            row_count        INTEGER NOT NULL DEFAULT 0,
            columns_json     TEXT NOT NULL
        );

        CREATE TABLE staging_gl_raw_row (
            batch_id   TEXT NOT NULL,
            row_number INTEGER NOT NULL,
            row_json   TEXT NOT NULL,
            PRIMARY KEY (batch_id, row_number)
        );

        CREATE TABLE staging_tb_raw_row (
            batch_id   TEXT NOT NULL,
            row_number INTEGER NOT NULL,
            row_json   TEXT NOT NULL,
            PRIMARY KEY (batch_id, row_number)
        );

        INSERT INTO import_batch VALUES
            ('batch-gl-1', 'gl', 'C:\v1.xlsx', 'v1.xlsx', '2026-06-01T00:00:00.0000000+00:00', 3,
             '["doc","date","acc","name","desc","debit","credit"]'),
            ('batch-tb-1', 'tb', 'C:\v1-tb.xlsx', 'v1-tb.xlsx', '2026-06-01T00:00:00.0000000+00:00', 1,
             '["acc","name"]');

        -- 第 1 版的 row_number 即來源列號；刻意不連續（2,5,9）以證明回填是逐列複製、不是重新編號
        INSERT INTO staging_gl_raw_row VALUES
            ('batch-gl-1', 2, '{"doc":"D1","date":"2024-01-01","acc":"1101","name":"現金","desc":"a","debit":"100"}'),
            ('batch-gl-1', 5, '{"doc":"D1","date":"2024-01-01","acc":"1101","name":"現金","desc":"b","credit":"100"}'),
            ('batch-gl-1', 9, '{"doc":"D2","date":"2024-01-02","acc":"1101","name":"現金","desc":"c","debit":"5"}');

        INSERT INTO staging_tb_raw_row VALUES
            ('batch-tb-1', 2, '{"acc":"1101","name":"現金"}');
        """;

    private sealed class MigratedEnv : IDisposable
    {
        private readonly TempProjectRoot _root = new();

        public MigratedEnv()
        {
            Folder = new JetProjectFolder(_root.Path);
            Database = new SqliteProjectDatabase(Folder);
            ProjectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Folder.GetProjectDirectory(ProjectId));

            using var connection = Database.CreateConnection(ProjectId);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = V1SchemaAndData;
            command.ExecuteNonQuery();
        }

        public JetProjectFolder Folder { get; }
        public SqliteProjectDatabase Database { get; }
        public string ProjectId { get; }

        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = Database.CreateConnection(ProjectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var result = await command.ExecuteScalarAsync();
            return result is DBNull or null ? 0L : Convert.ToInt64(result);
        }

        public async Task<string> TextAsync(string sql)
        {
            await using var connection = Database.CreateConnection(ProjectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (string)(await command.ExecuteScalarAsync())!;
        }

        public void Dispose() => _root.Dispose();
    }

    [Fact]
    public async Task EnsureCreated_V1Database_MigratesToV3AndBackfills()
    {
        using var env = new MigratedEnv();

        await env.Database.EnsureCreatedAsync(env.ProjectId, CancellationToken.None);

        // 第 1 版鏈式升級一路走到目前 v9。
        Assert.Equal("10", await env.TextAsync("SELECT value FROM schema_info WHERE key='schema_version'"));

        // 回填：source_row_number = 第 1 版的 row_number（逐列相等，含不連續值 2,5,9）；source_no 一律 1
        Assert.Equal(3, await env.ScalarAsync(
            "SELECT COUNT(*) FROM staging_gl_raw_row WHERE source_row_number = row_number AND source_no = 1"));
        Assert.Equal(1, await env.ScalarAsync(
            "SELECT COUNT(*) FROM staging_tb_raw_row WHERE source_row_number = row_number AND source_no = 1"));

        // 每個既有批次補一筆來源序號 1 的紀錄；可選欄位（工作表/編碼/分隔符）為 NULL（第 1 版未記錄）
        Assert.Equal(2, await env.ScalarAsync("SELECT COUNT(*) FROM import_batch_source"));
        Assert.Equal(3, await env.ScalarAsync(
            """
            SELECT row_count FROM import_batch_source
            WHERE batch_id='batch-gl-1' AND source_no=1 AND source_file_name='v1.xlsx'
              AND sheet_name IS NULL AND encoding IS NULL AND delimiter IS NULL
            """));
    }

    [Fact]
    public async Task EnsureCreated_RunTwice_MigrationIsIdempotent()
    {
        using var env = new MigratedEnv();

        await env.Database.EnsureCreatedAsync(env.ProjectId, CancellationToken.None);
        await env.Database.EnsureCreatedAsync(env.ProjectId, CancellationToken.None);

        Assert.Equal("10", await env.TextAsync("SELECT value FROM schema_info WHERE key='schema_version'"));
        Assert.Equal(2, await env.ScalarAsync("SELECT COUNT(*) FROM import_batch_source"));
        Assert.Equal(3, await env.ScalarAsync("SELECT COUNT(*) FROM staging_gl_raw_row"));
    }

    [Fact]
    public async Task Migration_PreservesInfSamplingKeys_ThroughProjection()
    {
        using var env = new MigratedEnv();
        await env.Database.EnsureCreatedAsync(env.ProjectId, CancellationToken.None);

        // metamorphic 不變量：INF 抽樣以 target.source_row_number 排序——
        // 遷移後投影出的鍵集合必須與第 1 版的 row_number 集合 {2,5,9} 完全相等（樣本不變）
        var spec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.DocNum] = "doc",
                [GlMappingKeys.PostDate] = "date",
                [GlMappingKeys.AccNum] = "acc",
                [GlMappingKeys.AccName] = "name",
                [GlMappingKeys.Description] = "desc",
                [GlMappingKeys.DebitAmount] = "debit",
                [GlMappingKeys.CreditAmount] = "credit"
            },
            GlAmountMode.DualAmount);

        var glRepo = new LocalGlRepository(env.Database);
        var result = await glRepo.ProjectStagingToTargetAsync(
            env.ProjectId, "batch-gl-1", spec, 10_000, DateParseOptions.Default, CancellationToken.None);

        Assert.Empty(result.Errors);
        Assert.Equal(3, result.ProjectedRowCount);
        Assert.Equal(3, await env.ScalarAsync(
            "SELECT COUNT(*) FROM target_gl_entry WHERE source_row_number IN (2,5,9)"));
        Assert.Equal(3, await env.ScalarAsync(
            "SELECT COUNT(DISTINCT source_row_number) FROM target_gl_entry"));
    }

    /* ---- 第 2 版 → 第 3 版（規則命名更名 + 科目配對） ----------------------- */

    /// <summary>第 2 版的凍結快照：只含 v2→v3 遷移涉及的表與資料。</summary>
    private const string V2SchemaAndData =
        """
        CREATE TABLE schema_info (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        INSERT INTO schema_info (key, value) VALUES ('schema_version', '2');

        CREATE TABLE import_batch (
            batch_id         TEXT PRIMARY KEY,
            dataset_kind     TEXT NOT NULL CHECK (dataset_kind IN ('gl','tb')),
            source_file_path TEXT NOT NULL,
            source_file_name TEXT NOT NULL,
            imported_utc     TEXT NOT NULL,
            row_count        INTEGER NOT NULL DEFAULT 0,
            columns_json     TEXT NOT NULL
        );
        INSERT INTO import_batch VALUES
            ('batch-gl-1', 'gl', 'C:\v2.xlsx', 'v2.xlsx', '2026-06-10T00:00:00.0000000+00:00', 9,
             '["doc","date","acc"]');

        CREATE TABLE result_rule_run (
            run_id        TEXT PRIMARY KEY,
            run_kind      TEXT NOT NULL CHECK (run_kind IN ('validate','prescreen')),
            generated_utc TEXT NOT NULL,
            summary_json  TEXT NOT NULL
        );
        INSERT INTO result_rule_run VALUES
            ('run-old-1', 'validate', '2026-06-10T01:00:00.0000000+00:00', '{"v1":{"status":"V"}}'),
            ('run-old-2', 'prescreen', '2026-06-10T01:00:00.0000000+00:00', '{"r2":{"count":30}}');

        CREATE TABLE result_validation_v3_sample (
            run_id          TEXT NOT NULL,
            entry_id        INTEGER NOT NULL,
            document_number TEXT NULL,
            line_item       TEXT NULL,
            PRIMARY KEY (run_id, entry_id)
        );
        INSERT INTO result_validation_v3_sample VALUES ('run-old-1', 1, 'D1', NULL);

        CREATE TABLE config_filter_scenario (
            position        INTEGER PRIMARY KEY,
            name            TEXT NOT NULL,
            rationale       TEXT NOT NULL,
            definition_json TEXT NOT NULL,
            saved_utc       TEXT NOT NULL
        );
        INSERT INTO config_filter_scenario VALUES
            (1, '舊鍵情境', '驗證遷移翻譯',
             '{"name":"舊鍵情境","rationale":"驗證遷移翻譯","groups":[{"join":"AND","rules":[{"join":"AND","type":"prescreen","prescreenKey":"r2"},{"join":"OR","type":"prescreen","prescreenKey":"r8post"},{"join":"AND","type":"prescreen","prescreenKey":"descNull"}]}]}',
             '2026-06-10T02:00:00.0000000+00:00'),
            (2, '無預篩選情境', '不該被改動',
             '{"name":"無預篩選情境","rationale":"不該被改動","groups":[{"join":"AND","rules":[{"join":"AND","type":"drCrOnly","drCr":"debit"}]}]}',
             '2026-06-10T02:00:00.0000000+00:00');
        """;

    private sealed class V2MigratedEnv : IDisposable
    {
        private readonly TempProjectRoot _root = new();

        public V2MigratedEnv()
        {
            Folder = new JetProjectFolder(_root.Path);
            Database = new SqliteProjectDatabase(Folder);
            ProjectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Folder.GetProjectDirectory(ProjectId));

            using var connection = Database.CreateConnection(ProjectId);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = V2SchemaAndData;
            command.ExecuteNonQuery();
        }

        public JetProjectFolder Folder { get; }
        public SqliteProjectDatabase Database { get; }
        public string ProjectId { get; }

        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = Database.CreateConnection(ProjectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var result = await command.ExecuteScalarAsync();
            return result is DBNull or null ? 0L : Convert.ToInt64(result);
        }

        public async Task<string> TextAsync(string sql)
        {
            await using var connection = Database.CreateConnection(ProjectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (string)(await command.ExecuteScalarAsync())!;
        }

        public void Dispose() => _root.Dispose();
    }

    [Fact]
    public async Task EnsureCreated_V2Database_ClearsOldKeyRuleRuns()
    {
        using var env = new V2MigratedEnv();

        await env.Database.EnsureCreatedAsync(env.ProjectId, CancellationToken.None);

        // 舊鍵摘要為衍生資料：清除不翻譯（重跑即恢復且結果相同——INF 抽樣 seed 固定）。
        Assert.Equal("10", await env.TextAsync("SELECT value FROM schema_info WHERE key='schema_version'"));
        Assert.Equal(0, await env.ScalarAsync("SELECT COUNT(*) FROM result_rule_run"));
        Assert.Equal(0, await env.ScalarAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='result_validation_v3_sample'"));
        Assert.Equal(1, await env.ScalarAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='result_inf_sampling_test_sample'"));
    }

    [Fact]
    public async Task EnsureCreated_V2Database_TranslatesScenarioPrescreenKeys()
    {
        using var env = new V2MigratedEnv();

        await env.Database.EnsureCreatedAsync(env.ProjectId, CancellationToken.None);

        // 篩選情境是使用者著作的組態：逐鍵翻譯保留（r2→suspiciousKeywords、
        // r8post→holidayPosting、descNull→blankDescription），其餘內容原樣。
        var translated = await env.TextAsync(
            "SELECT definition_json FROM config_filter_scenario WHERE position = 1");
        Assert.Contains("\"suspiciousKeywords\"", translated);
        Assert.Contains("\"holidayPosting\"", translated);
        Assert.Contains("\"blankDescription\"", translated);
        Assert.DoesNotContain("\"r2\"", translated);
        Assert.DoesNotContain("\"r8post\"", translated);
        Assert.DoesNotContain("\"descNull\"", translated);
        Assert.Contains("舊鍵情境", translated);

        var untouched = await env.TextAsync(
            "SELECT definition_json FROM config_filter_scenario WHERE position = 2");
        Assert.Contains("\"drCrOnly\"", untouched);
    }

    [Fact]
    public async Task EnsureCreated_V2Database_RebuildsImportBatchCheckAndKeepsRows()
    {
        using var env = new V2MigratedEnv();

        await env.Database.EnsureCreatedAsync(env.ProjectId, CancellationToken.None);

        // CHECK 重建保資料：既有批次原樣保留，且新 CHECK 接受 account_mapping。
        Assert.Equal(9, await env.ScalarAsync(
            "SELECT row_count FROM import_batch WHERE batch_id='batch-gl-1' AND dataset_kind='gl'"));
        Assert.Equal(1, await env.ScalarAsync(
            """
            INSERT INTO import_batch VALUES
                ('batch-am-1', 'account_mapping', 'C:\am.xlsx', 'am.xlsx',
                 '2026-06-11T00:00:00.0000000+00:00', 1, '["a","b","c"]');
            SELECT changes();
            """));
    }

    [Fact]
    public async Task EnsureCreated_V2Database_MigrationIsIdempotent()
    {
        using var env = new V2MigratedEnv();

        await env.Database.EnsureCreatedAsync(env.ProjectId, CancellationToken.None);
        await env.Database.EnsureCreatedAsync(env.ProjectId, CancellationToken.None);

        Assert.Equal("10", await env.TextAsync("SELECT value FROM schema_info WHERE key='schema_version'"));
        Assert.Equal(2, await env.ScalarAsync("SELECT COUNT(*) FROM config_filter_scenario"));
        Assert.Equal(1, await env.ScalarAsync("SELECT COUNT(*) FROM import_batch"));
    }

    /* ---- 第 3 版 → 第 4 版(假日名稱欄) ----------------------- */

    /// <summary>第 3 版的凍結快照:只含 v3→v4 遷移涉及的表(staging_calendar_raw_day 無 day_name)。</summary>
    private const string V3SchemaAndData =
        """
        CREATE TABLE schema_info (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        INSERT INTO schema_info (key, value) VALUES ('schema_version', '3');

        CREATE TABLE staging_calendar_raw_day (
            day_type TEXT NOT NULL CHECK (day_type IN ('holiday','makeup')),
            date     TEXT NOT NULL,
            PRIMARY KEY (day_type, date)
        );
        INSERT INTO staging_calendar_raw_day (day_type, date) VALUES ('holiday', '2025-01-01');
        """;

    private sealed class V3MigratedEnv : IDisposable
    {
        private readonly TempProjectRoot _root = new();

        public V3MigratedEnv()
        {
            Folder = new JetProjectFolder(_root.Path);
            Database = new SqliteProjectDatabase(Folder);
            ProjectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Folder.GetProjectDirectory(ProjectId));

            using var connection = Database.CreateConnection(ProjectId);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = V3SchemaAndData;
            command.ExecuteNonQuery();
        }

        public JetProjectFolder Folder { get; }
        public SqliteProjectDatabase Database { get; }
        public string ProjectId { get; }

        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = Database.CreateConnection(ProjectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var result = await command.ExecuteScalarAsync();
            return result is DBNull or null ? 0L : Convert.ToInt64(result);
        }

        public async Task<string> TextAsync(string sql)
        {
            await using var connection = Database.CreateConnection(ProjectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (string)(await command.ExecuteScalarAsync())!;
        }

        public void Dispose() => _root.Dispose();
    }

    [Fact]
    public async Task EnsureCreated_V3Database_AddsCalendarDayNameAndBumpsTo4()
    {
        using var env = new V3MigratedEnv();

        await env.Database.EnsureCreatedAsync(env.ProjectId, CancellationToken.None);

        Assert.Equal("10", await env.TextAsync("SELECT value FROM schema_info WHERE key='schema_version'"));
        // day_name 欄存在
        Assert.Equal(1, await env.ScalarAsync(
            "SELECT COUNT(*) FROM pragma_table_info('staging_calendar_raw_day') WHERE name='day_name'"));
        // 既有資料保留
        Assert.Equal(1, await env.ScalarAsync(
            "SELECT COUNT(*) FROM staging_calendar_raw_day WHERE day_type='holiday' AND date='2025-01-01'"));
    }

    [Fact]
    public async Task EnsureCreated_V3Database_MigrationIsIdempotent()
    {
        using var env = new V3MigratedEnv();

        await env.Database.EnsureCreatedAsync(env.ProjectId, CancellationToken.None);
        await env.Database.EnsureCreatedAsync(env.ProjectId, CancellationToken.None);

        Assert.Equal("10", await env.TextAsync("SELECT value FROM schema_info WHERE key='schema_version'"));
        Assert.Equal(1, await env.ScalarAsync(
            "SELECT COUNT(*) FROM pragma_table_info('staging_calendar_raw_day') WHERE name='day_name'"));
    }

    /* ---- 第 4 版 → 第 5 版(傳票日期欄) ----------------------- */

    /// <summary>第 4 版的凍結快照:只含 v4→v5 遷移涉及的表(target_gl_entry 無 voucher_date)。</summary>
    private const string V4SchemaAndData =
        """
        CREATE TABLE schema_info (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        INSERT INTO schema_info (key, value) VALUES ('schema_version', '4');

        CREATE TABLE target_gl_entry (
            entry_id              INTEGER PRIMARY KEY AUTOINCREMENT,
            batch_id              TEXT NOT NULL,
            source_row_number     INTEGER NOT NULL,
            document_number       TEXT NULL,
            line_item             TEXT NULL,
            post_date             TEXT NULL,
            approval_date         TEXT NULL,
            account_code          TEXT NULL,
            account_name          TEXT NULL,
            document_description  TEXT NULL,
            source_module         TEXT NULL,
            created_by            TEXT NULL,
            approved_by           TEXT NULL,
            is_manual             INTEGER NULL,
            amount_scaled         INTEGER NOT NULL,
            debit_amount_scaled   INTEGER NOT NULL,
            credit_amount_scaled  INTEGER NOT NULL,
            dr_cr                 TEXT NOT NULL CHECK (dr_cr IN ('DEBIT','CREDIT'))
        );
        INSERT INTO target_gl_entry
            (batch_id, source_row_number, document_number, post_date, approval_date,
             account_code, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('batch-gl-1', 2, 'D1', '2025-01-01', '2025-01-02', '1101', 1000000, 1000000, 0, 'DEBIT');
        """;

    private sealed class V4MigratedEnv : IAsyncDisposable
    {
        private readonly TempProjectRoot _root = new();

        private V4MigratedEnv()
        {
            Folder = new JetProjectFolder(_root.Path);
            Database = new SqliteProjectDatabase(Folder);
            ProjectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Folder.GetProjectDirectory(ProjectId));

            using var connection = Database.CreateConnection(ProjectId);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = V4SchemaAndData;
            command.ExecuteNonQuery();
        }

        public JetProjectFolder Folder { get; }
        public SqliteProjectDatabase Database { get; }
        public string ProjectId { get; }

        public static async Task<V4MigratedEnv> CreateAsync()
        {
            var env = new V4MigratedEnv();
            await env.RunEnsureCreatedAsync();
            return env;
        }

        public Task RunEnsureCreatedAsync() =>
            Database.EnsureCreatedAsync(ProjectId, CancellationToken.None);

        public async Task<string> ReadVersionAsync()
        {
            await using var connection = Database.CreateConnection(ProjectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM schema_info WHERE key='schema_version'";
            return (string)(await command.ExecuteScalarAsync())!;
        }

        public async Task<bool> ColumnExistsAsync(string table, string column)
        {
            await using var connection = Database.CreateConnection(ProjectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
        }

        public ValueTask DisposeAsync()
        {
            _root.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task EnsureCreated_V4Database_AddsVoucherDateAndMigratesThroughV7()
    {
        await using var env = await V4MigratedEnv.CreateAsync();
        Assert.Equal("10", await env.ReadVersionAsync());
        Assert.True(await env.ColumnExistsAsync("target_gl_entry", "voucher_date"));
        Assert.True(await env.ColumnExistsAsync(
            "target_gl_entry",
            "line_item_numeric_sort_key"));
    }

    [Fact]
    public async Task V4Migration_IsIdempotent()
    {
        await using var env = await V4MigratedEnv.CreateAsync();
        await env.RunEnsureCreatedAsync(); // 第二次
        Assert.Equal("10", await env.ReadVersionAsync());
        Assert.True(await env.ColumnExistsAsync("target_gl_entry", "voucher_date"));
        Assert.True(await env.ColumnExistsAsync(
            "target_gl_entry",
            "line_item_numeric_sort_key"));
    }

    /* ---- 第 5 版 → 第 6 版（數值項次 ordinal sort key） ----------------------- */

    private const string V5SchemaAndData =
        """
        CREATE TABLE schema_info (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        INSERT INTO schema_info (key, value) VALUES ('schema_version', '5');

        CREATE TABLE target_gl_entry (
            entry_id              INTEGER PRIMARY KEY AUTOINCREMENT,
            batch_id              TEXT NOT NULL,
            source_row_number     INTEGER NOT NULL,
            document_number       TEXT NULL,
            line_item             TEXT NULL,
            post_date             TEXT NULL,
            approval_date         TEXT NULL,
            voucher_date          TEXT NULL,
            account_code          TEXT NULL,
            account_name          TEXT NULL,
            document_description  TEXT NULL,
            source_module         TEXT NULL,
            created_by            TEXT NULL,
            approved_by           TEXT NULL,
            is_manual             INTEGER NULL,
            amount_scaled         INTEGER NOT NULL,
            debit_amount_scaled   INTEGER NOT NULL,
            credit_amount_scaled  INTEGER NOT NULL,
            dr_cr                 TEXT NOT NULL CHECK (dr_cr IN ('DEBIT','CREDIT'))
        );

        INSERT INTO target_gl_entry
            (batch_id, source_row_number, document_number, line_item, post_date,
             approval_date, account_code, amount_scaled, debit_amount_scaled,
             credit_amount_scaled, dr_cr)
        VALUES
            ('batch-v5', 2, 'DOC-V5', '7', '2025-01-01',
             '2025-01-02', '1101', 1000000, 1000000, 0, 'DEBIT');
        """;

    [Fact]
    public async Task EnsureCreated_V5Database_AddsNumericSortKeyWithoutGuessingBackfill()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var seedCommand = connection.CreateCommand();
            seedCommand.CommandText = V5SchemaAndData;
            await seedCommand.ExecuteNonQueryAsync();
        }

        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        await using var verify = database.CreateConnection(projectId);
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT value FROM schema_info WHERE key = 'schema_version'),
                (SELECT COUNT(*) FROM pragma_table_info('target_gl_entry')
                    WHERE name = 'line_item_numeric_sort_key'),
                (SELECT COUNT(*) FROM target_gl_entry
                    WHERE document_number = 'DOC-V5'
                      AND line_item = '7'
                      AND line_item_numeric_sort_key IS NULL);
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("10", reader.GetString(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(1, reader.GetInt64(2));
    }

    [Fact]
    public async Task DuckDbEnsureCreated_V5Database_AddsNumericSortKeyWithoutGuessingBackfill()
    {
        using var root = new TempProjectRoot();
        var database = new DuckDbProjectDatabase(new JetProjectFolder(root.Path));
        var projectId = Guid.NewGuid().ToString("N");
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText =
                """
                INSERT INTO target_gl_entry
                    (entry_id, batch_id, source_row_number, document_number, line_item,
                     line_item_numeric_sort_key, post_date, approval_date, account_code,
                     amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
                VALUES
                    (1, 'batch-v5', 2, 'DOC-V5', '7', 'old-key', '2025-01-01',
                     '2025-01-02', '1101', 1000000, 1000000, 0, 'DEBIT');
                DROP INDEX ix_target_gl_entry_doc;
                DROP INDEX ix_target_gl_entry_account;
                DROP INDEX ix_target_gl_entry_post_date;
                DROP INDEX ix_target_gl_entry_approval_date;
                ALTER TABLE target_gl_entry DROP COLUMN line_item_numeric_sort_key;
                UPDATE schema_info SET value = '5' WHERE key = 'schema_version';
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        await using var verify = database.CreateConnection(projectId);
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT value FROM schema_info WHERE key = 'schema_version'),
                (SELECT COUNT(*) FROM pragma_table_info('target_gl_entry')
                    WHERE name = 'line_item_numeric_sort_key'),
                (SELECT COUNT(*) FROM target_gl_entry
                    WHERE document_number = 'DOC-V5'
                      AND line_item = '7'
                      AND line_item_numeric_sort_key IS NULL);
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("10", reader.GetString(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(1, reader.GetInt64(2));
    }
}
