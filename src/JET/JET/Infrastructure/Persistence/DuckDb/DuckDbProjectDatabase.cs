using System.Data.Common;
using DuckDB.NET.Data;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 每專案 jet.duckdb 的 DuckDB 連線工廠與 schema 初始化——本地引擎家族的第二實作
/// （<see cref="ILocalProjectDatabase"/>）。與 <see cref="SqliteProjectDatabase"/> 同屬
/// 「一資料夾一專案、檔案即 scope」模型，共用同一套 <c>Local*</c> repository 與 SQL 文本；引擎差異
/// 全部收進本類（連線工廠＋DDL）與 <see cref="DuckDbConnectionAdapter"/>（@name→$name 參數轉接）＋
/// <see cref="DuckDbDialect"/>。發出的連線一律經轉接器包裝，故共用 repo 的 <c>@p{i}</c> SQL 無需改動。
/// </summary>
public sealed class DuckDbProjectDatabase(JetProjectFolder folder) : ILocalProjectDatabase, IProjectDatabaseRetention
{
    /// <summary>DuckDB 資料庫檔名（與 sqlite 的 jet.db 並列於同一 projects/{id}/ 資料夾）。</summary>
    public const string DatabaseFileName = "jet.duckdb";

    /// <summary>
    /// DuckDB 版 schema——新檔一次到位建第 6 版（與 <see cref="SqliteProjectDatabase"/> 的
    /// 第 6 版形狀對齊：表／欄／索引名一字不差），再逐版升到現行版。第 1 到第 5 版的舊檔不再升版，
    /// 開啟時直接回報錯誤。
    /// 引擎差異只三類：(1) SQLite <c>INTEGER PRIMARY KEY AUTOINCREMENT</c> → DuckDB
    /// <c>BIGINT PRIMARY KEY DEFAULT nextval('seq_&lt;表&gt;')</c>＋前置 <c>CREATE SEQUENCE IF NOT EXISTS</c>
    /// （DuckDB 無 AUTOINCREMENT）；(2) 型別採 SqlServer 家族已驗證的跨引擎映射——金額／計數／
    /// row_number／entry_id 等以 BIGINT（DuckDB INTEGER 為 32 位，scaled 金額會溢位），小整數（source_no／
    /// is_manual／position 等）以 INTEGER；TEXT 保留（DuckDB TEXT≡VARCHAR）；(3) schema_version 冪等插入
    /// 用 ANSI <c>WHERE NOT EXISTS</c>（不依賴 SQLite 專屬 ON CONFLICT 形）。<c>IF NOT EXISTS</c> 全程冪等。
    /// </summary>
    private const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS schema_info (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        INSERT INTO schema_info (key, value)
            SELECT 'schema_version', '6'
            WHERE NOT EXISTS (SELECT 1 FROM schema_info WHERE key = 'schema_version');

        INSERT INTO schema_info (key, value)
            SELECT 'filter_data_revision', '0'
            WHERE NOT EXISTS (SELECT 1 FROM schema_info WHERE key = 'filter_data_revision');

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

        -- Legacy TableDef 等價欄位定義只由 fresh import 建立；不替舊 batch
        -- 回填或猜測 metadata。獨立的 v5→v6 遷移只補 numeric sort key。
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
            row_number        BIGINT NOT NULL,
            source_no         INTEGER NOT NULL DEFAULT 1,
            source_row_number INTEGER NOT NULL DEFAULT 0,
            row_json          TEXT NOT NULL,
            PRIMARY KEY (batch_id, row_number)
        );

