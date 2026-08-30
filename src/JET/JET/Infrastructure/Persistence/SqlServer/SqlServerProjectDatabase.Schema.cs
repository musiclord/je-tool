using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

public sealed partial class SqlServerProjectDatabase
{
    // 目前 schema 版本。Fresh schema 由 SchemaSql 直接寫現行版；既有 schema 的版本則只在
    // MigrateExistingSchemaToCurrentAsync 完成 shape、data rewrite 與結果失效後最後寫回。
    internal const string SchemaVersion = "9";

    internal const string BumpSchemaVersionSql =
        "UPDATE {s}.schema_info SET [value] = '9' WHERE [key] = 'schema_version';";

    // SQL Server compiles a batch before executing its ALTER TABLE statements, so the v6 backfill must run
    // as a separate command after SchemaSql has added category_id. It remains inside the migration transaction.
    internal const string BackfillAccountMappingCategoryIdsSql =
        """
        UPDATE {s}.target_account_mapping
        SET category_id = CASE standardized_category
            WHEN 'Revenue' THEN 'builtin.revenue'
            WHEN 'Receivables' THEN 'builtin.receivables'
            WHEN 'Cash' THEN 'builtin.cash'
            WHEN 'Receipt in advance' THEN 'builtin.receipt_in_advance'
            WHEN 'Others' THEN 'builtin.others'
            ELSE category_id
        END
        WHERE category_id IS NULL;
        """;

    /// <summary>測試專用：在 v7 data rewrite 後及 result reset 後兩個交易邊界注入 fault。</summary>
    internal Action<string>? MigrationFaultHookForTests { get; set; }

