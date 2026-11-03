using JET.Domain;
using JET.AuditCore;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

public sealed partial class SqlServerProjectDatabase
{
    // 目前 schema 版本。新 schema 由 SchemaSql 直接寫現行版；既有 schema 只檢查版本，不升版。
    internal const string SchemaVersion = "19";

    public async Task EnsureCreatedAsync(string projectId, CancellationToken cancellationToken)
    {
        var schema = SqlServerProjectSchema.For(projectId);

        // 1) 確保單庫存在 + dbo 管理表就位(registry/access/app_config/operation_log/project_lock)。
        await EnsureSingleDatabaseAndControlPlaneAsync(cancellationToken);

        await using var connection = CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        // 2) schema 已存在 → 只檢查版本，不是現行版本就擋下，不改動既有內容。
        bool schemaExists;
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT CASE WHEN SCHEMA_ID(@s) IS NULL THEN 0 ELSE 1 END;";
            exists.Parameters.AddWithValue("@s", schema);
            schemaExists = Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) == 1;
        }

        if (schemaExists)
        {
            RejectUnsupportedSchemaVersion(await ReadSchemaVersionAsync(connection, projectId, cancellationToken));
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
    /// 既有 schema 只接受現行版本，和本機資料庫同一套說法（裁定 2026-10-30-24：SQL Server 的程式跟著改，只經編譯）。
    /// 低於現行版本是舊版 JET 建立的案件，請審計員刪除後重建；版本值缺漏、讀不出整數或高於現行版本時，當成版本資訊無法辨識。
    /// SQL Server 開發暫緩期間改成這樣，沒有在 SQL Server 上實際執行過。
    /// </summary>
    private static void RejectUnsupportedSchemaVersion(string? existingVersion)
    {
        var currentVersion = int.Parse(SchemaVersion, System.Globalization.CultureInfo.InvariantCulture);
        if (!int.TryParse(existingVersion, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var version)
            || version > currentVersion)
        {
            throw new JetActionException(JetErrorCodes.InvalidProjectSchema, ProjectFolderMessages.UnreadableDatabaseVersion);
        }

        if (version < currentVersion)
        {
            throw new JetActionException(JetErrorCodes.InvalidProjectSchema, ProjectFolderMessages.OldVersionProject());
        }
    }

    /// <summary>
    /// 讀該專案 schema 的 <c>schema_version</c>（<c>{s}.schema_info</c>）。schema_info 表不存在時回 null，
    /// 由 <see cref="RejectUnsupportedSchemaVersion"/> 當成版本資訊無法辨識。schema 名經
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
    /// SQL Server 版 schema(表形與 SQLite 對齊,目前版本見 <see cref="SchemaVersion"/>)。
    /// 冪等:每張表以 OBJECT_ID 守、每個索引以 sys.indexes 守。保留字 key/value 以方括號包。
    /// 定序契約(定序雙保險之二):凡參與 JOIN/WHERE/GROUP BY/UNIQUE/ORDER BY 的文字鍵欄,
    /// 一律顯式 COLLATE Latin1_General_BIN2(＝SQLite BINARY 位元序;BMP 中文＝碼位序)——即使庫層
    /// 預設定序不對(既有庫等環境重置),專案表行為仍正確,且意圖進版本控制。GL、TB 與科目配對的文字欄
    /// (line_item、account_name、description、source_module)會被文字條件、排序與 MAX 用到,
    /// 也固定 BIN2。其餘純顯示/payload 欄(day_name、mode_name、
    /// 檔名路徑、*_json、message_log、filter_scenario 文字欄)沿用庫層預設。
    /// line_item_numeric_sort_key 會參與 step4-1 ORDER BY／keyset，故固定 BIN2。
    /// 釘欄清單的機器守衛:SqlServerCollationProbeTests。
    /// </summary>
    // internal 供漂移守衛測試讀取（版本字面值須＝SchemaVersion，見該常數上方警語）。
    internal const string SchemaSql =
        """
        IF OBJECT_ID(N'{s}.schema_info','U') IS NULL
            CREATE TABLE {s}.schema_info ([key] NVARCHAR(450) COLLATE Latin1_General_BIN2 PRIMARY KEY, [value] NVARCHAR(MAX) NOT NULL);
        IF NOT EXISTS (SELECT 1 FROM {s}.schema_info WHERE [key] = 'schema_version')
            INSERT INTO {s}.schema_info ([key], [value]) VALUES ('schema_version', '19');
        IF NOT EXISTS (SELECT 1 FROM {s}.schema_info WHERE [key] = 'filter_data_revision')
            INSERT INTO {s}.schema_info ([key], [value]) VALUES ('filter_data_revision', '0');

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
                header_row       INT NULL,
                row_count        INT NOT NULL DEFAULT 0,
                imported_utc     NVARCHAR(40) NOT NULL,
                PRIMARY KEY (batch_id, source_no)
            );

        IF OBJECT_ID(N'{s}.field_info','U') IS NULL
            CREATE TABLE {s}.field_info (
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

        IF OBJECT_ID(N'{s}.pbc_gl','U') IS NULL
            CREATE TABLE {s}.pbc_gl (
                batch_id          NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                row_number        BIGINT NOT NULL,
                source_no         INT NOT NULL DEFAULT 1,
                source_row_number INT NOT NULL DEFAULT 0,
                row_json          NVARCHAR(MAX) NOT NULL,
                PRIMARY KEY (batch_id, row_number)
            );

        IF OBJECT_ID(N'{s}.pbc_tb','U') IS NULL
            CREATE TABLE {s}.pbc_tb (
                batch_id          NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                row_number        BIGINT NOT NULL,
                source_no         INT NOT NULL DEFAULT 1,
                source_row_number INT NOT NULL DEFAULT 0,
                row_json          NVARCHAR(MAX) NOT NULL,
                PRIMARY KEY (batch_id, row_number)
            );

        IF OBJECT_ID(N'{s}.field_mapping','U') IS NULL
            CREATE TABLE {s}.field_mapping (
                dataset_kind    NVARCHAR(20) COLLATE Latin1_General_BIN2 PRIMARY KEY CHECK (dataset_kind IN ('gl','tb')),
                mapping_json    NVARCHAR(MAX) NOT NULL,
                mode_name       NVARCHAR(40) NOT NULL,
                source_batch_id NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                committed_utc   NVARCHAR(40) NOT NULL,
                format_version  INT NOT NULL DEFAULT 1,
                options_json    NVARCHAR(MAX) NULL
            );

        IF OBJECT_ID(N'{s}.field_mapping_previous','U') IS NULL
            CREATE TABLE {s}.field_mapping_previous (
                dataset_kind    NVARCHAR(20) COLLATE Latin1_General_BIN2 PRIMARY KEY CHECK (dataset_kind IN ('gl','tb')),
                mapping_json    NVARCHAR(MAX) NOT NULL,
                mode_name       NVARCHAR(40) NOT NULL,
                source_batch_id NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                committed_utc   NVARCHAR(40) NOT NULL,
                format_version  INT NOT NULL,
                options_json    NVARCHAR(MAX) NULL
            );

        IF OBJECT_ID(N'{s}.calendar_day','U') IS NULL
            CREATE TABLE {s}.calendar_day (
                day_type NVARCHAR(10) COLLATE Latin1_General_BIN2 NOT NULL CHECK (day_type IN ('holiday','makeup')),
                date     NVARCHAR(32) COLLATE Latin1_General_BIN2 NOT NULL,
                day_name NVARCHAR(256) NULL,
                PRIMARY KEY (day_type, date)
            );
        IF OBJECT_ID(N'{s}.gl','U') IS NULL
            CREATE TABLE {s}.gl (
                entry_id              BIGINT IDENTITY(1,1) PRIMARY KEY,
                batch_id              NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                source_row_number     BIGINT NOT NULL,
                voucher_number       NVARCHAR(450) COLLATE Latin1_General_BIN2 NULL,
                line_item             NVARCHAR(400) COLLATE Latin1_General_BIN2 NULL,
                line_item_numeric_sort_key NVARCHAR(40) COLLATE Latin1_General_BIN2 NULL,
                gl_date             NVARCHAR(32) COLLATE Latin1_General_BIN2 NULL,
                approval_date         NVARCHAR(32) COLLATE Latin1_General_BIN2 NULL,
                account_number          NVARCHAR(450) COLLATE Latin1_General_BIN2 NULL,
                account_name          NVARCHAR(400) COLLATE Latin1_General_BIN2 NULL,
                description           NVARCHAR(MAX) COLLATE Latin1_General_BIN2 NULL,
                source_module         NVARCHAR(400) COLLATE Latin1_General_BIN2 NULL,
                created_by            NVARCHAR(400) COLLATE Latin1_General_BIN2 NULL,
                approved_by           NVARCHAR(400) COLLATE Latin1_General_BIN2 NULL,
                is_manual             INT NULL,
                posting_status        NVARCHAR(400) COLLATE Latin1_General_BIN2 NULL,
                is_excluded           BIT NULL,
                exclusion_reason      NVARCHAR(64) COLLATE Latin1_General_BIN2 NULL,
                amount_scaled         BIGINT NOT NULL,
                debit_amount_scaled   BIGINT NOT NULL,
                credit_amount_scaled  BIGINT NOT NULL,
                dr_cr                 NVARCHAR(10) COLLATE Latin1_General_BIN2 NOT NULL CHECK (dr_cr IN ('DEBIT','CREDIT'))
            );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_gl_voucher' AND object_id = OBJECT_ID(N'{s}.gl'))
            CREATE INDEX ix_gl_voucher ON {s}.gl (voucher_number);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_gl_account' AND object_id = OBJECT_ID(N'{s}.gl'))
            CREATE INDEX ix_gl_account ON {s}.gl (account_number);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_gl_gl_date' AND object_id = OBJECT_ID(N'{s}.gl'))
            CREATE INDEX ix_gl_gl_date ON {s}.gl (gl_date);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_gl_approval_date' AND object_id = OBJECT_ID(N'{s}.gl'))
            CREATE INDEX ix_gl_approval_date ON {s}.gl (approval_date);

        IF OBJECT_ID(N'{s}.gl_rde_field','U') IS NULL
            CREATE TABLE {s}.gl_rde_field (
                field_id      NVARCHAR(64) COLLATE Latin1_General_BIN2 PRIMARY KEY,
                source_column NVARCHAR(MAX) NOT NULL,
                label         NVARCHAR(400) NOT NULL,
                value_type    NVARCHAR(10) COLLATE Latin1_General_BIN2 NOT NULL
                    CHECK (value_type IN ('text','date','amount')),
                ordinal       INT NOT NULL,
                is_rde        BIT NOT NULL
            );

        IF OBJECT_ID(N'{s}.gl_rde_value','U') IS NULL
            CREATE TABLE {s}.gl_rde_value (
                entry_id      BIGINT NOT NULL,
                field_id      NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                value_type    NVARCHAR(10) COLLATE Latin1_General_BIN2 NOT NULL
                    CHECK (value_type IN ('text','date','amount')),
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
                      OR (value_type = 'amount' AND amount_scaled IS NOT NULL))
                )
            );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_gl_rde_value_text' AND object_id = OBJECT_ID(N'{s}.gl_rde_value'))
            CREATE INDEX ix_gl_rde_value_text ON {s}.gl_rde_value (field_id, value_type, text_value, entry_id);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_gl_rde_value_date' AND object_id = OBJECT_ID(N'{s}.gl_rde_value'))
            CREATE INDEX ix_gl_rde_value_date ON {s}.gl_rde_value (field_id, value_type, date_value, entry_id);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_gl_rde_value_amount' AND object_id = OBJECT_ID(N'{s}.gl_rde_value'))
            CREATE INDEX ix_gl_rde_value_amount ON {s}.gl_rde_value (field_id, value_type, amount_scaled, entry_id);

        IF OBJECT_ID(N'{s}.tb','U') IS NULL
            CREATE TABLE {s}.tb (
                balance_id            BIGINT IDENTITY(1,1) PRIMARY KEY,
                batch_id              NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                source_row_number     BIGINT NOT NULL,
                account_number          NVARCHAR(450) COLLATE Latin1_General_BIN2 NULL,
                account_name          NVARCHAR(400) COLLATE Latin1_General_BIN2 NULL,
                change_amount_scaled  BIGINT NOT NULL
            );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_tb_account' AND object_id = OBJECT_ID(N'{s}.tb'))
            CREATE INDEX ix_tb_account ON {s}.tb (account_number);

        IF OBJECT_ID(N'{s}.validation_result','U') IS NULL
            CREATE TABLE {s}.validation_result (
                result_id     NVARCHAR(64) COLLATE Latin1_General_BIN2 PRIMARY KEY,
                generated_utc NVARCHAR(40) COLLATE Latin1_General_BIN2 NOT NULL,
                summary_json  NVARCHAR(MAX) NOT NULL
            );
        IF OBJECT_ID(N'{s}.prescreen_result','U') IS NULL
            CREATE TABLE {s}.prescreen_result (
                result_id     NVARCHAR(64) COLLATE Latin1_General_BIN2 PRIMARY KEY,
                generated_utc NVARCHAR(40) COLLATE Latin1_General_BIN2 NOT NULL,
                summary_json  NVARCHAR(MAX) NOT NULL
            );

        IF OBJECT_ID(N'{s}.inf_sample','U') IS NULL
            CREATE TABLE {s}.inf_sample (
                result_id       NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                entry_id        BIGINT NOT NULL,
                voucher_number NVARCHAR(450) COLLATE Latin1_General_BIN2 NULL,
                line_item       NVARCHAR(400) COLLATE Latin1_General_BIN2 NULL,
                PRIMARY KEY (result_id, entry_id)
            );

        IF OBJECT_ID(N'{s}.result_stale_state','U') IS NULL
            CREATE TABLE {s}.result_stale_state (
                singleton        INT PRIMARY KEY CHECK (singleton = 1),
                validation_stale BIT NOT NULL,
                prescreen_stale  BIT NOT NULL,
                filter_stale     BIT NOT NULL
            );
        IF NOT EXISTS (SELECT 1 FROM {s}.result_stale_state WHERE singleton = 1)
            INSERT INTO {s}.result_stale_state
                (singleton, validation_stale, prescreen_stale, filter_stale)
            VALUES (1, 0, 0, 0);

        IF OBJECT_ID(N'{s}.filter_scenario','U') IS NULL
            CREATE TABLE {s}.filter_scenario (
                scenario_id     INT PRIMARY KEY,
                position        INT NOT NULL,
                name            NVARCHAR(400) NOT NULL,
                rationale       NVARCHAR(MAX) NOT NULL,
                definition_json NVARCHAR(MAX) NOT NULL,
                saved_utc       NVARCHAR(40) NOT NULL
            );

        IF OBJECT_ID(N'{s}.pbc_account_mapping','U') IS NULL
            CREATE TABLE {s}.pbc_account_mapping (
                batch_id          NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                row_number        BIGINT NOT NULL,
                source_no         INT NOT NULL DEFAULT 1,
                source_row_number INT NOT NULL DEFAULT 0,
                row_json          NVARCHAR(MAX) NOT NULL,
                PRIMARY KEY (batch_id, row_number)
            );

        IF OBJECT_ID(N'{s}.account_category','U') IS NULL
            CREATE TABLE {s}.account_category (
                category_id   NVARCHAR(64) COLLATE Latin1_General_BIN2 PRIMARY KEY,
                label         NVARCHAR(400) NOT NULL,
                ordinal       INT NOT NULL UNIQUE,
                semantic_role NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                is_builtin    BIT NOT NULL,
                revision      INT NOT NULL,
                parent_category_id NVARCHAR(64) COLLATE Latin1_General_BIN2 NULL
            );
        IF NOT EXISTS (SELECT 1 FROM {s}.account_category WHERE category_id = 'builtin.revenue')
            INSERT INTO {s}.account_category (category_id, label, ordinal, semantic_role, is_builtin, revision) VALUES ('builtin.revenue', 'Revenue', 0, 'revenue', 1, 1);
        IF NOT EXISTS (SELECT 1 FROM {s}.account_category WHERE category_id = 'builtin.receivables')
            INSERT INTO {s}.account_category (category_id, label, ordinal, semantic_role, is_builtin, revision) VALUES ('builtin.receivables', 'Receivables', 1, 'receivables', 1, 1);
        IF NOT EXISTS (SELECT 1 FROM {s}.account_category WHERE category_id = 'builtin.cash')
            INSERT INTO {s}.account_category (category_id, label, ordinal, semantic_role, is_builtin, revision) VALUES ('builtin.cash', 'Cash', 2, 'cash', 1, 1);
        IF NOT EXISTS (SELECT 1 FROM {s}.account_category WHERE category_id = 'builtin.receipt_in_advance')
            INSERT INTO {s}.account_category (category_id, label, ordinal, semantic_role, is_builtin, revision) VALUES ('builtin.receipt_in_advance', 'Receipt in advance', 3, 'receipt_in_advance', 1, 1);
        IF NOT EXISTS (SELECT 1 FROM {s}.account_category WHERE category_id = 'builtin.others')
            INSERT INTO {s}.account_category (category_id, label, ordinal, semantic_role, is_builtin, revision) VALUES ('builtin.others', 'Others', 4, 'others', 1, 1);

        IF OBJECT_ID(N'{s}.account_category_path','U') IS NULL
        BEGIN
            CREATE TABLE {s}.account_category_path (
                ancestor_id NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                descendant_id NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                PRIMARY KEY (ancestor_id, descendant_id));
            INSERT INTO {s}.account_category_path SELECT category_id, category_id FROM {s}.account_category;
        END
        IF OBJECT_ID(N'{s}.account_mapping','U') IS NULL
            CREATE TABLE {s}.account_mapping (
                mapping_id            BIGINT IDENTITY(1,1) PRIMARY KEY,
                batch_id              NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                source_row_number     INT NOT NULL,
                account_number          NVARCHAR(450) COLLATE Latin1_General_BIN2 NOT NULL,
                account_name          NVARCHAR(400) COLLATE Latin1_General_BIN2 NULL,
                standardized_category NVARCHAR(40) COLLATE Latin1_General_BIN2 NOT NULL
                    CHECK (standardized_category IN ('Revenue','Receivables','Cash','Receipt in advance','Others')),
                category_id           NVARCHAR(64) COLLATE Latin1_General_BIN2 NULL,
                classification_explicit INT NULL
            );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_account_mapping_number' AND object_id = OBJECT_ID(N'{s}.account_mapping'))
            CREATE UNIQUE INDEX ix_account_mapping_number ON {s}.account_mapping (account_number);

        IF OBJECT_ID(N'{s}.pbc_authorized_preparer','U') IS NULL
            CREATE TABLE {s}.pbc_authorized_preparer (
                batch_id          NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                row_number        BIGINT NOT NULL,
                source_no         INT NOT NULL DEFAULT 1,
                source_row_number INT NOT NULL DEFAULT 0,
                row_json          NVARCHAR(MAX) NOT NULL,
                PRIMARY KEY (batch_id, row_number)
            );

        IF OBJECT_ID(N'{s}.authorized_preparer','U') IS NULL
            CREATE TABLE {s}.authorized_preparer (
                name NVARCHAR(450) COLLATE Latin1_General_BIN2 NOT NULL PRIMARY KEY
            );

        IF OBJECT_ID(N'{s}.message_log','U') IS NULL
            CREATE TABLE {s}.message_log (
                message_id   BIGINT IDENTITY(1,1) PRIMARY KEY,
                occurred_utc NVARCHAR(40) NOT NULL,
                level        NVARCHAR(10) NOT NULL CHECK (level IN ('info','warn','error')),
                text         NVARCHAR(MAX) NOT NULL
            );

        IF OBJECT_ID(N'{s}.operation_log','U') IS NULL
            CREATE TABLE {s}.operation_log (
                event_id       NVARCHAR(32) COLLATE Latin1_General_BIN2 PRIMARY KEY,
                occurred_utc   NVARCHAR(40) COLLATE Latin1_General_BIN2 NOT NULL,
                operation      NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
                target_type    NVARCHAR(32) COLLATE Latin1_General_BIN2 NOT NULL,
                target_id      NVARCHAR(512) COLLATE Latin1_General_BIN2 NOT NULL,
                subject_count  BIGINT NOT NULL CHECK (subject_count >= 0),
                replaced_count BIGINT NOT NULL CHECK (replaced_count >= 0)
            );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_operation_log_occurred' AND object_id = OBJECT_ID(N'{s}.operation_log'))
            CREATE INDEX ix_operation_log_occurred ON {s}.operation_log (occurred_utc, event_id);

        -- schema v9：{s}.operation_log 的 append-only 由引擎強制。INSTEAD OF UPDATE, DELETE
        -- trigger 讓任何改與刪直接 THROW，INSERT 不受影響。CREATE TRIGGER 必須是 batch 內第一個
        -- 敘述，故沿用本檔既有的 EXEC() 動態 DDL 慣例（同 CREATE SCHEMA）。
        IF OBJECT_ID(N'{s}.trg_operation_log_append_only','TR') IS NULL
            EXEC('CREATE TRIGGER {s}.trg_operation_log_append_only ON {s}.operation_log
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 50009, ''project audit log is append-only'', 1;
                END');

        IF OBJECT_ID(N'{s}.gl_control_total','U') IS NULL
            CREATE TABLE {s}.gl_control_total (
                singleton        INT PRIMARY KEY CHECK (singleton = 1),
                pbc_row_count BIGINT NOT NULL,
                stored_row_count BIGINT NOT NULL,
                stored_debit_scaled  BIGINT NOT NULL,
                stored_credit_scaled BIGINT NOT NULL,
                gl_row_count  BIGINT NULL,
                excluded_by_period_count BIGINT NULL,
                excluded_by_posting_status_count BIGINT NULL,
                gl_debit_scaled  BIGINT NULL,
                gl_credit_scaled BIGINT NULL
            );

        IF OBJECT_ID(N'{s}.filter_hit', N'U') IS NULL
            CREATE TABLE {s}.filter_hit (
                scenario_id INT NOT NULL,
                entry_id    BIGINT NOT NULL,
                PRIMARY KEY (scenario_id, entry_id)
            );
        -- 高風險條件彙總查詢的輔助索引，與 SQLite 的 idx_filter_hit_entry 相同：以 entry_id 開頭，
        -- 讓「這筆分錄有沒有命中」與「一段 entry_id 範圍命中了哪些情境」都能走索引；主鍵以 scenario_id 開頭，
        -- 不適合這兩種查詢。先查 sys.indexes 確認索引不存在才建立。
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_filter_hit_entry' AND object_id = OBJECT_ID(N'{s}.filter_hit'))
            CREATE INDEX idx_filter_hit_entry ON {s}.filter_hit (entry_id, scenario_id);

        -- 命中傳票裡每筆分錄符合第幾組第幾條，與 SQLite 的 filter_hit_condition 相同。
        IF OBJECT_ID(N'{s}.filter_hit_condition', N'U') IS NULL
            CREATE TABLE {s}.filter_hit_condition (
                scenario_id  INT NOT NULL,
                entry_id     BIGINT NOT NULL,
                group_no     INT NOT NULL,
                condition_no INT NOT NULL,
                PRIMARY KEY (scenario_id, entry_id, group_no, condition_no)
            );
        """;
}
