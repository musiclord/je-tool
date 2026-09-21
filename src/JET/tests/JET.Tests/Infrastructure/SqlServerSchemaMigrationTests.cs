using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// SQL Server schema 版本化遷移機制的驗收（控制面第七輪 §1；修 <see cref="SqlServerProjectDatabase.EnsureCreatedAsync"/>
/// 的 early-return 死路）。全部 [SqlServerFact] 閘控（連隔離庫 JET_Test，非 Express、≥ 2022）。
/// 既有 schema 會 additive 升到 v8；測試同時保留早期「降版 schema」守衛，並以直接建立、未經 current
/// SchemaSql 的 frozen-v6 fixture 驗收完整 v6→v7 shape/data/result migration，再接續 v7→v8 audit table。
/// SQLite 版本鏈見 <c>SchemaMigrationTests</c>。
/// </summary>
public sealed class SqlServerSchemaMigrationTests
{
    private const string SingleDb = "JET_Test";

    // 漂移守衛（純單元、不需 SQL Server）：fresh insert 與 transaction 最後的 bump
    // 都必須等於 SchemaVersion 常數。日後只 bump 常數、忘改任一處字面值，
    // 既有 schema 會在每次觸碰時重跑整段 DDL（讀 < 現行 → 遷移 → 寫回舊值 → 又 < 現行 → 無窮遷移）。
    // 那種 bump 下本測試即紅，逼開發者同步兩端。對抗驗收（第七輪 check 3）發現此脫鉤後補。
    [Fact]
    public void SchemaSql_WritesTheCurrentSchemaVersionConstant()
    {
        var version = SqlServerProjectDatabase.SchemaVersion;
        Assert.Contains($"'schema_version', '{version}')", SqlServerProjectDatabase.SchemaSql);
        Assert.Contains(
            $"SET [value] = '{version}' WHERE [key] = 'schema_version'",
            SqlServerProjectDatabase.BumpSchemaVersionSql);
    }

    [SqlServerFact]
    public async Task EnsureCreated_OnDowngradedSchema_RestoresShape_BumpsVersion_PreservesData_Idempotent()
    {
        var baseConn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (baseConn is null) { return; }

        var options = new SqlServerConnectionOptions(baseConn, SingleDb);
        var database = new SqlServerProjectDatabase(options);
        var projectId = $"mig-{Guid.NewGuid():N}";
        var q = SqlServerProjectSchema.QualifierFor(projectId); // 例 [prj_xxx].

        try
        {
            // 建現行 v8 schema，塞一列可辨識的資料（證遷移保留既有列）。
            await database.EnsureCreatedAsync(projectId, CancellationToken.None);
            await ExecuteAsync(baseConn,
                $"INSERT INTO {q}target_gl_entry " +
                "(batch_id, source_row_number, document_number, line_item, account_code, voucher_date, " +
                " amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr) " +
                "VALUES ('batch-mig-1', 2, 'DOC-MIG', '7', 'ACC-9', '2025-01-03', 1000000, 1000000, 0, 'DEBIT');");

            // 合成 v5：卸 step4-1 numeric sort key，再把版本改回 5。
            await ExecuteAsync(
                baseConn,
                $"ALTER TABLE {q}target_gl_entry DROP COLUMN line_item_numeric_sort_key;");
            await ExecuteAsync(
                baseConn,
                $"UPDATE {q}schema_info SET [value] = '5' WHERE [key] = 'schema_version';");

            // 遷移前（紅燈）：欄已缺、版本為 5。
            Assert.Equal(
                0,
                await ColumnPresentAsync(
                    baseConn,
                    q,
                    "target_gl_entry",
                    "line_item_numeric_sort_key"));
            Assert.Equal("5", await VersionAsync(baseConn, q));

            // 觸發版本化遷移機制。
            await database.EnsureCreatedAsync(projectId, CancellationToken.None);

            // 遷移後：形狀補齊、版本回現行、既有列保留；舊列不猜測回填 key。
            Assert.Equal(
                1,
                await ColumnPresentAsync(
                    baseConn,
                    q,
                    "target_gl_entry",
                    "line_item_numeric_sort_key"));
            Assert.Equal(SqlServerProjectDatabase.SchemaVersion, await VersionAsync(baseConn, q));
            Assert.Equal(1, await ScalarLongAsync(baseConn,
                $"SELECT COUNT(*) FROM {q}target_gl_entry WHERE document_number = 'DOC-MIG' AND account_code = 'ACC-9';"));
            Assert.Equal(1, await ScalarLongAsync(baseConn,
                $"SELECT COUNT(*) FROM {q}target_gl_entry WHERE document_number = 'DOC-MIG' AND line_item = '7' AND line_item_numeric_sort_key IS NULL;"));

            // 冪等：重跑不拋、版本仍為現行版、欄仍在、既有列不變。
            await database.EnsureCreatedAsync(projectId, CancellationToken.None);
            Assert.Equal(SqlServerProjectDatabase.SchemaVersion, await VersionAsync(baseConn, q));
            Assert.Equal(
                1,
                await ColumnPresentAsync(
                    baseConn,
                    q,
                    "target_gl_entry",
                    "line_item_numeric_sort_key"));
            Assert.Equal(1, await ScalarLongAsync(baseConn,
                $"SELECT COUNT(*) FROM {q}target_gl_entry WHERE document_number = 'DOC-MIG';"));
        }
        finally
        {
            await database.DeleteAsync(projectId, CancellationToken.None);
        }
    }