    public async Task EnsureCreatedAsync(string projectId, CancellationToken cancellationToken)
    {
        var schema = SqlServerProjectSchema.For(projectId);

        // 1) 確保單庫存在 + dbo 控制面表就位(registry/access/app_config/audit_log)。
        await EnsureSingleDatabaseAndControlPlaneAsync(cancellationToken);

        await using var connection = CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        // 2) schema 已存在 → 讀版本，落後才跑守欄冪等遷移（不再無條件 early-return；死路修復見 §7）。
        bool schemaExists;
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT CASE WHEN SCHEMA_ID(@s) IS NULL THEN 0 ELSE 1 END;";
            exists.Parameters.AddWithValue("@s", schema);
            schemaExists = Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) == 1;
        }

        if (schemaExists)
        {
            await MigrateExistingSchemaToCurrentAsync(connection, projectId, cancellationToken);
            return;
        }

        // 3) CREATE SCHEMA → 在 schema 內建表(DDL {s}.),兩步同一交易。schema → 專案反查由 registry 承載,不再寫 map。
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var create = connection.CreateCommand())
        {
            create.Transaction = tx;
            // schema 名由 For() 衍生(白名單格式),非使用者輸入;CREATE SCHEMA 識別字不可參數化。
            create.CommandText = $"EXEC('CREATE SCHEMA [{schema}]');";
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var ddl = CreateCommand(connection, projectId, SchemaSql))
        {
            ddl.Transaction = tx;
            await ddl.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 既有 schema 的版本化遷移（控制面第七輪，修 <see cref="EnsureCreatedAsync"/> 的 early-return 死路；比照 SQLite
    /// 版本鏈的 SQL script migrator——guide §15.2「SQL script migrator、非 EF migrations」）。讀
    /// <c>{s}.schema_info</c> 的 <c>schema_version</c>：
    /// <list type="bullet">
    /// <item>＝現行 <see cref="SchemaVersion"/> → no-op（冪等：重跑既不重複 ALTER、也不開交易）。</item>
    /// <item>&lt; 現行（或版本列缺失）→ 於單一交易內重跑<b>守欄冪等</b>的 <see cref="SchemaSql"/>（每表
    /// <c>IF OBJECT_ID … IS NULL</c>、每欄 <c>IF COL_LENGTH … IS NULL ALTER … ADD</c>、每索引 <c>IF NOT EXISTS(sys.indexes)</c>），
    /// 補齊落後形狀並把 <c>schema_version</c> 回填現行；既有資料列不受影響（DDL 只補缺、不搬既有列）。</item>
    /// <item>&gt; 現行（理論上不會發生——schema 一路 forward-only）→ 不動。</item>
    /// </list>
    /// 既有 v5 schema 會以同一機制 additive 補入 line-item numeric sort key；之後 schema
    /// 版本 bump 仍可沿用這條路線惰性升級（碰到才升，不做啟動時全庫掃描）。
    /// <see cref="SchemaVersion"/> 常數自此有真實讀者（不再是 DDL 硬寫值的死碼）。
    /// </summary>
    private async Task MigrateExistingSchemaToCurrentAsync(
        SqlConnection connection, string projectId, CancellationToken cancellationToken)
    {
        var existingVersion = await ReadSchemaVersionAsync(connection, projectId, cancellationToken);
        var currentVersion = int.Parse(SchemaVersion, System.Globalization.CultureInfo.InvariantCulture);

        // 版本列缺失（null / 不可解析）視為 0（＜現行 → 需補建 schema_info 並升級）。
        var parsedExisting = int.TryParse(existingVersion, out var v) ? v : 0;
        if (parsedExisting >= currentVersion)
        {
            return; // 已達現行（或更新）→ no-op：不重跑 DDL、不重複 ALTER、不開交易。
        }

        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var ddl = CreateCommand(connection, projectId, SchemaSql))
            {
                ddl.Transaction = tx;
                await ddl.ExecuteNonQueryAsync(cancellationToken);
            }

            if (parsedExisting < 7)
            {
                await using (var backfill = CreateCommand(connection, projectId, BackfillAccountMappingCategoryIdsSql))
                {
                    backfill.Transaction = tx;
                    await backfill.ExecuteNonQueryAsync(cancellationToken);
                }

                await SqliteProjectDatabase.MigrateScenarioCategoryIdsAsync(
                    connection,
                    tx,
                    SqlServerProjectSchema.QualifierFor(projectId),
                    cancellationToken);

                MigrationFaultHookForTests?.Invoke("after-v7-data-rewrite");

                await RuleRunResultReset.ClearWithinAsync(
                    connection,
                    tx,
                    cancellationToken,
                    AuditMutation.SchemaV7Migration,
                    SqlServerProjectSchema.QualifierFor(projectId));

                MigrationFaultHookForTests?.Invoke("after-v7-result-reset");
            }

            await using (var bump = CreateCommand(connection, projectId, BumpSchemaVersionSql))
            {
                bump.Transaction = tx;
                await bump.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            try
            {
                await tx.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Preserve the migration failure; disposal remains the final rollback backstop.
            }

            throw;
        }
    }

    /// <summary>
    /// 讀該專案 schema 的 <c>schema_version</c>（<c>{s}.schema_info</c>）。schema_info 表尚不存在（極舊形狀或被卸）
    /// 時回 null，交由 <see cref="MigrateExistingSchemaToCurrentAsync"/> 當作「低於現行、需遷移」。schema 名經
    /// <see cref="CreateCommand"/> 的白名單守衛後拼入識別字（不可參數化），值鍵仍走參數化字面。
    /// </summary>
    private async Task<string?> ReadSchemaVersionAsync(
        SqlConnection connection, string projectId, CancellationToken cancellationToken)
    {
        await using var read = CreateCommand(connection, projectId,
            "IF OBJECT_ID(N'{s}.schema_info','U') IS NOT NULL " +
            "SELECT [value] FROM {s}.schema_info WHERE [key] = 'schema_version';");
        return await read.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>
    /// 啟動就緒化：確保單庫存在（不存在則以設定登入建立）與跨專案反查表就位，並驗證引擎非 Express。
    /// 冪等；供 composition 在 app 開啟時背景呼叫（「開啟即測試連線＋建庫」）。失敗由呼叫端當非致命處理
    /// （伺服器暫不可達、或登入尚未生效如剛切混合模式未重啟時，稍後建案會再試）。
    /// </summary>

    /// <summary>
    /// SQL Server 版 schema(對齊 SQLite SchemaSql 的第 3 版形狀,型別映射見 plan)。
    /// 冪等:每張表以 OBJECT_ID 守、每個索引以 sys.indexes 守。保留字 key/value 以方括號包。
    /// 定序契約(design §2.1,雙保險之二):凡參與 JOIN/WHERE/GROUP BY/UNIQUE/ORDER BY 的文字鍵欄,
    /// 一律顯式 COLLATE Latin1_General_BIN2(＝SQLite BINARY 位元序;BMP 中文＝碼位序)——即使庫層
    /// 預設定序不對(既有庫等環境重置),專案表行為仍正確,且意圖進版本控制。純顯示/payload 欄
    /// (line_item、account_name、document_description、source_module、day_name、mode_name、檔名路徑、
    /// *_json、app_message_log、config_filter_scenario 文字欄)刻意不釘,沿用庫層預設。
    /// line_item_numeric_sort_key 會參與 step4-1 ORDER BY／keyset，故固定 BIN2。
    /// 釘欄清單的機器守衛:SqlServerCollationProbeTests。
    /// </summary>
    // internal 供漂移守衛測試讀取（版本字面值須＝SchemaVersion，見該常數上方警語）。
    internal const string SchemaSql =
        """
        IF OBJECT_ID(N'{s}.schema_info','U') IS NULL
            CREATE TABLE {s}.schema_info ([key] NVARCHAR(450) COLLATE Latin1_General_BIN2 PRIMARY KEY, [value] NVARCHAR(MAX) NOT NULL);
        IF NOT EXISTS (SELECT 1 FROM {s}.schema_info WHERE [key] = 'schema_version')
            INSERT INTO {s}.schema_info ([key], [value]) VALUES ('schema_version', '9');

        IF OBJECT_ID(N'{s}.import_batch','U') IS NULL
            CREATE TABLE {s}.import_batch (
                batch_id         NVARCHAR(64) COLLATE Latin1_General_BIN2 PRIMARY KEY,
                dataset_kind     NVARCHAR(20) COLLATE Latin1_General_BIN2 NOT NULL CHECK (dataset_kind IN ('gl','tb','account_mapping')),
                source_file_path NVARCHAR(MAX) NOT NULL,
                source_file_name NVARCHAR(400) NOT NULL,
                imported_utc     NVARCHAR(40) COLLATE Latin1_General_BIN2 NOT NULL,
                row_count        INT NOT NULL DEFAULT 0,
                columns_json     NVARCHAR(MAX) NOT NULL
            );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_import_batch_kind' AND object_id = OBJECT_ID(N'{s}.import_batch'))
            CREATE INDEX ix_import_batch_kind ON {s}.import_batch (dataset_kind, imported_utc);

        IF OBJECT_ID(N'{s}.import_batch_source','U') IS NULL
            CREATE TABLE {s}.import_batch_source (
                batch_id         NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                source_no        INT NOT NULL,
                source_file_path NVARCHAR(MAX) NOT NULL,
                source_file_name NVARCHAR(400) NOT NULL,
                sheet_name       NVARCHAR(400) NULL,
                encoding         NVARCHAR(400) NULL,
                delimiter        NVARCHAR(400) NULL,
                row_count        INT NOT NULL DEFAULT 0,
                imported_utc     NVARCHAR(40) NOT NULL,
                PRIMARY KEY (batch_id, source_no)
            );

        IF OBJECT_ID(N'{s}.import_field_definition','U') IS NULL
            CREATE TABLE {s}.import_field_definition (
                batch_id            NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                definition_scope     NVARCHAR(16) COLLATE Latin1_General_BIN2 NOT NULL
                    CHECK (definition_scope IN ('source','target')),
                ordinal             INT NOT NULL CHECK (ordinal >= 1),
                field_name           NVARCHAR(MAX) NOT NULL,
                description          NVARCHAR(MAX) NULL,
                field_kind           NVARCHAR(16) COLLATE Latin1_General_BIN2 NOT NULL
                    CHECK (field_kind IN ('text','number','date','time')),
                text_length          INT NULL CHECK (text_length IS NULL OR text_length >= 0),
                decimal_places       INT NULL CHECK (decimal_places IS NULL OR decimal_places >= 0),
                max_rendered_length  INT NOT NULL CHECK (max_rendered_length >= 0),
                has_observation      BIT NOT NULL,
                PRIMARY KEY (batch_id, definition_scope, ordinal)
            );

        IF OBJECT_ID(N'{s}.staging_gl_raw_row','U') IS NULL
            CREATE TABLE {s}.staging_gl_raw_row (
                batch_id          NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                row_number        BIGINT NOT NULL,
                source_no         INT NOT NULL DEFAULT 1,
                source_row_number INT NOT NULL DEFAULT 0,
                row_json          NVARCHAR(MAX) NOT NULL,
                PRIMARY KEY (batch_id, row_number)
            );

        IF OBJECT_ID(N'{s}.staging_tb_raw_row','U') IS NULL
            CREATE TABLE {s}.staging_tb_raw_row (
                batch_id          NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                row_number        BIGINT NOT NULL,
                source_no         INT NOT NULL DEFAULT 1,
                source_row_number INT NOT NULL DEFAULT 0,
                row_json          NVARCHAR(MAX) NOT NULL,
                PRIMARY KEY (batch_id, row_number)
            );

        IF OBJECT_ID(N'{s}.config_field_mapping','U') IS NULL
            CREATE TABLE {s}.config_field_mapping (
                dataset_kind    NVARCHAR(20) COLLATE Latin1_General_BIN2 PRIMARY KEY CHECK (dataset_kind IN ('gl','tb')),
                mapping_json    NVARCHAR(MAX) NOT NULL,
                mode_name       NVARCHAR(40) NOT NULL,
                source_batch_id NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                committed_utc   NVARCHAR(40) NOT NULL,
                format_version  INT NOT NULL DEFAULT 1,
                options_json    NVARCHAR(MAX) NULL
            );
        IF COL_LENGTH('{s}.config_field_mapping','format_version') IS NULL
            ALTER TABLE {s}.config_field_mapping ADD format_version INT NOT NULL DEFAULT 1 WITH VALUES;
        IF COL_LENGTH('{s}.config_field_mapping','options_json') IS NULL
            ALTER TABLE {s}.config_field_mapping ADD options_json NVARCHAR(MAX) NULL;

        IF OBJECT_ID(N'{s}.staging_calendar_raw_day','U') IS NULL
            CREATE TABLE {s}.staging_calendar_raw_day (
                day_type NVARCHAR(10) COLLATE Latin1_General_BIN2 NOT NULL CHECK (day_type IN ('holiday','makeup')),
                date     NVARCHAR(32) COLLATE Latin1_General_BIN2 NOT NULL,
                day_name NVARCHAR(256) NULL,
                PRIMARY KEY (day_type, date)
            );
        IF COL_LENGTH('{s}.staging_calendar_raw_day','day_name') IS NULL
            ALTER TABLE {s}.staging_calendar_raw_day ADD day_name NVARCHAR(256) NULL;
        IF OBJECT_ID(N'{s}.target_gl_entry','U') IS NULL
            CREATE TABLE {s}.target_gl_entry (
                entry_id              BIGINT IDENTITY(1,1) PRIMARY KEY,
                batch_id              NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                source_row_number     BIGINT NOT NULL,
                document_number       NVARCHAR(450) COLLATE Latin1_General_BIN2 NULL,
                line_item             NVARCHAR(400) NULL,
                line_item_numeric_sort_key NVARCHAR(40) COLLATE Latin1_General_BIN2 NULL,
                post_date             NVARCHAR(32) COLLATE Latin1_General_BIN2 NULL,
                approval_date         NVARCHAR(32) COLLATE Latin1_General_BIN2 NULL,
                voucher_date          NVARCHAR(32) COLLATE Latin1_General_BIN2 NULL,
                account_code          NVARCHAR(450) COLLATE Latin1_General_BIN2 NULL,
                account_name          NVARCHAR(400) NULL,
                document_description  NVARCHAR(MAX) NULL,
                source_module         NVARCHAR(400) NULL,
                created_by            NVARCHAR(400) COLLATE Latin1_General_BIN2 NULL,
                approved_by           NVARCHAR(400) COLLATE Latin1_General_BIN2 NULL,
                is_manual             INT NULL,
                posting_status        NVARCHAR(400) COLLATE Latin1_General_BIN2 NULL,
                is_effective          BIT NULL,
                exclusion_reason      NVARCHAR(64) COLLATE Latin1_General_BIN2 NULL,
                amount_scaled         BIGINT NOT NULL,
                debit_amount_scaled   BIGINT NOT NULL,
                credit_amount_scaled  BIGINT NOT NULL,
                dr_cr                 NVARCHAR(10) COLLATE Latin1_General_BIN2 NOT NULL CHECK (dr_cr IN ('DEBIT','CREDIT'))
            );
        IF COL_LENGTH('{s}.target_gl_entry','voucher_date') IS NULL
            ALTER TABLE {s}.target_gl_entry ADD voucher_date NVARCHAR(32) COLLATE Latin1_General_BIN2 NULL;
        IF COL_LENGTH('{s}.target_gl_entry','line_item_numeric_sort_key') IS NULL
            ALTER TABLE {s}.target_gl_entry ADD line_item_numeric_sort_key NVARCHAR(40) COLLATE Latin1_General_BIN2 NULL;
        IF COL_LENGTH('{s}.target_gl_entry','posting_status') IS NULL
            ALTER TABLE {s}.target_gl_entry ADD posting_status NVARCHAR(400) COLLATE Latin1_General_BIN2 NULL;
        IF COL_LENGTH('{s}.target_gl_entry','is_effective') IS NULL
            ALTER TABLE {s}.target_gl_entry ADD is_effective BIT NULL;
        IF COL_LENGTH('{s}.target_gl_entry','exclusion_reason') IS NULL
            ALTER TABLE {s}.target_gl_entry ADD exclusion_reason NVARCHAR(64) COLLATE Latin1_General_BIN2 NULL;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_target_gl_entry_doc' AND object_id = OBJECT_ID(N'{s}.target_gl_entry'))
            CREATE INDEX ix_target_gl_entry_doc ON {s}.target_gl_entry (document_number);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_target_gl_entry_account' AND object_id = OBJECT_ID(N'{s}.target_gl_entry'))
            CREATE INDEX ix_target_gl_entry_account ON {s}.target_gl_entry (account_code);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_target_gl_entry_post_date' AND object_id = OBJECT_ID(N'{s}.target_gl_entry'))
            CREATE INDEX ix_target_gl_entry_post_date ON {s}.target_gl_entry (post_date);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_target_gl_entry_approval_date' AND object_id = OBJECT_ID(N'{s}.target_gl_entry'))
            CREATE INDEX ix_target_gl_entry_approval_date ON {s}.target_gl_entry (approval_date);

        IF OBJECT_ID(N'{s}.config_gl_rde_field','U') IS NULL
            CREATE TABLE {s}.config_gl_rde_field (
                field_id      NVARCHAR(64) COLLATE Latin1_General_BIN2 PRIMARY KEY,
                source_column NVARCHAR(MAX) NOT NULL,
                label         NVARCHAR(400) NOT NULL,
                value_type    NVARCHAR(10) COLLATE Latin1_General_BIN2 NOT NULL
                    CHECK (value_type IN ('text','date','money')),
                ordinal       INT NOT NULL,
                is_rde        BIT NOT NULL
            );

        IF OBJECT_ID(N'{s}.target_gl_rde_value','U') IS NULL
            CREATE TABLE {s}.target_gl_rde_value (
                entry_id      BIGINT NOT NULL,
                field_id      NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                value_type    NVARCHAR(10) COLLATE Latin1_General_BIN2 NOT NULL
                    CHECK (value_type IN ('text','date','money')),
                text_value    NVARCHAR(450) COLLATE Latin1_General_BIN2 NULL,
                date_value    NVARCHAR(32) COLLATE Latin1_General_BIN2 NULL,
                amount_scaled BIGINT NULL,
                PRIMARY KEY (entry_id, field_id),
                CHECK (
                    (CASE WHEN text_value IS NULL THEN 0 ELSE 1 END
                     + CASE WHEN date_value IS NULL THEN 0 ELSE 1 END
                     + CASE WHEN amount_scaled IS NULL THEN 0 ELSE 1 END) = 1
                    AND ((value_type = 'text' AND text_value IS NOT NULL)
                      OR (value_type = 'date' AND date_value IS NOT NULL)
                      OR (value_type = 'money' AND amount_scaled IS NOT NULL))
                )
            );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_target_gl_rde_value_text' AND object_id = OBJECT_ID(N'{s}.target_gl_rde_value'))
            CREATE INDEX ix_target_gl_rde_value_text ON {s}.target_gl_rde_value (field_id, value_type, text_value, entry_id);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_target_gl_rde_value_date' AND object_id = OBJECT_ID(N'{s}.target_gl_rde_value'))
            CREATE INDEX ix_target_gl_rde_value_date ON {s}.target_gl_rde_value (field_id, value_type, date_value, entry_id);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_target_gl_rde_value_amount' AND object_id = OBJECT_ID(N'{s}.target_gl_rde_value'))
            CREATE INDEX ix_target_gl_rde_value_amount ON {s}.target_gl_rde_value (field_id, value_type, amount_scaled, entry_id);

        IF OBJECT_ID(N'{s}.target_tb_balance','U') IS NULL
            CREATE TABLE {s}.target_tb_balance (
                balance_id            BIGINT IDENTITY(1,1) PRIMARY KEY,
                batch_id              NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                source_row_number     BIGINT NOT NULL,
                account_code          NVARCHAR(450) COLLATE Latin1_General_BIN2 NULL,
                account_name          NVARCHAR(400) NULL,
                change_amount_scaled  BIGINT NOT NULL
            );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_target_tb_balance_account' AND object_id = OBJECT_ID(N'{s}.target_tb_balance'))
            CREATE INDEX ix_target_tb_balance_account ON {s}.target_tb_balance (account_code);

        IF OBJECT_ID(N'{s}.result_rule_run','U') IS NULL
            CREATE TABLE {s}.result_rule_run (
                run_id        NVARCHAR(64) COLLATE Latin1_General_BIN2 PRIMARY KEY,
                run_kind      NVARCHAR(20) COLLATE Latin1_General_BIN2 NOT NULL CHECK (run_kind IN ('validate','prescreen')),
                generated_utc NVARCHAR(40) COLLATE Latin1_General_BIN2 NOT NULL,
                summary_json  NVARCHAR(MAX) NOT NULL
            );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_result_rule_run_kind' AND object_id = OBJECT_ID(N'{s}.result_rule_run'))
            CREATE INDEX ix_result_rule_run_kind ON {s}.result_rule_run (run_kind, generated_utc);

        IF OBJECT_ID(N'{s}.result_inf_sampling_test_sample','U') IS NULL
            CREATE TABLE {s}.result_inf_sampling_test_sample (
                run_id          NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                entry_id        BIGINT NOT NULL,
                document_number NVARCHAR(450) COLLATE Latin1_General_BIN2 NULL,
                line_item       NVARCHAR(400) NULL,
                PRIMARY KEY (run_id, entry_id)
            );

        IF OBJECT_ID(N'{s}.config_result_stale_state','U') IS NULL
            CREATE TABLE {s}.config_result_stale_state (
                singleton        INT PRIMARY KEY CHECK (singleton = 1),
                validation_stale BIT NOT NULL,
                prescreen_stale  BIT NOT NULL,
                filter_stale     BIT NOT NULL
            );
        IF NOT EXISTS (SELECT 1 FROM {s}.config_result_stale_state WHERE singleton = 1)
            INSERT INTO {s}.config_result_stale_state
                (singleton, validation_stale, prescreen_stale, filter_stale)
            VALUES (1, 0, 0, 0);

        IF OBJECT_ID(N'{s}.config_filter_scenario','U') IS NULL
            CREATE TABLE {s}.config_filter_scenario (
                position        INT PRIMARY KEY,
                name            NVARCHAR(400) NOT NULL,
                rationale       NVARCHAR(MAX) NOT NULL,
                definition_json NVARCHAR(MAX) NOT NULL,
                saved_utc       NVARCHAR(40) NOT NULL
            );

        IF OBJECT_ID(N'{s}.staging_account_mapping_raw_row','U') IS NULL
            CREATE TABLE {s}.staging_account_mapping_raw_row (
                batch_id          NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                row_number        BIGINT NOT NULL,
                source_no         INT NOT NULL DEFAULT 1,
                source_row_number INT NOT NULL DEFAULT 0,
                row_json          NVARCHAR(MAX) NOT NULL,
                PRIMARY KEY (batch_id, row_number)
            );

        IF OBJECT_ID(N'{s}.config_account_taxonomy','U') IS NULL
            CREATE TABLE {s}.config_account_taxonomy (
                category_id   NVARCHAR(64) COLLATE Latin1_General_BIN2 PRIMARY KEY,
                label         NVARCHAR(400) NOT NULL,
                ordinal       INT NOT NULL UNIQUE,
                semantic_role NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                is_builtin    BIT NOT NULL,
                revision      INT NOT NULL
            );
        IF NOT EXISTS (SELECT 1 FROM {s}.config_account_taxonomy WHERE category_id = 'builtin.revenue')
            INSERT INTO {s}.config_account_taxonomy VALUES ('builtin.revenue', 'Revenue', 0, 'revenue', 1, 1);
        IF NOT EXISTS (SELECT 1 FROM {s}.config_account_taxonomy WHERE category_id = 'builtin.receivables')
            INSERT INTO {s}.config_account_taxonomy VALUES ('builtin.receivables', 'Receivables', 1, 'receivables', 1, 1);
        IF NOT EXISTS (SELECT 1 FROM {s}.config_account_taxonomy WHERE category_id = 'builtin.cash')
            INSERT INTO {s}.config_account_taxonomy VALUES ('builtin.cash', 'Cash', 2, 'cash', 1, 1);
        IF NOT EXISTS (SELECT 1 FROM {s}.config_account_taxonomy WHERE category_id = 'builtin.receipt_in_advance')
            INSERT INTO {s}.config_account_taxonomy VALUES ('builtin.receipt_in_advance', 'Receipt in advance', 3, 'receipt_in_advance', 1, 1);
        IF NOT EXISTS (SELECT 1 FROM {s}.config_account_taxonomy WHERE category_id = 'builtin.others')
            INSERT INTO {s}.config_account_taxonomy VALUES ('builtin.others', 'Others', 4, 'others', 1, 1);

        IF OBJECT_ID(N'{s}.target_account_mapping','U') IS NULL
            CREATE TABLE {s}.target_account_mapping (
                mapping_id            BIGINT IDENTITY(1,1) PRIMARY KEY,
                batch_id              NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                source_row_number     INT NOT NULL,
                account_code          NVARCHAR(450) COLLATE Latin1_General_BIN2 NOT NULL,
                account_name          NVARCHAR(400) NULL,
                standardized_category NVARCHAR(40) COLLATE Latin1_General_BIN2 NOT NULL
                    CHECK (standardized_category IN ('Revenue','Receivables','Cash','Receipt in advance','Others')),
                category_id           NVARCHAR(64) COLLATE Latin1_General_BIN2 NULL
            );
        IF COL_LENGTH('{s}.target_account_mapping','category_id') IS NULL
            ALTER TABLE {s}.target_account_mapping ADD category_id NVARCHAR(64) COLLATE Latin1_General_BIN2 NULL;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_target_account_mapping_code' AND object_id = OBJECT_ID(N'{s}.target_account_mapping'))
            CREATE UNIQUE INDEX ix_target_account_mapping_code ON {s}.target_account_mapping (account_code);

        IF OBJECT_ID(N'{s}.staging_authorized_preparer_raw_row','U') IS NULL
            CREATE TABLE {s}.staging_authorized_preparer_raw_row (
                batch_id          NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                row_number        BIGINT NOT NULL,
                source_no         INT NOT NULL DEFAULT 1,
                source_row_number INT NOT NULL DEFAULT 0,
                row_json          NVARCHAR(MAX) NOT NULL,
                PRIMARY KEY (batch_id, row_number)
            );

        IF OBJECT_ID(N'{s}.target_authorized_preparer','U') IS NULL
            CREATE TABLE {s}.target_authorized_preparer (
                name NVARCHAR(450) COLLATE Latin1_General_BIN2 NOT NULL PRIMARY KEY
            );

        IF OBJECT_ID(N'{s}.app_message_log','U') IS NULL
            CREATE TABLE {s}.app_message_log (
                message_id   BIGINT IDENTITY(1,1) PRIMARY KEY,
                occurred_utc NVARCHAR(40) NOT NULL,
                level        NVARCHAR(10) NOT NULL CHECK (level IN ('info','warn')),
                text         NVARCHAR(MAX) NOT NULL
            );

        IF OBJECT_ID(N'{s}.audit_event_log','U') IS NULL
            CREATE TABLE {s}.audit_event_log (
                event_id       NVARCHAR(32) COLLATE Latin1_General_BIN2 PRIMARY KEY,
                occurred_utc   NVARCHAR(40) COLLATE Latin1_General_BIN2 NOT NULL,
                operation      NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                target_type    NVARCHAR(32) COLLATE Latin1_General_BIN2 NOT NULL,
                target_id      NVARCHAR(512) COLLATE Latin1_General_BIN2 NOT NULL,
                subject_count  BIGINT NOT NULL CHECK (subject_count >= 0),
                replaced_count BIGINT NOT NULL CHECK (replaced_count >= 0)
            );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_audit_event_log_occurred' AND object_id = OBJECT_ID(N'{s}.audit_event_log'))
            CREATE INDEX ix_audit_event_log_occurred ON {s}.audit_event_log (occurred_utc, event_id);

        -- schema v9：{s}.audit_event_log 的 append-only 由引擎強制。INSTEAD OF UPDATE, DELETE
        -- trigger 讓任何改與刪直接 THROW，INSERT 不受影響。CREATE TRIGGER 必須是 batch 內第一個
        -- 敘述，故沿用本檔既有的 EXEC() 動態 DDL 慣例（同 CREATE SCHEMA）。
        IF OBJECT_ID(N'{s}.trg_audit_event_log_append_only','TR') IS NULL
            EXEC('CREATE TRIGGER {s}.trg_audit_event_log_append_only ON {s}.audit_event_log
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 50009, ''project audit log is append-only'', 1;
                END');

        IF OBJECT_ID(N'{s}.gl_control_total','U') IS NULL
            CREATE TABLE {s}.gl_control_total (
                singleton        INT PRIMARY KEY CHECK (singleton = 1),
                source_row_count BIGINT NOT NULL,
                target_row_count BIGINT NOT NULL,
                target_debit_scaled  BIGINT NOT NULL,
                target_credit_scaled BIGINT NOT NULL,
                raw_row_count        BIGINT NULL,
                effective_row_count  BIGINT NULL,
                excluded_by_period_count BIGINT NULL,
                excluded_by_posting_status_count BIGINT NULL,
                effective_debit_scaled  BIGINT NULL,
                effective_credit_scaled BIGINT NULL
            );
        IF COL_LENGTH('{s}.gl_control_total','raw_row_count') IS NULL
            ALTER TABLE {s}.gl_control_total ADD raw_row_count BIGINT NULL;
        IF COL_LENGTH('{s}.gl_control_total','effective_row_count') IS NULL
            ALTER TABLE {s}.gl_control_total ADD effective_row_count BIGINT NULL;
        IF COL_LENGTH('{s}.gl_control_total','excluded_by_period_count') IS NULL
            ALTER TABLE {s}.gl_control_total ADD excluded_by_period_count BIGINT NULL;
        IF COL_LENGTH('{s}.gl_control_total','excluded_by_posting_status_count') IS NULL
            ALTER TABLE {s}.gl_control_total ADD excluded_by_posting_status_count BIGINT NULL;
        IF COL_LENGTH('{s}.gl_control_total','effective_debit_scaled') IS NULL
            ALTER TABLE {s}.gl_control_total ADD effective_debit_scaled BIGINT NULL;
        IF COL_LENGTH('{s}.gl_control_total','effective_credit_scaled') IS NULL
            ALTER TABLE {s}.gl_control_total ADD effective_credit_scaled BIGINT NULL;

        IF OBJECT_ID(N'{s}.result_filter_run', N'U') IS NULL
            CREATE TABLE {s}.result_filter_run (
                scenario_position INT NOT NULL,
                entry_id          BIGINT NOT NULL,
                PRIMARY KEY (scenario_position, entry_id)
            );
        -- D2 tag 矩陣即時 pivot 的輔助索引(對齊 SQLite idx_result_filter_run_entry):以 entry_id 為前導鍵,
        -- 讓 EXISTS / entry_id 鍵範圍存取走索引(PK 前導鍵為 scenario_position,不利這兩種存取)。
        -- sys.indexes 守欄、加法,不升 schema 版本。
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_result_filter_run_entry' AND object_id = OBJECT_ID(N'{s}.result_filter_run'))
            CREATE INDEX idx_result_filter_run_entry ON {s}.result_filter_run (entry_id, scenario_position);
        """;
}
