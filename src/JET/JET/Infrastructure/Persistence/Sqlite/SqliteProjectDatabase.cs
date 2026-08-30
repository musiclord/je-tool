using System.Data.Common;
using JET.Domain;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure;

/// <summary>
/// 每專案 jet.db 的 SQLite 連線工廠與 schema 初始化——本地引擎家族的 SQLite 實作
/// （<see cref="ILocalProjectDatabase"/>；設計 spec §3 的 SQLite 引擎類，由舊連線工廠更名而來、行為凍結）。
/// 每專案一個 DB 檔，資料表不帶 project_id 欄（檔案即 scope）。
/// </summary>
public sealed class SqliteProjectDatabase(JetProjectFolder folder) : ILocalProjectDatabase
{
    /// <summary>
    /// schema 第 3 版（規則命名更名 + 科目配對，2026-06-11；多來源批次見 guide §3.1.4）。
    /// 新資料庫直接以此建立；第 1/2 版資料庫由 <see cref="EnsureCreatedAsync"/> 的遷移段逐版升級。
    /// staging 的 row_number = 批次內單調遞增排序鍵（INF 抽樣基礎），
    /// source_row_number = 來源檔內實際列號（錯誤定位），source_no 對應 import_batch_source。
    /// </summary>
    private const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS schema_info (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        INSERT INTO schema_info (key, value) VALUES ('schema_version', '6')
            ON CONFLICT(key) DO NOTHING;

        CREATE TABLE IF NOT EXISTS import_batch (
            batch_id         TEXT PRIMARY KEY,
            dataset_kind     TEXT NOT NULL CHECK (dataset_kind IN ('gl','tb','account_mapping')),
            source_file_path TEXT NOT NULL,
            source_file_name TEXT NOT NULL,
            imported_utc     TEXT NOT NULL,
            row_count        INTEGER NOT NULL DEFAULT 0,
            columns_json     TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_import_batch_kind
            ON import_batch (dataset_kind, imported_utc);

        CREATE TABLE IF NOT EXISTS import_batch_source (
            batch_id         TEXT NOT NULL,
            source_no        INTEGER NOT NULL,
            source_file_path TEXT NOT NULL,
            source_file_name TEXT NOT NULL,
            sheet_name       TEXT NULL,
            encoding         TEXT NULL,
            delimiter        TEXT NULL,
            row_count        INTEGER NOT NULL DEFAULT 0,
            imported_utc     TEXT NOT NULL,
            PRIMARY KEY (batch_id, source_no)
        );

        -- Legacy TableDef 等價欄位定義只由 fresh import 建立；IF NOT EXISTS 讓 DDL 冪等，
        -- 不替舊 import_batch 回填或猜測 metadata。獨立的 v5→v6 遷移只補 numeric sort key。
        CREATE TABLE IF NOT EXISTS import_field_definition (
            batch_id            TEXT NOT NULL,
            definition_scope    TEXT NOT NULL CHECK (definition_scope IN ('source','target')),
            ordinal             INTEGER NOT NULL,
            field_name          TEXT NOT NULL,
            description         TEXT NULL,
            field_kind          TEXT NOT NULL CHECK (field_kind IN ('text','number','date','time')),
            text_length         INTEGER NULL,
            decimal_places      INTEGER NULL,
            max_rendered_length INTEGER NOT NULL,
            has_observation     INTEGER NOT NULL CHECK (has_observation IN (0,1)),
            PRIMARY KEY (batch_id, definition_scope, ordinal)
        );

        CREATE TABLE IF NOT EXISTS staging_gl_raw_row (
            batch_id          TEXT NOT NULL,
            row_number        INTEGER NOT NULL,
            source_no         INTEGER NOT NULL DEFAULT 1,
            source_row_number INTEGER NOT NULL DEFAULT 0,
            row_json          TEXT NOT NULL,
            PRIMARY KEY (batch_id, row_number)
        );

        CREATE TABLE IF NOT EXISTS staging_tb_raw_row (
            batch_id          TEXT NOT NULL,
            row_number        INTEGER NOT NULL,
            source_no         INTEGER NOT NULL DEFAULT 1,
            source_row_number INTEGER NOT NULL DEFAULT 0,
            row_json          TEXT NOT NULL,
            PRIMARY KEY (batch_id, row_number)
        );

        CREATE TABLE IF NOT EXISTS config_field_mapping (
            dataset_kind    TEXT PRIMARY KEY CHECK (dataset_kind IN ('gl','tb')),
            mapping_json    TEXT NOT NULL,
            mode_name       TEXT NOT NULL,
            source_batch_id TEXT NOT NULL,
            committed_utc   TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS staging_calendar_raw_day (
            day_type TEXT NOT NULL CHECK (day_type IN ('holiday','makeup')),
            date     TEXT NOT NULL,
            day_name TEXT NULL,
            PRIMARY KEY (day_type, date)
        );

        CREATE TABLE IF NOT EXISTS target_gl_entry (
            entry_id              INTEGER PRIMARY KEY AUTOINCREMENT,
            batch_id              TEXT NOT NULL,
            source_row_number     INTEGER NOT NULL,
            document_number       TEXT NULL,
            line_item             TEXT NULL,
            line_item_numeric_sort_key TEXT NULL,
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
        CREATE INDEX IF NOT EXISTS ix_target_gl_entry_doc
            ON target_gl_entry (document_number);
        CREATE INDEX IF NOT EXISTS ix_target_gl_entry_account
            ON target_gl_entry (account_code);

        CREATE TABLE IF NOT EXISTS target_tb_balance (
            balance_id            INTEGER PRIMARY KEY AUTOINCREMENT,
            batch_id              TEXT NOT NULL,
            source_row_number     INTEGER NOT NULL,
            account_code          TEXT NULL,
            account_name          TEXT NULL,
            change_amount_scaled  INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_target_tb_balance_account
            ON target_tb_balance (account_code);

        CREATE TABLE IF NOT EXISTS result_rule_run (
            run_id        TEXT PRIMARY KEY,
            run_kind      TEXT NOT NULL CHECK (run_kind IN ('validate','prescreen')),
            generated_utc TEXT NOT NULL,
            summary_json  TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_result_rule_run_kind
            ON result_rule_run (run_kind, generated_utc);

        CREATE TABLE IF NOT EXISTS result_inf_sampling_test_sample (
            run_id          TEXT NOT NULL,
            entry_id        INTEGER NOT NULL,
            document_number TEXT NULL,
            line_item       TEXT NULL,
            PRIMARY KEY (run_id, entry_id)
        );

        CREATE TABLE IF NOT EXISTS config_filter_scenario (
            position        INTEGER PRIMARY KEY,
            name            TEXT NOT NULL,
            rationale       TEXT NOT NULL,
            definition_json TEXT NOT NULL,
            saved_utc       TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS staging_account_mapping_raw_row (
            batch_id          TEXT NOT NULL,
            row_number        INTEGER NOT NULL,
            source_no         INTEGER NOT NULL DEFAULT 1,
            source_row_number INTEGER NOT NULL DEFAULT 0,
            row_json          TEXT NOT NULL,
            PRIMARY KEY (batch_id, row_number)
        );

        CREATE TABLE IF NOT EXISTS target_account_mapping (
            mapping_id            INTEGER PRIMARY KEY AUTOINCREMENT,
            batch_id              TEXT NOT NULL,
            source_row_number     INTEGER NOT NULL,
            account_code          TEXT NOT NULL,
            account_name          TEXT NULL,
            standardized_category TEXT NOT NULL
                CHECK (standardized_category IN ('Revenue','Receivables','Cash','Receipt in advance','Others'))
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ix_target_account_mapping_code
            ON target_account_mapping (account_code);

        -- 授權編製人員清單（C 子專案）。單欄姓名集合（name PK）+ 原始列暫存。
        -- 不入 import_batch dataset_kind 體系;加法建表(IF NOT EXISTS)不需 schema 升版,既有資料庫開啟時自動補上。
        CREATE TABLE IF NOT EXISTS staging_authorized_preparer_raw_row (
            batch_id          TEXT NOT NULL,
            row_number        INTEGER NOT NULL,
            source_no         INTEGER NOT NULL DEFAULT 1,
            source_row_number INTEGER NOT NULL DEFAULT 0,
            row_json          TEXT NOT NULL,
            PRIMARY KEY (batch_id, row_number)
        );
        CREATE TABLE IF NOT EXISTS target_authorized_preparer (
            name TEXT NOT NULL PRIMARY KEY
        );

        CREATE INDEX IF NOT EXISTS ix_target_gl_entry_post_date
            ON target_gl_entry (post_date);
        CREATE INDEX IF NOT EXISTS ix_target_gl_entry_approval_date
            ON target_gl_entry (approval_date);

        -- 前端「狀態與訊息」的持久化（manifest log.append / log.recent）。
        -- UX 輔助紀錄,非審計留痕;加法建表(IF NOT EXISTS)不需 schema 升版,既有資料庫開啟時自動補上。
        CREATE TABLE IF NOT EXISTS app_message_log (
            message_id   INTEGER PRIMARY KEY AUTOINCREMENT,
            occurred_utc TEXT NOT NULL,
            level        TEXT NOT NULL CHECK (level IN ('info','warn')),
            text         TEXT NOT NULL
        );

        -- 完整性 part(a) 控制總數（單列;投影時 upsert replace）。
        -- 投影 staging→target 時落地的來源列數、母體列數與借/貸總額,供 validate.run 對上 target 現值。
        -- 加法建表(IF NOT EXISTS)不需 schema 升版,既有資料庫開啟時自動補上（同 app_message_log 先例）。
        CREATE TABLE IF NOT EXISTS gl_control_total (
            singleton        INTEGER PRIMARY KEY CHECK (singleton = 1),
            source_row_count INTEGER NOT NULL,
            target_row_count INTEGER NOT NULL,
            target_debit_scaled  INTEGER NOT NULL,
            target_credit_scaled INTEGER NOT NULL
        );

        -- 進階篩選命中落地（plan 子專案 D1）：filter.commit 把每個已存情境的命中 entry_id
        -- 落地於此，供 query.filterHitsPage keyset 分頁回取（PK 覆蓋 seek）。衍生資料：
        -- 隨 RuleRunResultReset 失效集清除。加法建表（IF NOT EXISTS）不需 schema 升版。
        CREATE TABLE IF NOT EXISTS result_filter_run (
            scenario_position INTEGER NOT NULL,
            entry_id          INTEGER NOT NULL,
            PRIMARY KEY (scenario_position, entry_id)
        );
        -- D2 tag 矩陣即時 pivot 的輔助索引:以 entry_id 為前導鍵,讓
        -- 「EXISTS(result_filter_run WHERE entry_id = g.entry_id)」與「entry_id 鍵範圍取命中位置」走索引
        -- (PK 前導鍵為 scenario_position,不利這兩種存取)。加法、IF NOT EXISTS 不升 schema 版本。
        CREATE INDEX IF NOT EXISTS idx_result_filter_run_entry ON result_filter_run (entry_id, scenario_position);

        -- source_file_path 是 legacy 欄名，載入後不再使用。既有本地案件若曾保存來源機器的
        -- 絕對路徑，開啟時冪等收斂成已持久化的顯示檔名；不升 schema、不動匯入資料列。
        UPDATE import_batch
        SET source_file_path = source_file_name
        WHERE source_file_path <> source_file_name;
        UPDATE import_batch_source
        SET source_file_path = source_file_name
        WHERE source_file_path <> source_file_name;
        """;

    public string GetDatabasePath(string projectId) => folder.GetDatabasePath(projectId);

    /// <inheritdoc />
    public IProviderSqlDialect Dialect => SqliteDialect.Instance;

    /// <summary>SQLite:jet.db 檔是否存在(同名資料夾的唯一性由 IProjectStore.FindAsync 把關,此處為防禦縱深)。
    /// databaseProvider 由介面統一傳入;本實作即 sqlite 路徑,檔案位置由 projectId 唯一決定,不需分流。</summary>
    public Task<bool> DatabaseExistsAsync(string projectId, string databaseProvider, CancellationToken cancellationToken)
        => Task.FromResult(File.Exists(folder.GetDatabasePath(projectId)));

    public DbConnection CreateConnection(string projectId)
    {
        var databasePath = folder.GetDatabasePath(projectId);

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        return new SqliteConnection(builder.ToString());
    }

    /// <summary>
    /// dev 檢視專用的獨立唯讀連線：Mode=ReadOnly、私有快取、不進連線池——
    /// 每次查詢都真實開磁碟檔，保證看到的是已持久化資料且零副作用。
    /// DB 檔不存在時開啟即失敗（不會建檔）。
    /// </summary>
    public DbConnection CreateReadOnlyConnection(string projectId)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = folder.GetDatabasePath(projectId),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };

        return new SqliteConnection(builder.ToString());
    }

    /// <summary>
    /// 匯入連線層調校（guide §3.1.5 規模調校；只作用於匯入連線）：WAL 下 synchronous=NORMAL
    /// 仍保證一致性；temp_store/cache_size 降低大批寫入的 I/O。由本地引擎家族的匯入 repository 於
    /// 連線開啟後、交易開始前呼叫（provider 分支留在 Infrastructure）。
    /// </summary>
    public async Task ApplyImportSessionSettingsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-65536;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>SQLite 批量列寫入＝現行參數化 INSERT 的封裝（行為凍結；見 <see cref="SqliteBulkRowWriter"/>）。</summary>
    public IBulkRowWriter CreateBulkRowWriter(
        DbConnection connection, DbTransaction transaction, string table, IReadOnlyList<string> columns)
        => new SqliteBulkRowWriter(connection, transaction, table, columns);

    /// <summary>
    /// 第 1 版 → 第 2 版的加法遷移（單一 transaction、冪等：版本判斷 + 升版同交易）。
    /// 第 1 版的 row_number 就是來源列號，因此 source_row_number 直接回填 row_number、
    /// 既有批次補一筆來源序號 1 的紀錄——遷移後既有專案的 INF 抽樣排序鍵完全不變。
    /// 不重建表、不動 target；SQL 為 SQL Server 可直譯的加法語句。
    /// </summary>
    private const string MigrateV1ToV2Sql =
        """
        ALTER TABLE staging_gl_raw_row ADD COLUMN source_no INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE staging_gl_raw_row ADD COLUMN source_row_number INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE staging_tb_raw_row ADD COLUMN source_no INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE staging_tb_raw_row ADD COLUMN source_row_number INTEGER NOT NULL DEFAULT 0;
        UPDATE staging_gl_raw_row SET source_row_number = row_number;
        UPDATE staging_tb_raw_row SET source_row_number = row_number;
        INSERT INTO import_batch_source
            (batch_id, source_no, source_file_path, source_file_name, sheet_name, encoding, delimiter, row_count, imported_utc)
        SELECT batch_id, 1, source_file_path, source_file_name, NULL, NULL, NULL, row_count, imported_utc
        FROM import_batch;
        UPDATE schema_info SET value = '2' WHERE key = 'schema_version';
        """;

    /// <summary>
    /// 第 2 版 → 第 3 版（規則命名更名 + 科目配對，2026-06-11）：
    /// 1. 舊鍵（v1–v4 / r1–r8 / descNullCount）儲存的規則執行摘要**清除不翻譯**——
    ///    衍生資料重跑即恢復且結果相同（INF 抽樣 seed 固定）；舊抽樣表一併卸除
    ///    （改建為 result_inf_sampling_test_sample，由基底 schema 建立）。
    /// 2. import_batch 的 dataset_kind CHECK 擴充 'account_mapping'：SQLite 不能
    ///    ALTER CHECK，以重建表搬資料完成（欄序不變）。
    /// 3. 篩選情境（使用者著作的組態）由 C# 段逐鍵翻譯保留，見
    ///    <see cref="TranslateScenarioKeysAsync"/>。
    /// </summary>
    private const string MigrateV2ToV3Sql =
        """
        DELETE FROM result_rule_run;
        DROP TABLE IF EXISTS result_validation_v3_sample;

        CREATE TABLE import_batch_v3 (
            batch_id         TEXT PRIMARY KEY,
            dataset_kind     TEXT NOT NULL CHECK (dataset_kind IN ('gl','tb','account_mapping')),
            source_file_path TEXT NOT NULL,
            source_file_name TEXT NOT NULL,
            imported_utc     TEXT NOT NULL,
            row_count        INTEGER NOT NULL DEFAULT 0,
            columns_json     TEXT NOT NULL
        );
        INSERT INTO import_batch_v3
        SELECT batch_id, dataset_kind, source_file_path, source_file_name, imported_utc, row_count, columns_json
        FROM import_batch;
        DROP TABLE import_batch;
        ALTER TABLE import_batch_v3 RENAME TO import_batch;
        CREATE INDEX IF NOT EXISTS ix_import_batch_kind
            ON import_batch (dataset_kind, imported_utc);

        UPDATE schema_info SET value = '3' WHERE key = 'schema_version';
        """;

    /// <summary>
    /// 第 3 版 → 第 4 版:假日/補班名稱欄(行事曆檔案匯入帶名稱)。加法、冪等。
    /// 註:基底 SchemaSql 對「表尚不存在」的庫(如 v1/v2 凍結快照)已直接建出帶 day_name 的表;
    /// SQLite 無 ADD COLUMN IF NOT EXISTS,故 ALTER 前以 pragma_table_info 守欄是否已存在,
    /// 避免鏈式升級時 duplicate column。版本回填一律執行(冪等)。
    /// </summary>
    private const string AddCalendarDayNameSql =
        "ALTER TABLE staging_calendar_raw_day ADD COLUMN day_name TEXT;";

    private const string BumpToV4Sql =
        "UPDATE schema_info SET value = '4' WHERE key = 'schema_version';";

    private const string CalendarDayNameExistsSql =
        "SELECT COUNT(*) FROM pragma_table_info('staging_calendar_raw_day') WHERE name = 'day_name';";

    /// <summary>
    /// 第 4 版 → 第 5 版:傳票日期欄(回溯過帳偵測 + 日期區間篩選)。加法、冪等。
    /// 註:基底 SchemaSql 對「表尚不存在」的庫已直接建出帶 voucher_date 的表;
    /// SQLite 無 ADD COLUMN IF NOT EXISTS,故 ALTER 前以 pragma_table_info 守欄是否已存在,
    /// 避免鏈式升級時 duplicate column。版本回填一律執行(冪等)。
    /// </summary>
    private const string VoucherDateExistsSql =
        "SELECT COUNT(*) FROM pragma_table_info('target_gl_entry') WHERE name = 'voucher_date';";

    private const string AddVoucherDateSql =
        "ALTER TABLE target_gl_entry ADD COLUMN voucher_date TEXT;";

    private const string BumpToV5Sql =
        "UPDATE schema_info SET value = '5' WHERE key = 'schema_version';";

    /// <summary>
    /// 第 5 版 → 第 6 版：持久化傳票文件項次的 provider-neutral numeric ordinal sort key。
    /// 既有列不從顯示文字猜測型態或回填；numeric step4-1 讀到缺 key 時會 fail closed，
    /// 要求重新匯入／配對／投影。
    /// </summary>
    private const string LineItemNumericSortKeyExistsSql =
        "SELECT COUNT(*) FROM pragma_table_info('target_gl_entry') "
        + "WHERE name = 'line_item_numeric_sort_key';";

    private const string AddLineItemNumericSortKeySql =
        "ALTER TABLE target_gl_entry ADD COLUMN line_item_numeric_sort_key TEXT;";

    private const string BumpToV6Sql =
        "UPDATE schema_info SET value = '6' WHERE key = 'schema_version';";

    private const string BumpToV7Sql =
        "UPDATE schema_info SET value = '7' WHERE key = 'schema_version';";

    private const string CreateV8AuditTableSql =
        """
        CREATE TABLE IF NOT EXISTS audit_event_log (
            event_id       TEXT PRIMARY KEY,
            occurred_utc   TEXT NOT NULL,
            operation      TEXT NOT NULL,
            target_type    TEXT NOT NULL,
            target_id      TEXT NOT NULL,
            subject_count  INTEGER NOT NULL CHECK (subject_count >= 0),
            replaced_count INTEGER NOT NULL CHECK (replaced_count >= 0)
        );
        CREATE INDEX IF NOT EXISTS ix_audit_event_log_occurred
            ON audit_event_log (occurred_utc, event_id);
        """;

    private const string BumpToV8Sql =
        "UPDATE schema_info SET value = '8' WHERE key = 'schema_version';";

    /// <summary>
    /// 第 8 版 → 第 9 版：把 <c>audit_event_log</c> 的 append-only 從程式紀律提升為資料庫層強制。
    /// SQLite 沒有 table-level 權限，引擎機制是 BEFORE UPDATE／BEFORE DELETE trigger 內的
    /// <c>RAISE(ABORT, …)</c>；INSERT 不受影響。只加防護，不動既有列。
    /// </summary>
    private const string CreateV9AuditGuardSql =
        """
        CREATE TRIGGER IF NOT EXISTS trg_audit_event_log_no_update
        BEFORE UPDATE ON audit_event_log
        BEGIN
            SELECT RAISE(ABORT, 'project audit log is append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS trg_audit_event_log_no_delete
        BEFORE DELETE ON audit_event_log
        BEGIN
            SELECT RAISE(ABORT, 'project audit log is append-only');
        END;
        """;

    private const string BumpToV9Sql =
        "UPDATE schema_info SET value = '9' WHERE key = 'schema_version';";

    private const string CreateV7TablesAndIndexesSql =
        """
        CREATE TABLE IF NOT EXISTS config_gl_rde_field (
            field_id      TEXT PRIMARY KEY,
            source_column TEXT NOT NULL,
            label         TEXT NOT NULL,
            value_type    TEXT NOT NULL CHECK (value_type IN ('text','date','money')),
            ordinal       INTEGER NOT NULL,
            is_rde        INTEGER NOT NULL CHECK (is_rde IN (0,1))
        );

        CREATE TABLE IF NOT EXISTS target_gl_rde_value (
            entry_id      INTEGER NOT NULL,
            field_id      TEXT NOT NULL,
            value_type    TEXT NOT NULL CHECK (value_type IN ('text','date','money')),
            text_value    TEXT NULL,
            date_value    TEXT NULL,
            amount_scaled INTEGER NULL,
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
        CREATE INDEX IF NOT EXISTS ix_target_gl_rde_value_text
            ON target_gl_rde_value (field_id, value_type, text_value, entry_id);
        CREATE INDEX IF NOT EXISTS ix_target_gl_rde_value_date
            ON target_gl_rde_value (field_id, value_type, date_value, entry_id);
        CREATE INDEX IF NOT EXISTS ix_target_gl_rde_value_amount
            ON target_gl_rde_value (field_id, value_type, amount_scaled, entry_id);

        CREATE TABLE IF NOT EXISTS config_account_taxonomy (
            category_id  TEXT PRIMARY KEY,
            label        TEXT NOT NULL,
            ordinal      INTEGER NOT NULL UNIQUE,
            semantic_role TEXT NOT NULL,
            is_builtin   INTEGER NOT NULL CHECK (is_builtin IN (0,1)),
            revision     INTEGER NOT NULL
        );
        INSERT INTO config_account_taxonomy
            (category_id, label, ordinal, semantic_role, is_builtin, revision)
        VALUES
            ('builtin.revenue', 'Revenue', 0, 'revenue', 1, 1),
            ('builtin.receivables', 'Receivables', 1, 'receivables', 1, 1),
            ('builtin.cash', 'Cash', 2, 'cash', 1, 1),
            ('builtin.receipt_in_advance', 'Receipt in advance', 3, 'receipt_in_advance', 1, 1),
            ('builtin.others', 'Others', 4, 'others', 1, 1)
        ON CONFLICT(category_id) DO NOTHING;

        CREATE TABLE IF NOT EXISTS config_result_stale_state (
            singleton        INTEGER PRIMARY KEY CHECK (singleton = 1),
            validation_stale INTEGER NOT NULL CHECK (validation_stale IN (0,1)),
            prescreen_stale  INTEGER NOT NULL CHECK (prescreen_stale IN (0,1)),
            filter_stale     INTEGER NOT NULL CHECK (filter_stale IN (0,1))
        );
        INSERT INTO config_result_stale_state
            (singleton, validation_stale, prescreen_stale, filter_stale)
        VALUES (1, 0, 0, 0)
        ON CONFLICT(singleton) DO NOTHING;
        """;

    /// <summary>測試專用：在 v7 data rewrite 後及 result reset 後兩個交易邊界注入 fault。</summary>
    internal Action<string>? MigrationFaultHookForTests { get; set; }

    /// <summary>config_filter_scenario 內 prescreenKey 的舊鍵 → 新 wire key 對照（manifest Prescreen 章節）。</summary>
    private static readonly IReadOnlyDictionary<string, string> PrescreenKeyMigrationMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["r1"] = "postPeriodApproval",
            ["r2"] = "suspiciousKeywords",
            ["r3"] = "unexpectedAccountPair",
            ["r4"] = "trailingZeros",
            ["r7post"] = "weekendPosting",
            ["r7doc"] = "weekendApproval",
            ["r8post"] = "holidayPosting",
            ["r8doc"] = "holidayApproval",
            ["descNull"] = "blankDescription"
        };

    public async Task EnsureCreatedAsync(string projectId, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        // WAL 是資料庫檔案層的持久設定（冪等）：百萬列單交易寫入避免 rollback journal
        // 的雙倍寫放大，replace 模式的大量 DELETE 也因此變廉價（guide §3.1.5 規模調校）
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var version = await ReadVersionAsync(connection, cancellationToken);

        if (version == "1")
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrate = connection.CreateCommand();
            migrate.Transaction = transaction;
            migrate.CommandText = MigrateV1ToV2Sql;
            await migrate.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            version = "2";
        }

        if (version == "2")
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var migrate = connection.CreateCommand())
            {
                migrate.Transaction = transaction;
                migrate.CommandText = MigrateV2ToV3Sql;
                await migrate.ExecuteNonQueryAsync(cancellationToken);
            }

            await TranslateScenarioKeysAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            version = "3";
        }

        if (version == "3")
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using (var exists = connection.CreateCommand())
            {
                exists.Transaction = transaction;
                exists.CommandText = CalendarDayNameExistsSql;
                var hasColumn = Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)) > 0;
                if (!hasColumn)
                {
                    await using var addColumn = connection.CreateCommand();
                    addColumn.Transaction = transaction;
                    addColumn.CommandText = AddCalendarDayNameSql;
                    await addColumn.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await using (var bump = connection.CreateCommand())
            {
                bump.Transaction = transaction;
                bump.CommandText = BumpToV4Sql;
                await bump.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            version = "4";
        }

        if (version == "4")
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using (var exists = connection.CreateCommand())
            {
                exists.Transaction = transaction;
                exists.CommandText = VoucherDateExistsSql;
                var hasColumn = Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)) > 0;
                if (!hasColumn)
                {
                    await using var addColumn = connection.CreateCommand();
                    addColumn.Transaction = transaction;
                    addColumn.CommandText = AddVoucherDateSql;
                    await addColumn.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await using (var bump = connection.CreateCommand())
            {
                bump.Transaction = transaction;
                bump.CommandText = BumpToV5Sql;
                await bump.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            version = "5";
        }

        if (version == "5")
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using (var exists = connection.CreateCommand())
            {
                exists.Transaction = transaction;
                exists.CommandText = LineItemNumericSortKeyExistsSql;
                var hasColumn = Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)) > 0;
                if (!hasColumn)
                {
                    await using var addColumn = connection.CreateCommand();
                    addColumn.Transaction = transaction;
                    addColumn.CommandText = AddLineItemNumericSortKeySql;
                    await addColumn.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await using (var bump = connection.CreateCommand())
            {
                bump.Transaction = transaction;
                bump.CommandText = BumpToV6Sql;
                await bump.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            version = "6";
        }

        if (version == "6")
        {
            await MigrateV6ToV7Async(connection, cancellationToken);
            version = "7";
        }

        if (version == "7")
        {
            await MigrateV7ToV8Async(connection, cancellationToken);
            version = "8";
        }

        if (version == "8")
        {
            await MigrateV8ToV9Async(connection, cancellationToken);
        }
    }

    private static async Task MigrateV8ToV9Async(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var guard = connection.CreateCommand())
        {
            guard.Transaction = transaction;
            guard.CommandText = CreateV9AuditGuardSql;
            await guard.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var bump = connection.CreateCommand())
        {
            bump.Transaction = transaction;
            bump.CommandText = BumpToV9Sql;
            await bump.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MigrateV7ToV8Async(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = CreateV8AuditTableSql;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var bump = connection.CreateCommand())
        {
            bump.Transaction = transaction;
            bump.CommandText = BumpToV8Sql;
            await bump.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task MigrateV6ToV7Async(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await EnsureColumnAsync(
                connection, transaction, "target_gl_entry", "posting_status", "TEXT", cancellationToken);
            await EnsureColumnAsync(
                connection, transaction, "target_gl_entry", "is_effective", "INTEGER", cancellationToken);
            await EnsureColumnAsync(
                connection, transaction, "target_gl_entry", "exclusion_reason", "TEXT", cancellationToken);

            await EnsureColumnAsync(
                connection, transaction, "gl_control_total", "raw_row_count", "INTEGER", cancellationToken);
            await EnsureColumnAsync(
                connection, transaction, "gl_control_total", "effective_row_count", "INTEGER", cancellationToken);
            await EnsureColumnAsync(
                connection, transaction, "gl_control_total", "excluded_by_period_count", "INTEGER", cancellationToken);
            await EnsureColumnAsync(
                connection, transaction, "gl_control_total", "excluded_by_posting_status_count", "INTEGER", cancellationToken);
            await EnsureColumnAsync(
                connection, transaction, "gl_control_total", "effective_debit_scaled", "INTEGER", cancellationToken);
            await EnsureColumnAsync(
                connection, transaction, "gl_control_total", "effective_credit_scaled", "INTEGER", cancellationToken);

            await EnsureColumnAsync(
                connection,
                transaction,
                "config_field_mapping",
                "format_version",
                "INTEGER NOT NULL DEFAULT 1",
                cancellationToken);
            await EnsureColumnAsync(
                connection, transaction, "config_field_mapping", "options_json", "TEXT", cancellationToken);
            await EnsureColumnAsync(
                connection, transaction, "target_account_mapping", "category_id", "TEXT", cancellationToken);

            await using (var create = connection.CreateCommand())
            {
                create.Transaction = transaction;
                create.CommandText = CreateV7TablesAndIndexesSql;
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var backfill = connection.CreateCommand())
            {
                backfill.Transaction = transaction;
                backfill.CommandText =
                    """
                    UPDATE target_account_mapping
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
                await backfill.ExecuteNonQueryAsync(cancellationToken);
            }

            await MigrateScenarioCategoryIdsAsync(
                connection, transaction, schemaPrefix: "", cancellationToken);

            MigrationFaultHookForTests?.Invoke("after-v7-data-rewrite");

            await RuleRunResultReset.ClearWithinAsync(
                connection,
                transaction,
                cancellationToken,
                AuditMutation.SchemaV7Migration);

            MigrationFaultHookForTests?.Invoke("after-v7-result-reset");

            await using (var bump = connection.CreateCommand())
            {
                bump.Transaction = transaction;
                bump.CommandText = BumpToV7Sql;
                await bump.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Preserve the migration failure; disposal remains the final rollback backstop.
            }

            throw;
        }
    }

    private static async Task EnsureColumnAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string column,
        string declaration,
        CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = @column;";
        exists.AddWithValue("@column", column);
        var hasColumn = Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)) > 0;
        if (hasColumn)
        {
            return;
        }

        await using var add = connection.CreateCommand();
        add.Transaction = transaction;
        add.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration};";
        await add.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task MigrateScenarioCategoryIdsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string schemaPrefix,
        CancellationToken cancellationToken)
    {
        var changes = new List<(int Position, string Json)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = $"SELECT position, definition_json FROM {schemaPrefix}config_filter_scenario ORDER BY position;";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var position = reader.GetInt32(0);
                var json = reader.GetString(1);
                var rewritten = ScenarioV7DefinitionMigration.Rewrite(json);
                if (!string.Equals(json, rewritten, StringComparison.Ordinal))
                {
                    changes.Add((position, rewritten));
                }
            }
        }

        foreach (var change in changes)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"UPDATE {schemaPrefix}config_filter_scenario SET definition_json = @json WHERE position = @position;";
            update.AddWithValue("@json", change.Json);
            update.AddWithValue("@position", change.Position);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// 刪除該專案的 jet.db（含 WAL/SHM 邊檔）。只清除此專案的連線池以釋放檔案鎖，
    /// 避免刪除一案時中斷其他專案正在執行的 SQLite 工作。
    /// 資料夾本身由 IProjectStore.DeleteAsync 刪除。
    /// </summary>
    public Task DeleteAsync(string projectId, CancellationToken cancellationToken)
    {
        using (var connection = (SqliteConnection)CreateConnection(projectId))
        {
            SqliteConnection.ClearPool(connection);
        }

        var databasePath = folder.GetDatabasePath(projectId);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        return Task.CompletedTask;
    }

    private static async Task<string?> ReadVersionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var versionQuery = connection.CreateCommand();
        versionQuery.CommandText = "SELECT value FROM schema_info WHERE key = 'schema_version';";
        return (string?)await versionQuery.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>
    /// v2→v3 的篩選情境翻譯：definition_json 內每條 rule 的 prescreenKey 舊鍵改新鍵。
    /// 情境是使用者著作的組態，不可清除；列數 ≤ 5，在 C# 內以 JsonNode 改寫安全且可讀。
    /// 未知鍵保持原樣（讓後續驗證報錯，而非遷移時靜默吞掉）。
    /// </summary>
    private static async Task TranslateScenarioKeysAsync(
        DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        var rows = new List<(long Position, string Json)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT position, definition_json FROM config_filter_scenario;";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        foreach (var (position, json) in rows)
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonObject scenario
                || scenario["groups"] is not System.Text.Json.Nodes.JsonArray groups)
            {
                continue;
            }

            var changed = false;
            foreach (var group in groups)
            {
                if (group?["rules"] is not System.Text.Json.Nodes.JsonArray rules)
                {
                    continue;
                }

                foreach (var rule in rules)
                {
                    var oldKey = rule?["prescreenKey"]?.GetValue<string>();
                    if (oldKey is not null && PrescreenKeyMigrationMap.TryGetValue(oldKey, out var newKey))
                    {
                        rule!["prescreenKey"] = newKey;
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                continue;
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE config_filter_scenario SET definition_json = @json WHERE position = @position;";
            update.AddWithValue("@json", scenario.ToJsonString(JetJsonStorage.Options));
            update.AddWithValue("@position", position);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

internal static class ScenarioV7DefinitionMigration
{
    internal static string Rewrite(string json)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json);
        if (root is null)
        {
            return json;
        }

        var changed = RewriteNode(root);
        return changed ? root.ToJsonString(JetJsonStorage.Options) : json;
    }

    private static bool RewriteNode(System.Text.Json.Nodes.JsonNode node)
    {
        var changed = false;
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            changed |= AddSingletonId(obj, "debitCategory", "debitCategoryIds");
            changed |= AddSingletonId(obj, "creditCategory", "creditCategoryIds");
            foreach (var property in obj.ToList())
            {
                if (property.Value is not null)
                {
                    changed |= RewriteNode(property.Value);
                }
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    changed |= RewriteNode(item);
                }
            }
        }

        return changed;
    }

    private static bool AddSingletonId(
        System.Text.Json.Nodes.JsonObject obj,
        string legacyProperty,
        string idArrayProperty)
    {
        if (obj.ContainsKey(idArrayProperty)
            || obj[legacyProperty] is not System.Text.Json.Nodes.JsonValue value
            || !value.TryGetValue<string>(out var label)
            || !AccountTaxonomyBuiltIns.TryResolveLegacyLabel(label, out var category))
        {
            return false;
        }

        obj[idArrayProperty] = new System.Text.Json.Nodes.JsonArray(
            System.Text.Json.Nodes.JsonValue.Create(category.CategoryId));
        return true;
    }
}