    [SqlServerFact]
    public async Task EnsureCreated_OnV7_AddsAuditTableWithoutClearingExistingResults()
    {
        var baseConn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (baseConn is null) { return; }

        var options = new SqlServerConnectionOptions(baseConn, SingleDb);
        var database = new SqlServerProjectDatabase(options);
        var projectId = $"v8-audit-{Guid.NewGuid():N}";
        var q = SqlServerProjectSchema.QualifierFor(projectId);

        try
        {
            await database.EnsureCreatedAsync(projectId, CancellationToken.None);
            await ExecuteAsync(
                baseConn,
                $"DROP TABLE {q}audit_event_log; " +
                $"UPDATE {q}schema_info SET [value]='7' WHERE [key]='schema_version'; " +
                $"INSERT INTO {q}result_rule_run (run_id, run_kind, generated_utc, summary_json) " +
                "VALUES ('v7-run', 'validate', '2026-08-19T00:00:00.0000000+00:00', '{}');");

            await database.EnsureCreatedAsync(projectId, CancellationToken.None);

            Assert.Equal("11", await VersionAsync(baseConn, q));
            Assert.Equal(1, await TablePresentAsync(baseConn, q, "audit_event_log"));
            Assert.Equal(1, await ScalarLongAsync(
                baseConn,
                $"SELECT COUNT(*) FROM {q}result_rule_run WHERE run_id='v7-run';"));
        }
        finally
        {
            await database.DeleteAsync(projectId, CancellationToken.None);
        }
    }