        CREATE TABLE IF NOT EXISTS staging_tb_raw_row (
            batch_id          TEXT NOT NULL,
            row_number        BIGINT NOT NULL,
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

        -- 重新匯入前最後一次確認的配對，只用來在重開案件時帶回草稿（LocalMappingStateStore.RetireCommittedMappingSql）。
        CREATE TABLE IF NOT EXISTS config_field_mapping_previous (
            dataset_kind    TEXT PRIMARY KEY CHECK (dataset_kind IN ('gl','tb')),
            mapping_json    TEXT NOT NULL,
            mode_name       TEXT NOT NULL,
            source_batch_id TEXT NOT NULL,
            committed_utc   TEXT NOT NULL,
            format_version  INTEGER NOT NULL,
            options_json    TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS staging_calendar_raw_day (
            day_type TEXT NOT NULL CHECK (day_type IN ('holiday','makeup')),
            date     TEXT NOT NULL,
            day_name TEXT NULL,
            PRIMARY KEY (day_type, date)
        );

        CREATE SEQUENCE IF NOT EXISTS seq_target_gl_entry;
        CREATE TABLE IF NOT EXISTS target_gl_entry (
            entry_id              BIGINT PRIMARY KEY DEFAULT nextval('seq_target_gl_entry'),
            batch_id              TEXT NOT NULL,
            source_row_number     BIGINT NOT NULL,
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
            amount_scaled         BIGINT NOT NULL,
            debit_amount_scaled   BIGINT NOT NULL,
            credit_amount_scaled  BIGINT NOT NULL,
            dr_cr                 TEXT NOT NULL CHECK (dr_cr IN ('DEBIT','CREDIT'))
        );
        CREATE INDEX IF NOT EXISTS ix_target_gl_entry_doc
            ON target_gl_entry (document_number);
        CREATE INDEX IF NOT EXISTS ix_target_gl_entry_account
            ON target_gl_entry (account_code);
        CREATE INDEX IF NOT EXISTS ix_target_gl_entry_post_date
            ON target_gl_entry (post_date);
        CREATE INDEX IF NOT EXISTS ix_target_gl_entry_approval_date
            ON target_gl_entry (approval_date);

        CREATE SEQUENCE IF NOT EXISTS seq_target_tb_balance;
        CREATE TABLE IF NOT EXISTS target_tb_balance (
            balance_id            BIGINT PRIMARY KEY DEFAULT nextval('seq_target_tb_balance'),
            batch_id              TEXT NOT NULL,
            source_row_number     BIGINT NOT NULL,
            account_code          TEXT NULL,
            account_name          TEXT NULL,
            change_amount_scaled  BIGINT NOT NULL
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
            entry_id        BIGINT NOT NULL,
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
            row_number        BIGINT NOT NULL,
            source_no         INTEGER NOT NULL DEFAULT 1,
            source_row_number INTEGER NOT NULL DEFAULT 0,
            row_json          TEXT NOT NULL,
            PRIMARY KEY (batch_id, row_number)
        );

        CREATE SEQUENCE IF NOT EXISTS seq_target_account_mapping;
        CREATE TABLE IF NOT EXISTS target_account_mapping (
            mapping_id            BIGINT PRIMARY KEY DEFAULT nextval('seq_target_account_mapping'),
            batch_id              TEXT NOT NULL,
            source_row_number     INTEGER NOT NULL,
            -- account_code 唯一性以 inline UNIQUE 宣告（非 SQLite 的分離 CREATE UNIQUE INDEX）：DuckDB 對
            -- 「分離唯一索引」在同交易內 DELETE 後重插同鍵會誤報 Duplicate key（本機探針實證：分離唯一索引失敗、
            -- inline UNIQUE／PRIMARY KEY 正確）。account.mapping 的 replace 重匯入正是此形（同交易清 target 後重插）。
            -- inline UNIQUE 語意等同 SQLite ix_target_account_mapping_code（唯一性 + account_code 索引）。
            account_code          TEXT NOT NULL UNIQUE,
            account_name          TEXT NULL,
            standardized_category TEXT NOT NULL
                CHECK (standardized_category IN ('Revenue','Receivables','Cash','Receipt in advance','Others'))
        );

        CREATE TABLE IF NOT EXISTS staging_authorized_preparer_raw_row (
            batch_id          TEXT NOT NULL,
            row_number        BIGINT NOT NULL,
            source_no         INTEGER NOT NULL DEFAULT 1,
            source_row_number INTEGER NOT NULL DEFAULT 0,
            row_json          TEXT NOT NULL,
            PRIMARY KEY (batch_id, row_number)
        );
        CREATE TABLE IF NOT EXISTS target_authorized_preparer (
            name TEXT NOT NULL PRIMARY KEY
        );

        CREATE SEQUENCE IF NOT EXISTS seq_app_message_log;
        CREATE TABLE IF NOT EXISTS app_message_log (
            message_id   BIGINT PRIMARY KEY DEFAULT nextval('seq_app_message_log'),
            occurred_utc TEXT NOT NULL,
            level        TEXT NOT NULL CHECK (level IN ('info','warn')),
            text         TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS gl_control_total (
            singleton        INTEGER PRIMARY KEY CHECK (singleton = 1),
            source_row_count BIGINT NOT NULL,
            target_row_count BIGINT NOT NULL,
            target_debit_scaled  BIGINT NOT NULL,
            target_credit_scaled BIGINT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS result_filter_run (
            scenario_position INTEGER NOT NULL,
            entry_id          BIGINT NOT NULL,
            PRIMARY KEY (scenario_position, entry_id)
        );
        CREATE INDEX IF NOT EXISTS idx_result_filter_run_entry
            ON result_filter_run (entry_id, scenario_position);
        """;

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
            entry_id      BIGINT NOT NULL,
            field_id      TEXT NOT NULL,
            value_type    TEXT NOT NULL CHECK (value_type IN ('text','date','money')),
            text_value    TEXT NULL,
            date_value    TEXT NULL,
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
        CREATE INDEX IF NOT EXISTS ix_target_gl_rde_value_text
            ON target_gl_rde_value (field_id, value_type, text_value, entry_id);
        CREATE INDEX IF NOT EXISTS ix_target_gl_rde_value_date
            ON target_gl_rde_value (field_id, value_type, date_value, entry_id);
        CREATE INDEX IF NOT EXISTS ix_target_gl_rde_value_amount
            ON target_gl_rde_value (field_id, value_type, amount_scaled, entry_id);

        CREATE TABLE IF NOT EXISTS config_account_taxonomy (
            category_id   TEXT PRIMARY KEY,
            label         TEXT NOT NULL,
            ordinal       INTEGER NOT NULL UNIQUE,
            semantic_role TEXT NOT NULL,
            is_builtin    INTEGER NOT NULL CHECK (is_builtin IN (0,1)),
            revision      INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS config_result_stale_state (
            singleton        INTEGER PRIMARY KEY CHECK (singleton = 1),
            validation_stale INTEGER NOT NULL CHECK (validation_stale IN (0,1)),
            prescreen_stale  INTEGER NOT NULL CHECK (prescreen_stale IN (0,1)),
            filter_stale     INTEGER NOT NULL CHECK (filter_stale IN (0,1))
        );

        """;

    private const string SeedV7RowsSql =
        """
        INSERT INTO config_account_taxonomy (category_id, label, ordinal, semantic_role, is_builtin, revision)
            SELECT 'builtin.revenue', 'Revenue', 0, 'revenue', 1, 1
            WHERE NOT EXISTS (SELECT 1 FROM config_account_taxonomy WHERE category_id = 'builtin.revenue');
        INSERT INTO config_account_taxonomy (category_id, label, ordinal, semantic_role, is_builtin, revision)
            SELECT 'builtin.receivables', 'Receivables', 1, 'receivables', 1, 1
            WHERE NOT EXISTS (SELECT 1 FROM config_account_taxonomy WHERE category_id = 'builtin.receivables');
        INSERT INTO config_account_taxonomy (category_id, label, ordinal, semantic_role, is_builtin, revision)
            SELECT 'builtin.cash', 'Cash', 2, 'cash', 1, 1
            WHERE NOT EXISTS (SELECT 1 FROM config_account_taxonomy WHERE category_id = 'builtin.cash');
        INSERT INTO config_account_taxonomy (category_id, label, ordinal, semantic_role, is_builtin, revision)
            SELECT 'builtin.receipt_in_advance', 'Receipt in advance', 3, 'receipt_in_advance', 1, 1
            WHERE NOT EXISTS (SELECT 1 FROM config_account_taxonomy WHERE category_id = 'builtin.receipt_in_advance');
        INSERT INTO config_account_taxonomy (category_id, label, ordinal, semantic_role, is_builtin, revision)
            SELECT 'builtin.others', 'Others', 4, 'others', 1, 1
            WHERE NOT EXISTS (SELECT 1 FROM config_account_taxonomy WHERE category_id = 'builtin.others');
        INSERT INTO config_result_stale_state
            SELECT 1, 0, 0, 0
            WHERE NOT EXISTS (SELECT 1 FROM config_result_stale_state WHERE singleton = 1);
        """;

    private const string CreateV8AuditTableSql =
        """
        CREATE TABLE IF NOT EXISTS audit_event_log (
            event_id       TEXT PRIMARY KEY,
            occurred_utc   TEXT NOT NULL,
            operation      TEXT NOT NULL,
            target_type    TEXT NOT NULL,
            target_id      TEXT NOT NULL,
            subject_count  BIGINT NOT NULL CHECK (subject_count >= 0),
            replaced_count BIGINT NOT NULL CHECK (replaced_count >= 0)
        );
        CREATE INDEX IF NOT EXISTS ix_audit_event_log_occurred
            ON audit_event_log (occurred_utc, event_id);
        """;

    /// <summary>測試專用：在 v7 data rewrite 後及 result reset 後兩個交易邊界注入 fault。</summary>
    internal Action<string>? MigrationFaultHookForTests { get; set; }

    /// <summary>該專案 jet.duckdb 檔的實體路徑（與 sqlite 同資料夾、不同檔名）。</summary>
    public string GetDatabasePath(string projectId)
        => Path.Combine(folder.GetProjectDirectory(projectId), DatabaseFileName);

    /// <inheritdoc />
    public IProviderSqlDialect Dialect => DuckDbDialect.Instance;

    /// <summary>DuckDB：jet.duckdb 檔是否存在（同名資料夾唯一性由 IProjectStore 把關，此為防禦縱深）。
    /// databaseProvider 由介面統一傳入；本實作即 duckdb 路徑，檔案位置由 projectId 唯一決定，不需分流。</summary>
    public Task<bool> DatabaseExistsAsync(string projectId, string databaseProvider, CancellationToken cancellationToken)
        => Task.FromResult(File.Exists(GetDatabasePath(projectId)));

    public DbConnection CreateConnection(string projectId)
    {
        var databasePath = GetDatabasePath(projectId);

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 轉接器包裝：發出的每個命令在執行前把 @name→$name、參數名剝為裸名（DuckDB 綁定要求）。
        return new DuckDbConnectionAdapter(new DuckDBConnection(ConnectionString(databasePath)));
    }

    /// <summary>
    /// 一般讀寫連線的連線字串。DuckDB.NET 在同一程序內把 DataSource 相同的連線接到同一個資料庫實體，
    /// 實體只在第一次開啟時套用設定，所以持有連線與 repository 連線必須使用完全相同的字串。
    /// </summary>
    private static string ConnectionString(string databasePath) => $"DataSource={databasePath}";

    private readonly Lock retentionGate = new();

    /// <summary>每個資料庫路徑目前有幾個持有者；路徑大小寫不敏感，與 Windows 檔案系統一致。</summary>
    private readonly Dictionary<string, int> retainers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 在一次操作期間保持 jet.duckdb 開啟：開一條連線並持有到操作結束，期間 repository 開的連線都沿用
    /// 同一個資料庫實體，不必每次重新開啟。資料庫檔不存在時不持有，避免替缺檔的案件建出新檔；
    /// SQLite 案件的資料夾沒有 jet.duckdb，因此自然不持有。開啟失敗回 null，讓 repository 照原本的方式報錯。
    /// </summary>
    public IDisposable? TryRetain(string projectId)
    {
        string databasePath;
        try
        {
            databasePath = GetDatabasePath(projectId);
        }
        catch (Exception)
        {
            return null;
        }

        // 先登記再開啟：登記與檔案檢查和 DeleteAsync 用同一把鎖，刪除不會插進「已確認檔案存在、
        // 尚未開啟」之間。
        lock (retentionGate)
        {
            if (!File.Exists(databasePath))
            {
                return null;
            }

            retainers[databasePath] = retainers.GetValueOrDefault(databasePath) + 1;
        }

        DuckDBConnection? connection = null;
        try
        {
            connection = new DuckDBConnection(ConnectionString(databasePath));
            connection.Open();
            return new DatabaseRetention(this, databasePath, connection);
        }
        catch (Exception)
        {
            try
            {
                connection?.Dispose();
            }
            catch (Exception)
            {
                // 開啟失敗後的清理失敗不影響結果：照樣不持有。
            }

            ReleaseRetainer(databasePath);
            return null;
        }
    }

    private void ReleaseRetainer(string databasePath)
    {
        lock (retentionGate)
        {
            if (!retainers.TryGetValue(databasePath, out var count))
            {
                return;
            }

            if (count <= 1)
            {
                retainers.Remove(databasePath);
            }
            else
            {
                retainers[databasePath] = count - 1;
            }
        }
    }

    private sealed class DatabaseRetention(
        DuckDbProjectDatabase owner,
        string databasePath,
        DuckDBConnection connection) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            try
            {
                // 先關連線再撤登記：刪除檢查看到沒有持有者時，這條連線一定已經關閉。
                connection.Dispose();
            }
            catch (Exception)
            {
                // 釋放失敗不得遮蔽操作本身的結果。
            }
            finally
            {
                owner.ReleaseRetainer(databasePath);
            }
        }
    }

    /// <summary>
    /// dev 檢視專用的獨立唯讀連線：<c>ACCESS_MODE=READ_ONLY</c>（實測可與主連線並存，讀到 MVCC 快照）。
    /// DB 檔不存在時開啟即失敗（不建檔）。同樣經轉接器包裝以維持 dev 檢視查詢的參數語意一致。
    /// </summary>
    public DbConnection CreateReadOnlyConnection(string projectId)
        => new DuckDbConnectionAdapter(
            new DuckDBConnection($"DataSource={GetDatabasePath(projectId)};ACCESS_MODE=READ_ONLY"));

    /// <summary>
    /// 匯入連線層調校：DuckDB 自管 MVCC 與寫入路徑，SQLite 的 WAL/synchronous/temp_store/cache_size
    /// pragmas 不適用——no-op。效能修法採原生 Appender（見 <see cref="DuckDbBulkRowWriter"/>）：
    /// 逐列 INSERT 的計畫成本被繞過，此為主修法。<c>SET preserve_insertion_order=false</c> 經評估未加——
    /// 它 session-scoped 只作用於匯入連線本身，而落地投影／查詢在各自的新連線（預設 preserve_insertion_order=true），
    /// 故對本連線以外的讀取序無效；要全域套用需先審計每條讀取皆具顯式 ORDER BY，超出這次修改的保守範圍。
    /// </summary>
    public Task ApplyImportSessionSettingsAsync(DbConnection connection, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>DuckDB 批量列寫入＝原生 Appender（見 <see cref="DuckDbBulkRowWriter"/>）。</summary>
    public IBulkRowWriter CreateBulkRowWriter(
        DbConnection connection, DbTransaction transaction, string table, IReadOnlyList<string> columns)
        => new DuckDbBulkRowWriter(connection, transaction, table, columns);

    /// <summary>
    /// 新檔一次到位建第 6 版再逐版升級；第 1 到第 5 版的舊檔在任何寫入之前就擋下。
    /// DuckDB 支援單一命令內多語句，基底 DDL 的 <c>IF NOT EXISTS</c> 可重複執行。
    /// </summary>
    private readonly LocalSchemaReadiness readiness = new();

    /// <summary>建立或載入案件時呼叫：一律完整檢查建表與升版。</summary>
    public Task EnsureCreatedAsync(string projectId, CancellationToken cancellationToken) =>
        readiness.EnsureCreatedAsync(
            GetDatabasePath(projectId),
            ct => EnsureCreatedCoreAsync(projectId, ct),
            cancellationToken);

    /// <summary>repository 每次讀寫前呼叫：同一程序內每個資料庫檔只完整檢查一次。</summary>
    public Task EnsureReadyAsync(string projectId, CancellationToken cancellationToken) =>
        readiness.EnsureReadyAsync(
            GetDatabasePath(projectId),
            ct => EnsureCreatedCoreAsync(projectId, ct),
            cancellationToken);

    private async Task EnsureCreatedCoreAsync(string projectId, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        // 舊版 JET 建立的資料庫（第 1 到第 5 版）與版本資訊無法辨識的資料庫，在任何寫入之前就擋下，檔案保持原狀。
        LocalSchemaReadiness.RejectUnsupportedSchemaVersion(
            await ReadExistingVersionAsync(connection, cancellationToken));

        await using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText =
            "SELECT value FROM schema_info WHERE key = 'schema_version';";
        var version = await versionCommand.ExecuteScalarAsync(cancellationToken) as string;
        var parsedVersion = int.TryParse(version, out var value) ? value : 0;
        if (parsedVersion == 6)
        {
            await MigrateV6ToV7Async(connection, cancellationToken);
            parsedVersion = 7;
        }

        if (parsedVersion == 7)
        {
            await MigrateV7ToV8Async(connection, cancellationToken);
            parsedVersion = 8;
        }

        if (parsedVersion == 8)
        {
            await MigrateV8ToV9Async(connection, cancellationToken);
            parsedVersion = 9;
        }
        if (parsedVersion == 9)
        {
            await AccountClassificationMigration.UpgradeLocalAsync(connection, cancellationToken);
            parsedVersion = 10;
        }
        if (parsedVersion == 10)
            await AccountTaxonomyHierarchy.UpgradeLocalAsync(connection, cancellationToken);
    }

    /// <summary>
    /// 第 8 版 → 第 9 版：SQLite 與 SQL Server 在這一版把 <c>audit_event_log</c> 的 append-only
    /// 交由引擎強制（trigger）。DuckDB 1.5.3 沒有 trigger、也沒有 table-level 權限，因此本版對
    /// DuckDB 只推進版本號以維持三 provider 的版本鏈一致；append-only 在 DuckDB 仍是程式紀律，
    /// 由 <c>LocalProjectAuditLog</c> 只寫 INSERT 與來源守衛把關（見 docs/jet-guide.md 第 8 節「儲存與資料庫實作」）。
    /// </summary>
    private static async Task MigrateV8ToV9Async(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var bump = connection.CreateCommand())
        {
            bump.Transaction = transaction;
            bump.CommandText = "UPDATE schema_info SET value = '9' WHERE key = 'schema_version';";
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
            bump.CommandText = "UPDATE schema_info SET value = '8' WHERE key = 'schema_version';";
            await bump.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>尚未建表的新資料庫回傳 null；只讀取，不建立任何資料表。</summary>
    private static async Task<string?> ReadExistingVersionAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText =
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'schema_info';";
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                return null;
            }
        }

        await using var version = connection.CreateCommand();
        version.CommandText = "SELECT value FROM schema_info WHERE key = 'schema_version';";
        return await version.ExecuteScalarAsync(cancellationToken) as string;
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
                connection, transaction, "gl_control_total", "raw_row_count", "BIGINT", cancellationToken);
            await EnsureColumnAsync(
                connection, transaction, "gl_control_total", "effective_row_count", "BIGINT", cancellationToken);
            await EnsureColumnAsync(
                connection, transaction, "gl_control_total", "excluded_by_period_count", "BIGINT", cancellationToken);
            await EnsureColumnAsync(
                connection, transaction, "gl_control_total", "excluded_by_posting_status_count", "BIGINT", cancellationToken);
            await EnsureColumnAsync(
                connection, transaction, "gl_control_total", "effective_debit_scaled", "BIGINT", cancellationToken);
            await EnsureColumnAsync(
                connection, transaction, "gl_control_total", "effective_credit_scaled", "BIGINT", cancellationToken);

            await EnsureColumnAsync(
                connection, transaction, "target_account_mapping", "category_id", "TEXT", cancellationToken);

            var mappingHasFormatVersion = await ColumnExistsAsync(
                connection, transaction, "config_field_mapping", "format_version", cancellationToken);
            var mappingHasOptionsJson = await ColumnExistsAsync(
                connection, transaction, "config_field_mapping", "options_json", cancellationToken);

            await using (var prepareMappingV7 = connection.CreateCommand())
            {
                prepareMappingV7.Transaction = transaction;
                // DuckDB 的 ADD COLUMN 不接受 NOT NULL/DEFAULT，且 data update 後不可建立
                // constraint backing index；先清除 retry 留下的 scratch table，再建立完整 replacement
                // shape，待所有 v7 indexes 建好後才搬資料與原子換名。canonical source table
                // 已存在的 v2 欄位和值會在下面動態保留；真 frozen v6 才補 1/NULL。
                prepareMappingV7.CommandText =
                    """
                    DROP TABLE IF EXISTS config_field_mapping_v7;
                    CREATE TABLE config_field_mapping_v7 (
                        dataset_kind    TEXT PRIMARY KEY CHECK (dataset_kind IN ('gl','tb')),
                        mapping_json    TEXT NOT NULL,
                        mode_name       TEXT NOT NULL,
                        source_batch_id TEXT NOT NULL,
                        committed_utc   TEXT NOT NULL,
                        format_version  INTEGER NOT NULL DEFAULT 1,
                        options_json    TEXT NULL
                    );
                    """;
                await prepareMappingV7.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var create = connection.CreateCommand())
            {
                create.Transaction = transaction;
                create.CommandText = CreateV7TablesAndIndexesSql;
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var replaceMappingAndSeed = connection.CreateCommand())
            {
                replaceMappingAndSeed.Transaction = transaction;
                var formatVersionSource = mappingHasFormatVersion
                    ? "COALESCE(format_version, 1)"
                    : "1";
                var optionsJsonSource = mappingHasOptionsJson ? "options_json" : "NULL";
                replaceMappingAndSeed.CommandText =
                    $"""
                    INSERT INTO config_field_mapping_v7
                        (dataset_kind, mapping_json, mode_name, source_batch_id, committed_utc,
                         format_version, options_json)
                    SELECT dataset_kind, mapping_json, mode_name, source_batch_id, committed_utc,
                           {formatVersionSource}, {optionsJsonSource}
                    FROM config_field_mapping;
                    DROP TABLE config_field_mapping;
                    ALTER TABLE config_field_mapping_v7 RENAME TO config_field_mapping;
                    """
                    + SeedV7RowsSql;
                await replaceMappingAndSeed.ExecuteNonQueryAsync(cancellationToken);
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

            await RuleRunResultReset.ClearWithinAsync(
                connection,
                transaction,
                cancellationToken,
                AuditMutation.SchemaV7Migration);

            MigrationFaultHookForTests?.Invoke("after-v7-result-reset");

            await using (var bump = connection.CreateCommand())
            {
                bump.Transaction = transaction;
                bump.CommandText = "UPDATE schema_info SET value = '7' WHERE key = 'schema_version';";
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
        if (await ColumnExistsAsync(connection, transaction, table, column, cancellationToken))
        {
            return;
        }

        await using var add = connection.CreateCommand();
        add.Transaction = transaction;
        add.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration};";
        await add.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ColumnExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText =
            $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = @column;";
        exists.AddWithValue("@column", column);
        return Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    /// <summary>
    /// 刪除該專案的 jet.duckdb（含 DuckDB 交易期的 .wal 邊檔若殘留）。資料夾本身由
    /// IProjectStore.DeleteAsync 刪除。DuckDB.NET 無 SQLite 的 ClearAllPools——同一路徑的最後一條連線
    /// 關閉時資料庫實體才關閉並解鎖。
    /// 實測資料庫實體開著時刪檔也會成功，之後同一路徑的新連線仍沿用舊實體、讀到舊資料；使用者刪掉案件後
    /// 再建同名案件就會讀到舊案件的資料。因此該路徑仍有持有者（<see cref="TryRetain"/>）時，
    /// 回 operation_in_progress 而且不刪任何檔案。
    /// </summary>
    public Task DeleteAsync(string projectId, CancellationToken cancellationToken)
    {
        var databasePath = GetDatabasePath(projectId);
        lock (retentionGate)
        {
            if (retainers.ContainsKey(databasePath))
            {
                throw new JetActionException(
                    JetErrorCodes.OperationInProgress,
                    "案件資料庫還有作業在使用，請等作業完成後再刪除案件。");
            }

            readiness.Forget(databasePath);
            foreach (var suffix in new[] { "", ".wal" })
            {
                TryDeleteFile(databasePath + suffix);
            }
        }

        return Task.CompletedTask;
    }

    private static void TryDeleteFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 資料庫實體開著時刪檔仍會成功（見 DeleteAsync），所以這裡擋不住「實體還開著」的情況，
            // 那由 DeleteAsync 的持有者檢查負責。走到這裡是其他程式占用檔案；
            // 催動 finalizer 釋放可能殘留的 handle 後重試一次，仍失敗就把例外交給呼叫端。
            GC.Collect();
            GC.WaitForPendingFinalizers();
            File.Delete(path);
        }
    }
}