    [SqlServerFact]
    public async Task EnsureCreated_OnFrozenV6_MigratesCompleteV7State_AndNewInstanceReopensIdempotently()
    {
        var baseConn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (baseConn is null) { return; }

        var options = new SqlServerConnectionOptions(baseConn, SingleDb);
        var database = new SqlServerProjectDatabase(options);
        var projectId = $"v7-success-{Guid.NewGuid():N}";
        var schema = SqlServerProjectSchema.For(projectId);
        var q = SqlServerProjectSchema.QualifierFor(projectId);

        try
        {
            await database.EnsureDatabaseReadyAsync(CancellationToken.None);
            await CreateFrozenV6Async(baseConn, schema, q);

            await database.EnsureCreatedAsync(projectId, CancellationToken.None);

            Assert.Equal("11", await VersionAsync(baseConn, q));
            foreach (var column in new[] { "posting_status", "is_effective", "exclusion_reason" })
            {
                Assert.Equal(1, await ColumnPresentAsync(baseConn, q, "target_gl_entry", column));
            }
            foreach (var column in new[]
                     {
                         "raw_row_count", "effective_row_count", "excluded_by_period_count",
                         "excluded_by_posting_status_count", "effective_debit_scaled", "effective_credit_scaled"
                     })
            {
                Assert.Equal(1, await ColumnPresentAsync(baseConn, q, "gl_control_total", column));
            }
            Assert.Equal(1, await ColumnPresentAsync(baseConn, q, "config_field_mapping", "format_version"));
            Assert.Equal(1, await ColumnPresentAsync(baseConn, q, "config_field_mapping", "options_json"));
            Assert.Equal(1, await ColumnPresentAsync(baseConn, q, "target_account_mapping", "category_id"));
            foreach (var table in new[]
                     {
                         "config_gl_rde_field", "target_gl_rde_value", "config_account_taxonomy",
                         "config_result_stale_state"
                     })
            {
                Assert.Equal(1, await TablePresentAsync(baseConn, q, table));
            }

            Assert.Equal(1, await ScalarLongAsync(baseConn,
                $"SELECT COUNT(*) FROM {q}config_field_mapping WHERE dataset_kind='gl' "
                + "AND format_version=1 AND options_json IS NULL;"));
            Assert.Equal(1, await ScalarLongAsync(baseConn,
                $"SELECT COUNT(*) FROM {q}target_gl_entry WHERE document_number='DOC-V6' "
                + "AND posting_status IS NULL AND is_effective IS NULL AND exclusion_reason IS NULL;"));
            Assert.Equal(1, await ScalarLongAsync(baseConn,
                $"SELECT COUNT(*) FROM {q}target_account_mapping WHERE standardized_category='Revenue' "
                + "AND category_id='builtin.revenue';"));
            Assert.Equal(5, await ScalarLongAsync(baseConn,
                $"SELECT COUNT(*) FROM {q}config_account_taxonomy WHERE "
                + "(category_id='builtin.revenue' AND label='Revenue' AND ordinal=0 AND semantic_role='revenue' AND is_builtin=1 AND revision=1) OR "
                + "(category_id='builtin.receivables' AND label='Receivables' AND ordinal=1 AND semantic_role='receivables' AND is_builtin=1 AND revision=1) OR "
                + "(category_id='builtin.cash' AND label='Cash' AND ordinal=2 AND semantic_role='cash' AND is_builtin=1 AND revision=1) OR "
                + "(category_id='builtin.receipt_in_advance' AND label='Receipt in advance' AND ordinal=3 AND semantic_role='receipt_in_advance' AND is_builtin=1 AND revision=1) OR "
                + "(category_id='builtin.others' AND label='Others' AND ordinal=4 AND semantic_role='others' AND is_builtin=1 AND revision=1);"));
            Assert.Equal(1, await ScalarLongAsync(baseConn,
                $"SELECT COUNT(*) FROM {q}config_filter_scenario WHERE position=1 "
                + "AND CHARINDEX('\"debitCategory\":\"Cash\"', definition_json) > 0 "
                + "AND CHARINDEX('\"creditCategory\":\"Revenue\"', definition_json) > 0 "
                + "AND CHARINDEX('\"debitCategoryIds\":[\"builtin.cash\"]', definition_json) > 0 "
                + "AND CHARINDEX('\"creditCategoryIds\":[\"builtin.revenue\"]', definition_json) > 0;"));
            var migratedScenario = await ScalarTextAsync(
                baseConn,
                $"SELECT definition_json FROM {q}config_filter_scenario WHERE position=1;");
            Assert.Contains(
                "\"logicVersion\":\"filter-2026-08-04-v6\"",
                migratedScenario,
                StringComparison.Ordinal);
            Assert.False(RuleLogicVersions.IsCurrent(new SavedFilterScenario(
                1,
                "legacy pair",
                "migration oracle",
                migratedScenario,
                new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero))));
            Assert.Equal(0, await ScalarLongAsync(baseConn, $"SELECT COUNT(*) FROM {q}result_rule_run;"));
            Assert.Equal(0, await ScalarLongAsync(baseConn, $"SELECT COUNT(*) FROM {q}result_inf_sampling_test_sample;"));
            Assert.Equal(0, await ScalarLongAsync(baseConn, $"SELECT COUNT(*) FROM {q}result_filter_run;"));
            Assert.Equal(1, await ScalarLongAsync(baseConn,
                $"SELECT COUNT(*) FROM {q}config_result_stale_state WHERE singleton=1 "
                + "AND validation_stale=1 AND prescreen_stale=1 AND filter_stale=1;"));
            Assert.Equal(3, await ScalarLongAsync(baseConn,
                "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'" + q + "target_gl_rde_value') "
                + "AND name IN ('ix_target_gl_rde_value_text','ix_target_gl_rde_value_date','ix_target_gl_rde_value_amount');"));

            var reopened = new SqlServerProjectDatabase(options);
            await reopened.EnsureCreatedAsync(projectId, CancellationToken.None);
            Assert.Equal("11", await VersionAsync(baseConn, q));
            Assert.Equal(1, await ScalarLongAsync(baseConn,
                $"SELECT COUNT(*) FROM {q}target_gl_entry WHERE document_number='DOC-V6';"));
            Assert.Equal(1, await ScalarLongAsync(baseConn,
                $"SELECT COUNT(*) FROM {q}config_filter_scenario WHERE position=1;"));
        }
        finally
        {
            await database.DeleteAsync(projectId, CancellationToken.None);
        }
    }

    [SqlServerFact]
    public async Task EnsureCreated_AtCurrentVersion_IsNoOp_DoesNotReRunGuardedDdl()
    {
        var baseConn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (baseConn is null) { return; }

        var options = new SqlServerConnectionOptions(baseConn, SingleDb);
        var database = new SqlServerProjectDatabase(options);
        var projectId = $"noop-{Guid.NewGuid():N}";
        var q = SqlServerProjectSchema.QualifierFor(projectId);

        try
        {
            await database.EnsureCreatedAsync(projectId, CancellationToken.None);

            // 卸一欄但「不」改版本（維持現行 7）：證等於現行時走 no-op、不重跑守欄 DDL。
            await ExecuteAsync(
                baseConn,
                $"ALTER TABLE {q}target_gl_entry DROP COLUMN line_item_numeric_sort_key;");
            Assert.Equal(SqlServerProjectDatabase.SchemaVersion, await VersionAsync(baseConn, q));
            Assert.Equal(
                0,
                await ColumnPresentAsync(
                    baseConn,
                    q,
                    "target_gl_entry",
                    "line_item_numeric_sort_key"));

            await database.EnsureCreatedAsync(projectId, CancellationToken.None);

            // 版本＝現行 → 讀 schema_info、比對 SchemaVersion 常數後 no-op：欄仍缺（守欄 DDL 未重跑）＝冪等不重複 ALTER。
            // 這同時證 SchemaVersion 常數確有真實讀者（非死碼）——否則此路不會停在 no-op。
            Assert.Equal(
                0,
                await ColumnPresentAsync(
                    baseConn,
                    q,
                    "target_gl_entry",
                    "line_item_numeric_sort_key"));
            Assert.Equal(SqlServerProjectDatabase.SchemaVersion, await VersionAsync(baseConn, q));
        }
        finally
        {
            await database.DeleteAsync(projectId, CancellationToken.None);
        }
    }

    [SqlServerFact]
    public async Task EnsureCreated_FrozenV6FaultAfterResetBeforeBump_RollsBackShapeDataAndVersion()
    {
        var baseConn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (baseConn is null) { return; }

        var options = new SqlServerConnectionOptions(baseConn, SingleDb);
        var database = new SqlServerProjectDatabase(options);
        var projectId = $"v7-rollback-{Guid.NewGuid():N}";
        var schema = SqlServerProjectSchema.For(projectId);
        var q = SqlServerProjectSchema.QualifierFor(projectId);

        try
        {
            await database.EnsureDatabaseReadyAsync(CancellationToken.None);
            await CreateFrozenV6Async(baseConn, schema, q);

            database.MigrationFaultHookForTests = stage =>
            {
                if (stage == "after-v7-result-reset")
                {
                    throw new InvalidOperationException("injected SQL Server v7 migration fault");
                }
            };

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => database.EnsureCreatedAsync(projectId, CancellationToken.None));

            Assert.Equal("6", await VersionAsync(baseConn, q));
            Assert.Equal(0, await ColumnPresentAsync(baseConn, q, "target_gl_entry", "posting_status"));
            Assert.Equal(0, await ColumnPresentAsync(baseConn, q, "config_field_mapping", "format_version"));
            Assert.Equal(0, await ColumnPresentAsync(baseConn, q, "target_account_mapping", "category_id"));
            Assert.Equal(0, await TablePresentAsync(baseConn, q, "config_account_taxonomy"));
            Assert.Equal(0, await TablePresentAsync(baseConn, q, "config_result_stale_state"));
            Assert.Equal(2, await ScalarLongAsync(baseConn, $"SELECT COUNT(*) FROM {q}result_rule_run;"));
            Assert.Equal(1, await ScalarLongAsync(baseConn, $"SELECT COUNT(*) FROM {q}result_inf_sampling_test_sample;"));
            Assert.Equal(1, await ScalarLongAsync(baseConn, $"SELECT COUNT(*) FROM {q}result_filter_run;"));
            Assert.Equal(0, await ScalarLongAsync(
                baseConn,
                $"SELECT COUNT(*) FROM {q}config_filter_scenario WHERE definition_json LIKE '%CategoryIds%';"));
        }
        finally
        {
            database.MigrationFaultHookForTests = null;
            await database.DeleteAsync(projectId, CancellationToken.None);
        }
    }

    // ---- 對 JET_Test 的最小 raw helper（直接下 schema-qualified SQL 驗形狀/資料/版本） ----

    private static async Task CreateFrozenV6Async(
        string baseConnectionString,
        string schema,
        string qualifier)
    {
        // CREATE SCHEMA 必須是獨立 batch；fixture DDL 是直接 frozen-v6 shape，不經 current SchemaSql。
        await ExecuteAsync(baseConnectionString, $"CREATE SCHEMA [{schema}];");
        await ExecuteAsync(
            baseConnectionString,
            $$"""
            CREATE TABLE {{qualifier}}schema_info (
                [key] NVARCHAR(450) COLLATE Latin1_General_BIN2 PRIMARY KEY,
                [value] NVARCHAR(MAX) NOT NULL);
            INSERT INTO {{qualifier}}schema_info VALUES ('schema_version', '6');

            CREATE TABLE {{qualifier}}config_field_mapping (
                dataset_kind NVARCHAR(20) COLLATE Latin1_General_BIN2 PRIMARY KEY
                    CHECK (dataset_kind IN ('gl','tb')),
                mapping_json NVARCHAR(MAX) NOT NULL,
                mode_name NVARCHAR(40) NOT NULL,
                source_batch_id NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                committed_utc NVARCHAR(40) NOT NULL);
            INSERT INTO {{qualifier}}config_field_mapping VALUES
                ('gl', '{"docNum":"Document Number"}', 'dualAmount', 'batch-v6', '2026-08-01T00:00:00+00:00');

            CREATE TABLE {{qualifier}}target_gl_entry (
                entry_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                batch_id NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                source_row_number BIGINT NOT NULL,
                document_number NVARCHAR(450) COLLATE Latin1_General_BIN2 NULL,
                line_item NVARCHAR(400) NULL,
                line_item_numeric_sort_key NVARCHAR(40) COLLATE Latin1_General_BIN2 NULL,
                post_date NVARCHAR(32) COLLATE Latin1_General_BIN2 NULL,
                approval_date NVARCHAR(32) COLLATE Latin1_General_BIN2 NULL,
                voucher_date NVARCHAR(32) COLLATE Latin1_General_BIN2 NULL,
                account_code NVARCHAR(450) COLLATE Latin1_General_BIN2 NULL,
                account_name NVARCHAR(400) NULL,
                document_description NVARCHAR(MAX) NULL,
                source_module NVARCHAR(400) NULL,
                created_by NVARCHAR(400) COLLATE Latin1_General_BIN2 NULL,
                approved_by NVARCHAR(400) COLLATE Latin1_General_BIN2 NULL,
                is_manual INT NULL,
                amount_scaled BIGINT NOT NULL,
                debit_amount_scaled BIGINT NOT NULL,
                credit_amount_scaled BIGINT NOT NULL,
                dr_cr NVARCHAR(10) COLLATE Latin1_General_BIN2 NOT NULL
                    CHECK (dr_cr IN ('DEBIT','CREDIT')));
            INSERT INTO {{qualifier}}target_gl_entry
                (batch_id, source_row_number, document_number, account_code,
                 amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
            VALUES ('batch-v6', 2, 'DOC-V6', '4101', -10000, 0, 10000, 'CREDIT');

            CREATE TABLE {{qualifier}}gl_control_total (
                singleton INT PRIMARY KEY CHECK (singleton = 1),
                source_row_count BIGINT NOT NULL,
                target_row_count BIGINT NOT NULL,
                target_debit_scaled BIGINT NOT NULL,
                target_credit_scaled BIGINT NOT NULL);
            INSERT INTO {{qualifier}}gl_control_total VALUES (1, 1, 1, 0, 10000);

            CREATE TABLE {{qualifier}}target_account_mapping (
                mapping_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                batch_id NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                source_row_number INT NOT NULL,
                account_code NVARCHAR(450) COLLATE Latin1_General_BIN2 NOT NULL,
                account_name NVARCHAR(400) NULL,
                standardized_category NVARCHAR(40) COLLATE Latin1_General_BIN2 NOT NULL
                    CHECK (standardized_category IN ('Revenue','Receivables','Cash','Receipt in advance','Others')));
            INSERT INTO {{qualifier}}target_account_mapping
                (batch_id, source_row_number, account_code, account_name, standardized_category)
            VALUES ('batch-am-v6', 2, '4101', 'Revenue account', 'Revenue');

            CREATE TABLE {{qualifier}}config_filter_scenario (
                position INT PRIMARY KEY,
                name NVARCHAR(400) NOT NULL,
                rationale NVARCHAR(MAX) NOT NULL,
                definition_json NVARCHAR(MAX) NOT NULL,
                saved_utc NVARCHAR(40) NOT NULL);
            INSERT INTO {{qualifier}}config_filter_scenario VALUES
                (1, 'legacy pair', 'migration oracle',
                 '{"populationScope":"auditPeriod","logicVersion":"filter-2026-08-04-v6","groups":[{"rules":[{"type":"accountPair","debitCategory":"Cash","creditCategory":"Revenue"}]}]}',
                 '2026-08-01T00:00:00+00:00');

            CREATE TABLE {{qualifier}}result_rule_run (
                run_id NVARCHAR(64) COLLATE Latin1_General_BIN2 PRIMARY KEY,
                run_kind NVARCHAR(20) COLLATE Latin1_General_BIN2 NOT NULL,
                generated_utc NVARCHAR(40) NOT NULL,
                summary_json NVARCHAR(MAX) NOT NULL);
            INSERT INTO {{qualifier}}result_rule_run VALUES
                ('validation-v6', 'validate', '2026-08-01T00:00:00+00:00', '{"resultRef":{"logicVersion":"validation-2026-07-13-v2"} }'),
                ('prescreen-v6', 'prescreen', '2026-08-01T00:00:00+00:00', '{"resultRef":{"logicVersion":"prescreen-2026-07-11-v2"} }');

            CREATE TABLE {{qualifier}}result_inf_sampling_test_sample (
                run_id NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                entry_id BIGINT NOT NULL,
                document_number NVARCHAR(450) COLLATE Latin1_General_BIN2 NULL,
                line_item NVARCHAR(400) NULL,
                PRIMARY KEY (run_id, entry_id));
            INSERT INTO {{qualifier}}result_inf_sampling_test_sample
                VALUES ('validation-v6', 1, 'DOC-V6', NULL);

            CREATE TABLE {{qualifier}}result_filter_run (
                scenario_position INT NOT NULL,
                entry_id BIGINT NOT NULL,
                PRIMARY KEY (scenario_position, entry_id));
            INSERT INTO {{qualifier}}result_filter_run VALUES (1, 1);
            """);
    }

    private static string SingleDbConnectionString(string baseConnectionString) =>
        new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = SingleDb }.ConnectionString;

    private static async Task ExecuteAsync(string baseConnectionString, string sql)
    {
        await using var connection = new SqlConnection(SingleDbConnectionString(baseConnectionString));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(string baseConnectionString, string sql)
    {
        await using var connection = new SqlConnection(SingleDbConnectionString(baseConnectionString));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    private static async Task<string> ScalarTextAsync(string baseConnectionString, string sql)
    {
        await using var connection = new SqlConnection(SingleDbConnectionString(baseConnectionString));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> VersionAsync(string baseConnectionString, string qualifier)
    {
        await using var connection = new SqlConnection(SingleDbConnectionString(baseConnectionString));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT [value] FROM {qualifier}schema_info WHERE [key] = 'schema_version';";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    // COL_LENGTH 回欄長度或 NULL：欄存在回 1、不存在回 0（qualifier 內含中括號，COL_LENGTH 接受）。
    private static Task<long> ColumnPresentAsync(string baseConnectionString, string qualifier, string table, string column) =>
        ScalarLongAsync(baseConnectionString,
            $"SELECT CASE WHEN COL_LENGTH('{qualifier}{table}', '{column}') IS NULL THEN 0 ELSE 1 END;");

    private static Task<long> TablePresentAsync(string baseConnectionString, string qualifier, string table) =>
        ScalarLongAsync(baseConnectionString,
            $"SELECT CASE WHEN OBJECT_ID(N'{qualifier}{table}', N'U') IS NULL THEN 0 ELSE 1 END;");
}
